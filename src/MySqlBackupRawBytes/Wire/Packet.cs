using System;
using System.IO;
using System.Text;

namespace MySqlBackup.NET.RawBytes.Wire
{
    // MySQL wire packet framing: 3-byte LE length + 1-byte sequence + payload.
    // Payloads of exactly 0xFFFFFF bytes continue into the next packet.
    internal static class Packet
    {
        public static byte[] Read(Stream s, out byte lastSeq)
        {
            byte[] header = new byte[4];
            MemoryStream merged = null;
            lastSeq = 0;

            while (true)
            {
                ReadExact(s, header, 0, 4);
                int len = header[0] | (header[1] << 8) | (header[2] << 16);
                lastSeq = header[3];

                byte[] chunk = new byte[len];
                if (len > 0) ReadExact(s, chunk, 0, len);

                if (len < 0xFFFFFF)
                {
                    if (merged == null) return chunk;
                    merged.Write(chunk, 0, len);
                    return merged.ToArray();
                }

                if (merged == null) merged = new MemoryStream();
                merged.Write(chunk, 0, len);
            }
        }

        public static void Write(Stream s, byte[] payload, ref byte seq)
        {
            int offset = 0;
            int remaining = payload.Length;

            do
            {
                int chunk = remaining >= 0xFFFFFF ? 0xFFFFFF : remaining;
                byte[] header = new byte[4];
                header[0] = (byte)(chunk & 0xFF);
                header[1] = (byte)((chunk >> 8) & 0xFF);
                header[2] = (byte)((chunk >> 16) & 0xFF);
                header[3] = seq++;
                s.Write(header, 0, 4);
                if (chunk > 0) s.Write(payload, offset, chunk);
                offset += chunk;
                remaining -= chunk;
            } while (remaining > 0);

            s.Flush();
        }

        static void ReadExact(Stream s, byte[] buf, int off, int len)
        {
            while (len > 0)
            {
                int n = s.Read(buf, off, len);
                if (n <= 0) throw new EndOfStreamException("Connection closed by server");
                off += n; len -= n;
            }
        }
    }

    internal class PayloadReader
    {
        readonly byte[] _b;
        int _p;
        public PayloadReader(byte[] buf) { _b = buf; _p = 0; }
        public int Position { get { return _p; } }
        public int Length { get { return _b.Length; } }
        public bool EOF { get { return _p >= _b.Length; } }
        public byte PeekByte() { return _b[_p]; }
        public byte ReadByte() { return _b[_p++]; }
        public ushort ReadUInt16() { ushort v = (ushort)(_b[_p] | (_b[_p + 1] << 8)); _p += 2; return v; }
        public uint ReadUInt24() { uint v = (uint)(_b[_p] | (_b[_p + 1] << 8) | (_b[_p + 2] << 16)); _p += 3; return v; }
        public uint ReadUInt32() { uint v = (uint)(_b[_p] | (_b[_p + 1] << 8) | (_b[_p + 2] << 16) | (_b[_p + 3] << 24)); _p += 4; return v; }
        public ulong ReadUInt64() { ulong v = 0; for (int i = 0; i < 8; i++) v |= (ulong)_b[_p + i] << (i * 8); _p += 8; return v; }
        public void Skip(int n) { _p += n; }
        public byte[] ReadBytes(int n) { byte[] r = new byte[n]; Buffer.BlockCopy(_b, _p, r, 0, n); _p += n; return r; }
        public byte[] ReadNullTerminatedBytes()
        {
            int start = _p;
            while (_p < _b.Length && _b[_p] != 0) _p++;
            byte[] r = new byte[_p - start];
            Buffer.BlockCopy(_b, start, r, 0, r.Length);
            if (_p < _b.Length) _p++;
            return r;
        }
        public string ReadNullTerminatedString() { return Encoding.UTF8.GetString(ReadNullTerminatedBytes()); }
        public byte[] ReadEofBytes()
        {
            byte[] r = new byte[_b.Length - _p];
            Buffer.BlockCopy(_b, _p, r, 0, r.Length);
            _p = _b.Length;
            return r;
        }
        public ulong ReadLenEncInt()
        {
            byte b = _b[_p++];
            if (b < 0xFB) return b;
            if (b == 0xFC) return ReadUInt16();
            if (b == 0xFD) return ReadUInt24();
            if (b == 0xFE) return ReadUInt64();
            throw new InvalidOperationException("Invalid length-encoded int prefix 0x" + b.ToString("X2"));
        }
        public byte[] ReadLenEncBytes()
        {
            ulong len = ReadLenEncInt();
            return ReadBytes((int)len);
        }
        public string ReadLenEncString() { return Encoding.UTF8.GetString(ReadLenEncBytes()); }
        public bool ReadLenEncBytesOrNull(out byte[] result)
        {
            byte b = _b[_p];
            if (b == 0xFB) { _p++; result = null; return true; }
            result = ReadLenEncBytes();
            return false;
        }
    }

    internal class PayloadWriter
    {
        readonly MemoryStream _ms = new MemoryStream();
        public byte[] ToArray() { return _ms.ToArray(); }
        public int Length { get { return (int)_ms.Length; } }
        public void WriteByte(byte b) { _ms.WriteByte(b); }
        public void WriteUInt16(ushort v) { _ms.WriteByte((byte)v); _ms.WriteByte((byte)(v >> 8)); }
        public void WriteUInt32(uint v) { for (int i = 0; i < 4; i++) _ms.WriteByte((byte)(v >> (i * 8))); }
        public void WriteBytes(byte[] b) { _ms.Write(b, 0, b.Length); }
        public void WriteZeros(int n) { for (int i = 0; i < n; i++) _ms.WriteByte(0); }
        public void WriteNullTerminatedString(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            _ms.Write(b, 0, b.Length);
            _ms.WriteByte(0);
        }
        public void WriteLenEncInt(ulong v)
        {
            if (v < 0xFB) _ms.WriteByte((byte)v);
            else if (v <= 0xFFFF) { _ms.WriteByte(0xFC); WriteUInt16((ushort)v); }
            else if (v <= 0xFFFFFF)
            {
                _ms.WriteByte(0xFD);
                _ms.WriteByte((byte)v);
                _ms.WriteByte((byte)(v >> 8));
                _ms.WriteByte((byte)(v >> 16));
            }
            else
            {
                _ms.WriteByte(0xFE);
                for (int i = 0; i < 8; i++) _ms.WriteByte((byte)(v >> (i * 8)));
            }
        }
        public void WriteLenEncBytes(byte[] b)
        {
            WriteLenEncInt((ulong)b.Length);
            _ms.Write(b, 0, b.Length);
        }
    }
}
