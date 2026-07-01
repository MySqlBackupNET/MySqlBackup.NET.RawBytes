using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace MySqlBackup.NET.RawBytes.Wire
{
    public class MySqlConn : IDisposable
    {
        TcpClient _tcp;
        Stream _stream;

        public uint ServerCaps { get; private set; }
        public string ServerVersion { get; private set; }
        public bool DeprecateEof { get { return (ServerCaps & Caps.DEPRECATE_EOF) != 0; } }
        public Stream RawStream { get { return _stream; } }

        public void Open(string host, int port, string user, string password, string database)
        {
            _tcp = new TcpClient();
            _tcp.Connect(host, port);
            _tcp.NoDelay = true;
            _stream = _tcp.GetStream();

            byte seq;
            byte[] hsPayload = Packet.Read(_stream, out seq);
            var hs = HandshakeV10.Parse(hsPayload);
            ServerCaps = hs.CapabilityFlags;
            ServerVersion = hs.ServerVersion;

            uint clientCaps =
                Caps.LONG_PASSWORD | Caps.LONG_FLAG | Caps.PROTOCOL_41 |
                Caps.TRANSACTIONS | Caps.SECURE_CONNECTION |
                Caps.PLUGIN_AUTH | Caps.PLUGIN_AUTH_LENENC_CLIENT_DATA |
                (ServerCaps & Caps.DEPRECATE_EOF);
            if (!string.IsNullOrEmpty(database)) clientCaps |= Caps.CONNECT_WITH_DB;

            string plugin = hs.AuthPluginName ?? "mysql_native_password";
            byte[] authResp = ScrambleFor(plugin, password, hs.AuthPluginData);

            var w = new PayloadWriter();
            w.WriteUInt32(clientCaps);
            w.WriteUInt32(0x01000000);   // max packet 16 MB
            w.WriteByte(255);            // utf8mb4_0900_ai_ci
            w.WriteZeros(23);
            w.WriteNullTerminatedString(user);
            w.WriteLenEncBytes(authResp);
            if (!string.IsNullOrEmpty(database)) w.WriteNullTerminatedString(database);
            w.WriteNullTerminatedString(plugin);

            byte respSeq = (byte)(seq + 1);
            Packet.Write(_stream, w.ToArray(), ref respSeq);

            CompleteAuth(plugin, password, hs.AuthPluginData);
        }

        static byte[] ScrambleFor(string plugin, string password, byte[] seed)
        {
            if (plugin == "mysql_native_password") return AuthScramble.NativePassword(password, seed);
            if (plugin == "caching_sha2_password") return AuthScramble.CachingSha2(password, seed);
            // Unknown plugin: send empty, server will likely auth-switch.
            return new byte[0];
        }

        void CompleteAuth(string plugin, string password, byte[] seed)
        {
            while (true)
            {
                byte sseq;
                byte[] payload = Packet.Read(_stream, out sseq);
                byte marker = payload[0];

                if (marker == 0x00) return;        // OK
                if (marker == 0xFF) ThrowError(payload);

                if (marker == 0xFE)
                {
                    // Auth Switch Request: 0xFE, plugin_name\0, seed\0
                    var pr = new PayloadReader(payload);
                    pr.ReadByte();
                    string newPlugin = Encoding.ASCII.GetString(pr.ReadNullTerminatedBytes());
                    byte[] newSeed = pr.ReadEofBytes();
                    if (newSeed.Length > 0 && newSeed[newSeed.Length - 1] == 0)
                    {
                        byte[] t = new byte[newSeed.Length - 1];
                        Buffer.BlockCopy(newSeed, 0, t, 0, t.Length);
                        newSeed = t;
                    }
                    byte[] resp = ScrambleFor(newPlugin, password, newSeed);
                    byte next = (byte)(sseq + 1);
                    Packet.Write(_stream, resp, ref next);
                    plugin = newPlugin;
                    seed = newSeed;
                    continue;
                }

                if (marker == 0x01 && plugin == "caching_sha2_password" && payload.Length >= 2)
                {
                    byte status = payload[1];
                    if (status == 0x03)
                    {
                        // fast_auth_success — next packet is OK
                        continue;
                    }
                    if (status == 0x04)
                    {
                        // full_authentication required — request public key, then RSA-OAEP encrypted password
                        byte next = (byte)(sseq + 1);
                        Packet.Write(_stream, new byte[] { 0x02 }, ref next);

                        byte kseq;
                        byte[] keyPkt = Packet.Read(_stream, out kseq);
                        if (keyPkt[0] != 0x01)
                            throw new InvalidOperationException("Expected AuthMoreData carrying public key, got 0x" + keyPkt[0].ToString("X2"));
                        string pem = Encoding.ASCII.GetString(keyPkt, 1, keyPkt.Length - 1);

                        byte[] enc = AuthScramble.EncryptPasswordWithPubKey(password, seed, pem);
                        byte next2 = (byte)(kseq + 1);
                        Packet.Write(_stream, enc, ref next2);
                        continue;
                    }
                }

                throw new InvalidOperationException("Unexpected auth packet marker 0x" + marker.ToString("X2"));
            }
        }

        public ResultSet Query(string sql)
        {
            byte[] sqlBytes = Encoding.UTF8.GetBytes(sql);
            byte[] cmd = new byte[1 + sqlBytes.Length];
            cmd[0] = 0x03;                          // COM_QUERY
            Buffer.BlockCopy(sqlBytes, 0, cmd, 1, sqlBytes.Length);
            byte seq = 0;
            Packet.Write(_stream, cmd, ref seq);
            return ResultSet.Read(_stream, DeprecateEof);
        }

        static void ThrowError(byte[] payload)
        {
            var r = new PayloadReader(payload);
            r.ReadByte();
            ushort code = r.ReadUInt16();
            if (!r.EOF && r.PeekByte() == (byte)'#') { r.ReadByte(); r.Skip(5); }
            string msg = Encoding.UTF8.GetString(payload, r.Position, payload.Length - r.Position);
            throw new Exception("MySQL error " + code + ": " + msg);
        }

        public void Dispose()
        {
            try { if (_stream != null) _stream.Dispose(); } catch { }
            try { if (_tcp != null) _tcp.Close(); } catch { }
        }
    }
}