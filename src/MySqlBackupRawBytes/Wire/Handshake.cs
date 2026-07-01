using System;
using System.Security.Cryptography;
using System.Text;

namespace MySqlBackup.NET.RawBytes.Wire
{
    internal class HandshakeV10
    {
        public string ServerVersion;
        public uint ConnectionId;
        public byte[] AuthPluginData;   // 20-byte seed (concatenated parts, trailing NUL stripped)
        public uint CapabilityFlags;
        public byte CharacterSet;
        public ushort StatusFlags;
        public string AuthPluginName;

        public static HandshakeV10 Parse(byte[] payload)
        {
            var r = new PayloadReader(payload);
            byte protocol = r.ReadByte();
            if (protocol != 10)
                throw new NotSupportedException("Unsupported handshake protocol version " + protocol);

            var h = new HandshakeV10();
            h.ServerVersion = Encoding.ASCII.GetString(r.ReadNullTerminatedBytes());
            h.ConnectionId = r.ReadUInt32();
            byte[] seedPart1 = r.ReadBytes(8);
            r.Skip(1); // filler 0x00
            ushort capsLow = r.ReadUInt16();
            h.CharacterSet = r.ReadByte();
            h.StatusFlags = r.ReadUInt16();
            ushort capsHigh = r.ReadUInt16();
            h.CapabilityFlags = (uint)capsLow | ((uint)capsHigh << 16);

            byte authLen = 0;
            if ((h.CapabilityFlags & Caps.PLUGIN_AUTH) != 0)
                authLen = r.ReadByte();
            else
                r.Skip(1);
            r.Skip(10); // reserved zeros

            int part2Len = Math.Max(13, authLen - 8);
            byte[] seedPart2 = r.ReadBytes(part2Len);
            int p2Effective = seedPart2.Length;
            if (p2Effective > 0 && seedPart2[p2Effective - 1] == 0) p2Effective--;

            h.AuthPluginData = new byte[8 + p2Effective];
            Buffer.BlockCopy(seedPart1, 0, h.AuthPluginData, 0, 8);
            Buffer.BlockCopy(seedPart2, 0, h.AuthPluginData, 8, p2Effective);

            if ((h.CapabilityFlags & Caps.PLUGIN_AUTH) != 0)
                h.AuthPluginName = Encoding.ASCII.GetString(r.ReadNullTerminatedBytes());

            return h;
        }
    }

    internal static class AuthScramble
    {
        // mysql_native_password: SHA1(pw) XOR SHA1(seed + SHA1(SHA1(pw)))
        public static byte[] NativePassword(string password, byte[] seed)
        {
            if (string.IsNullOrEmpty(password)) return new byte[0];
            using (var sha = SHA1.Create())
            {
                byte[] pw = Encoding.UTF8.GetBytes(password);
                byte[] h1 = sha.ComputeHash(pw);
                byte[] h2 = sha.ComputeHash(h1);
                byte[] concat = new byte[seed.Length + h2.Length];
                Buffer.BlockCopy(seed, 0, concat, 0, seed.Length);
                Buffer.BlockCopy(h2, 0, concat, seed.Length, h2.Length);
                byte[] h3 = sha.ComputeHash(concat);
                byte[] res = new byte[h1.Length];
                for (int i = 0; i < h1.Length; i++) res[i] = (byte)(h1[i] ^ h3[i]);
                return res;
            }
        }

        // caching_sha2_password fast-auth scramble:
        //   SHA256(pw) XOR SHA256(SHA256(SHA256(pw)) + seed)
        public static byte[] CachingSha2(string password, byte[] seed)
        {
            if (string.IsNullOrEmpty(password)) return new byte[0];
            using (var sha = SHA256.Create())
            {
                byte[] pw = Encoding.UTF8.GetBytes(password);
                byte[] h1 = sha.ComputeHash(pw);
                byte[] h2 = sha.ComputeHash(h1);
                byte[] concat = new byte[h2.Length + seed.Length];
                Buffer.BlockCopy(h2, 0, concat, 0, h2.Length);
                Buffer.BlockCopy(seed, 0, concat, h2.Length, seed.Length);
                byte[] h3 = sha.ComputeHash(concat);
                byte[] res = new byte[h1.Length];
                for (int i = 0; i < h1.Length; i++) res[i] = (byte)(h1[i] ^ h3[i]);
                return res;
            }
        }

        // caching_sha2_password full-auth (no TLS): RSA-OAEP(SHA1) of XOR(pw+\0, seed-repeated) using server pubkey
        public static byte[] EncryptPasswordWithPubKey(string password, byte[] seed, string pem)
        {
            byte[] pw = Encoding.UTF8.GetBytes((password ?? string.Empty) + "\0");
            byte[] xored = new byte[pw.Length];
            for (int i = 0; i < pw.Length; i++) xored[i] = (byte)(pw[i] ^ seed[i % seed.Length]);
            using (var rsa = RsaPemHelper.ImportPublicKey(pem))
            {
                return rsa.Encrypt(xored, RSAEncryptionPadding.OaepSHA1);
            }
        }
    }

    // Minimal DER/PEM parser for SubjectPublicKeyInfo (RSA only). Avoids needing PemReader.
    internal static class RsaPemHelper
    {
        public static RSA ImportPublicKey(string pem)
        {
            int b = pem.IndexOf("-----BEGIN", StringComparison.Ordinal);
            int e = pem.IndexOf("-----END", StringComparison.Ordinal);
            if (b < 0 || e < 0) throw new InvalidOperationException("Invalid PEM (no markers)");
            int firstNl = pem.IndexOf('\n', b);
            string base64 = pem.Substring(firstNl + 1, e - firstNl - 1)
                .Replace("\r", string.Empty).Replace("\n", string.Empty).Replace(" ", string.Empty);
            byte[] der = Convert.FromBase64String(base64);
            return ImportSubjectPublicKeyInfo(der);
        }

        static RSA ImportSubjectPublicKeyInfo(byte[] der)
        {
            var p = new AsnReader(der);
            p.ReadSequenceHeader();
            p.SkipElement();          // AlgorithmIdentifier
            byte tag = p.ReadTag();
            if (tag != 0x03) throw new InvalidOperationException("Expected BIT STRING, got 0x" + tag.ToString("X2"));
            int bitStrLen = p.ReadLength();
            p.ReadByte();             // unused-bits count
            byte[] inner = p.ReadBytes(bitStrLen - 1);

            var inP = new AsnReader(inner);
            inP.ReadSequenceHeader();
            byte[] modulus = inP.ReadIntegerBytes();
            byte[] exponent = inP.ReadIntegerBytes();

            modulus = StripLeadingZero(modulus);
            exponent = StripLeadingZero(exponent);

            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
            return rsa;
        }

        static byte[] StripLeadingZero(byte[] b)
        {
            if (b.Length > 1 && b[0] == 0)
            {
                byte[] t = new byte[b.Length - 1];
                Buffer.BlockCopy(b, 1, t, 0, t.Length);
                return t;
            }
            return b;
        }

        class AsnReader
        {
            readonly byte[] _b;
            int _p;
            public AsnReader(byte[] b) { _b = b; _p = 0; }
            public byte ReadByte() { return _b[_p++]; }
            public byte ReadTag() { return _b[_p++]; }
            public int ReadLength()
            {
                byte b = _b[_p++];
                if ((b & 0x80) == 0) return b;
                int n = b & 0x7F;
                int len = 0;
                for (int i = 0; i < n; i++) len = (len << 8) | _b[_p++];
                return len;
            }
            public void ReadSequenceHeader()
            {
                byte tag = ReadTag();
                if (tag != 0x30) throw new InvalidOperationException("Expected SEQUENCE, got 0x" + tag.ToString("X2"));
                ReadLength();
            }
            public void SkipElement()
            {
                ReadTag();
                int len = ReadLength();
                _p += len;
            }
            public byte[] ReadBytes(int n)
            {
                byte[] r = new byte[n];
                Buffer.BlockCopy(_b, _p, r, 0, n);
                _p += n;
                return r;
            }
            public byte[] ReadIntegerBytes()
            {
                byte tag = ReadTag();
                if (tag != 0x02) throw new InvalidOperationException("Expected INTEGER, got 0x" + tag.ToString("X2"));
                int len = ReadLength();
                return ReadBytes(len);
            }
        }
    }
}
