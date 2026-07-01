using System.IO;
using System.Text;

namespace MySqlBackup.NET.RawBytes.Dump
{
    // Byte-level SQL value emission, mysqldump convention.
    // Strings stay UTF-8: the special escape bytes (\\ ' \0 \n \r \Z)
    // never appear inside a valid multi-byte UTF-8 continuation (0x80-0xBF),
    // so byte-by-byte escaping is safe and fast.
    internal static class SqlEscape
    {
        static readonly byte[] HEX = Encoding.ASCII.GetBytes("0123456789ABCDEF");
        static readonly byte[] NULL_LITERAL = Encoding.ASCII.GetBytes("NULL");
        static readonly byte[] EMPTY_HEX = Encoding.ASCII.GetBytes("''");

        public static void WriteNull(Stream s) { s.Write(NULL_LITERAL, 0, 4); }

        public static void WriteRaw(Stream s, byte[] bytes)
        {
            s.Write(bytes, 0, bytes.Length);
        }
        public static void WriteQuoted(Stream s, byte[] value)
        {
            s.WriteByte((byte)'\'');
            for (int i = 0; i < value.Length; i++)
            {
                byte b = value[i];
                switch (b)
                {
                    case (byte)'\'':                          // ' -> '' (ANSI doubling, any SQL mode)
                        s.WriteByte((byte)'\'');
                        s.WriteByte((byte)'\'');
                        break;
                    case (byte)'\\':                          // \      -> \\
                        s.WriteByte((byte)'\\');
                        s.WriteByte((byte)'\\');
                        break;
                    case 0x00:                                // NUL    -> \0
                        s.WriteByte((byte)'\\');
                        s.WriteByte((byte)'0');
                        break;
                    case 0x0A:                                // LF     -> \n
                        s.WriteByte((byte)'\\');
                        s.WriteByte((byte)'n');
                        break;
                    case 0x0D:                                // CR     -> \r
                        s.WriteByte((byte)'\\');
                        s.WriteByte((byte)'r');
                        break;
                    case 0x1A:                                // Ctrl-Z -> \Z
                        s.WriteByte((byte)'\\');
                        s.WriteByte((byte)'Z');
                        break;
                    default:
                        s.WriteByte(b);                       // all other bytes (incl. tab) pass through raw
                        break;
                }
            }
            s.WriteByte((byte)'\'');
        }
        public static void WriteHex(Stream s, byte[] value)
        {
            if (value.Length == 0) { s.Write(EMPTY_HEX, 0, EMPTY_HEX.Length); return; }
            s.WriteByte((byte)'0'); s.WriteByte((byte)'x');
            for (int i = 0; i < value.Length; i++)
            {
                byte b = value[i];
                s.WriteByte(HEX[b >> 4]);
                s.WriteByte(HEX[b & 0x0F]);
            }
        }
    }
}