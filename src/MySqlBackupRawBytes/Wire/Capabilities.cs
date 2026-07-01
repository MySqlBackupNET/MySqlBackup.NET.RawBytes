namespace MySqlBackup.NET.RawBytes.Wire
{
    internal static class Caps
    {
        public const uint LONG_PASSWORD = 0x00000001;
        public const uint FOUND_ROWS = 0x00000002;
        public const uint LONG_FLAG = 0x00000004;
        public const uint CONNECT_WITH_DB = 0x00000008;
        public const uint PROTOCOL_41 = 0x00000200;
        public const uint SSL = 0x00000800;
        public const uint TRANSACTIONS = 0x00002000;
        public const uint SECURE_CONNECTION = 0x00008000;
        public const uint MULTI_STATEMENTS = 0x00010000;
        public const uint MULTI_RESULTS = 0x00020000;
        public const uint PLUGIN_AUTH = 0x00080000;
        public const uint CONNECT_ATTRS = 0x00100000;
        public const uint PLUGIN_AUTH_LENENC_CLIENT_DATA = 0x00200000;
        public const uint DEPRECATE_EOF = 0x01000000;
    }
}
