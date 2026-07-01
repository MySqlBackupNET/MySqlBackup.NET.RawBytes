using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MySqlBackup.NET.RawBytes.Wire
{
    // enum_field_types — values we care about for emission decisions.
    internal static class FieldType
    {
        public const byte DECIMAL = 0x00;
        public const byte TINY = 0x01;
        public const byte SHORT = 0x02;
        public const byte LONG = 0x03;
        public const byte FLOAT = 0x04;
        public const byte DOUBLE = 0x05;
        public const byte NULL = 0x06;
        public const byte TIMESTAMP = 0x07;
        public const byte LONGLONG = 0x08;
        public const byte INT24 = 0x09;
        public const byte DATE = 0x0A;
        public const byte TIME = 0x0B;
        public const byte DATETIME = 0x0C;
        public const byte YEAR = 0x0D;
        public const byte BIT = 0x10;
        public const byte JSON = 0xF5;
        public const byte NEWDECIMAL = 0xF6;
        public const byte ENUM = 0xF7;
        public const byte SET = 0xF8;
        public const byte TINY_BLOB = 0xF9;
        public const byte MEDIUM_BLOB = 0xFA;
        public const byte LONG_BLOB = 0xFB;
        public const byte BLOB = 0xFC;
        public const byte VAR_STRING = 0xFD;
        public const byte STRING = 0xFE;
        public const byte GEOMETRY = 0xFF;
    }

    public class ColumnDef
    {
        public string Schema;
        public string Table;
        public string OrgTable;
        public string Name;
        public string OrgName;
        public ushort CharsetId;
        public uint ColumnLength;
        public byte Type;
        public ushort Flags;
        public byte Decimals;

        // Binary-content column: emit as 0x... hex literal.
        public bool IsBinary
        {
            get
            {
                if (Type == FieldType.BIT || Type == FieldType.GEOMETRY) return true;
                if (CharsetId == 63)
                {
                    switch (Type)
                    {
                        case FieldType.TINY_BLOB:
                        case FieldType.MEDIUM_BLOB:
                        case FieldType.LONG_BLOB:
                        case FieldType.BLOB:
                        case FieldType.VAR_STRING:
                        case FieldType.STRING:
                            return true;
                    }
                }
                return false;
            }
        }

        // Numeric column: text-protocol value is ASCII digits, emit raw (no quotes).
        public bool IsNumeric
        {
            get
            {
                switch (Type)
                {
                    case FieldType.DECIMAL:
                    case FieldType.TINY:
                    case FieldType.SHORT:
                    case FieldType.LONG:
                    case FieldType.FLOAT:
                    case FieldType.DOUBLE:
                    case FieldType.LONGLONG:
                    case FieldType.INT24:
                    case FieldType.YEAR:
                    case FieldType.NEWDECIMAL:
                        return true;
                }
                return false;
            }
        }

        public static ColumnDef Parse(byte[] payload)
        {
            var r = new PayloadReader(payload);
            r.ReadLenEncString();      // catalog ("def")
            var c = new ColumnDef();
            c.Schema = r.ReadLenEncString();
            c.Table = r.ReadLenEncString();
            c.OrgTable = r.ReadLenEncString();
            c.Name = r.ReadLenEncString();
            c.OrgName = r.ReadLenEncString();
            r.ReadLenEncInt();         // fixed-length fields marker (0x0c)
            c.CharsetId = r.ReadUInt16();
            c.ColumnLength = r.ReadUInt32();
            c.Type = r.ReadByte();
            c.Flags = r.ReadUInt16();
            c.Decimals = r.ReadByte();
            return c;
        }
    }

    public class ResultSet
    {
        public List<ColumnDef> Columns = new List<ColumnDef>();
        public List<byte[][]> Rows = new List<byte[][]>();
        public ulong AffectedRows;
        public ulong LastInsertId;

        public static ResultSet Read(Stream s, bool deprecateEof)
        {
            byte seq;
            byte[] first = Packet.Read(s, out seq);
            byte m = first[0];

            if (m == 0xFF) ThrowError(first);
            if (m == 0x00 || m == 0xFE)
            {
                // OK packet (no result set) — DML/DDL response
                var rs0 = new ResultSet();
                var pr0 = new PayloadReader(first);
                pr0.ReadByte();
                rs0.AffectedRows = pr0.ReadLenEncInt();
                rs0.LastInsertId = pr0.ReadLenEncInt();
                return rs0;
            }

            var rsHeader = new PayloadReader(first);
            ulong nCols = rsHeader.ReadLenEncInt();
            var rs = new ResultSet();
            for (ulong i = 0; i < nCols; i++)
            {
                byte[] col = Packet.Read(s, out seq);
                rs.Columns.Add(ColumnDef.Parse(col));
            }

            if (!deprecateEof)
            {
                byte[] eof = Packet.Read(s, out seq);
                if (eof[0] != 0xFE)
                    throw new InvalidOperationException("Expected EOF after column defs, got 0x" + eof[0].ToString("X2"));
            }

            while (true)
            {
                byte[] row = Packet.Read(s, out seq);
                byte marker = row[0];

                if (marker == 0xFF) ThrowError(row);
                // Resultset terminator: 0xFE with packet length < 9 (rows starting with 0xFE
                // would mean an 8-byte lenenc — payload would be much larger).
                if (marker == 0xFE && row.Length < 9) break;

                var cells = new byte[rs.Columns.Count][];
                var rr = new PayloadReader(row);
                for (int i = 0; i < rs.Columns.Count; i++)
                {
                    byte[] data;
                    if (rr.ReadLenEncBytesOrNull(out data)) cells[i] = null;
                    else cells[i] = data;
                }
                rs.Rows.Add(cells);
            }
            return rs;
        }

        static void ThrowError(byte[] payload)
        {
            var r = new PayloadReader(payload);
            r.ReadByte();              // 0xFF
            ushort code = r.ReadUInt16();
            if (!r.EOF && r.PeekByte() == (byte)'#') { r.ReadByte(); r.Skip(5); }
            string msg = Encoding.UTF8.GetString(payload, r.Position, payload.Length - r.Position);
            throw new Exception("MySQL error " + code + ": " + msg);
        }
    }
}
