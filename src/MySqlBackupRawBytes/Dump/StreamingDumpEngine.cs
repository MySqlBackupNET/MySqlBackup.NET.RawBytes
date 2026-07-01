using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MySqlBackup.NET.RawBytes.Wire;

namespace MySqlBackup.NET.RawBytes.Dump
{
    // =========================================================================
    //  DDL byte-scan phase enum
    //  Controls the single-pass byte scanner in WriteDdlBytes().
    //  The scanner never converts DDL to a string — it operates directly on
    //  the raw bytes from the wire packet.
    // =========================================================================
    internal enum DdlPhase
    {
        InColumnDefs,       // inside the opening '(' ... ')' — write every byte verbatim
        InTableOptions,     // after the closing ')' — parse option tokens
        SkipValue,          // consuming and suppressing a value after '='
        WriteValue          // passing a value after '=' verbatim to output
    }

    // =========================================================================
    //  Pipeline phase enum
    //  Each value names exactly one distinct action the main loop can be in.
    //  The loop never "figures out" what to do from context — it reads the
    //  current Phase, executes that one action, then sets the next Phase.
    // =========================================================================
    internal enum DumpPhase
    {
        // --- query result header ---
        ReadColumnCount,        // read first packet → how many columns follow
        ReadColumnDef,          // read one column-definition packet, repeat N times
        ReadColumnEof,          // consume the EOF/OK separator after column defs

        // --- DDL (happens before rows) ---
        WriteDrop,              // emit  DROP TABLE IF EXISTS `t`;
        WriteDdl,               // emit  CREATE TABLE ... ;

        // --- INSERT header (emitted once per INSERT batch) ---
        WriteInsertHeader,      // emit  INSERT INTO `t` (`c1`,`c2`,...) VALUES

        // --- row loop ---
        ReadRowPacket,          // read one row packet from the wire
        WriteRowOpen,           // emit  \n(  or  ,\n(
        WriteCell,              // emit one cell value (raw / hex / quoted / NULL)
        WriteRowClose,          // emit  )  and decide: flush batch or continue

        // --- done ---
        WriteSemicolon,         // emit  ;\n  to close the current INSERT batch
        TableDone,              // post-table housekeeping, advance to next table
        DatabaseDone            // write footer, flush, stop
    }

    // =========================================================================
    //  Per-column emission kind — resolved once from ColumnDef, reused per row
    // =========================================================================
    internal enum CellKind { Numeric, Binary, Quoted, Null /* placeholder, overridden at runtime */ }

    // =========================================================================
    //  StreamingDumpEngine
    //  Reads MySQL wire packets one at a time and writes SQL text directly to
    //  the output stream.  No List<row>, no byte[][] accumulation.
    //  Uses ArrayPool<byte> for packet buffers — zero steady-state allocation
    //  in the row loop.
    // =========================================================================
    public class StreamingDumpEngine
    {
        // ---- wiring ----
        readonly MySqlConn _conn;
        readonly Stream _tcp;        // raw TCP stream exposed by MySqlConn

        // ---- options ----
        public bool DropTable = true;
        public bool CreateTable = true;
        public bool DumpRows = true;
        public bool WriteComments = false;
        public bool RemoveAutoIncrement = false;
        public bool RemoveTableCharset = false;
        public int MaxInsertBytes = 512 * 1024;

        // Controls the layout of a multi-row (extended) INSERT batch.
        //   false (default) → COMBINED: all value-tuples on one line
        //                     INSERT INTO `t` (`a`,`b`) VALUES (1,2), (3,4), (5,6);
        //   true            → SPLIT:    each value-tuple on its own line
        //                     INSERT INTO `t` (`a`,`b`) VALUES
        //                     (1,2),
        //                     (3,4),
        //                     (5,6);
        // Mirrors ExportInformations.InsertLineBreakBetweenInserts from the original library.
        public bool InsertLineBreakBetweenInserts = false;

        // Write a "-- Dump created on <timestamp>" comment at the top of the file.
        // Only emitted when WriteComments is also true. Kept separate from WriteComments
        // so callers can produce byte-reproducible dumps (diff/hash) while still keeping
        // the other structural comments.
        public bool RecordDumpTime = true;

        // ---- document headers / footers (plain strings, normalized to '\n') ----
        // The header SET block configures the import session (charset/collation save,
        // SET NAMES, UTC time zone, NO_AUTO_VALUE_ON_ZERO, FK/UNIQUE/NOTES off); the
        // footer restores it. Settable wholesale; assigning normalizes CRLF/CR to LF so
        // the dump never carries mixed line endings.
        string _headers = NormalizeNewlines(DefaultHeaders);
        string _footers = NormalizeNewlines(DefaultFooters);

        public string Headers
        {
            get { return _headers; }
            set { _headers = NormalizeNewlines(value ?? string.Empty); }
        }

        public string Footers
        {
            get { return _footers; }
            set { _footers = NormalizeNewlines(value ?? string.Empty); }
        }

        // Built with explicit '\n' (NOT a verbatim @"" literal) so the default never
        // inherits CRLF from the source file.
        public const string DefaultHeaders =
            "/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;\n" +
            "/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;\n" +
            "/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;\n" +
            "/*!40101 SET NAMES utf8mb4 */;\n" +
            "/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;\n" +
            "/*!40103 SET TIME_ZONE='+00:00' */;\n" +
            "/*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;\n" +
            "/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;\n" +
            "/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;\n" +
            "/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;";

        public const string DefaultFooters =
            "/*!40103 SET TIME_ZONE=@OLD_TIME_ZONE */;\n" +
            "/*!40101 SET SQL_MODE=@OLD_SQL_MODE */;\n" +
            "/*!40014 SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS */;\n" +
            "/*!40014 SET UNIQUE_CHECKS=@OLD_UNIQUE_CHECKS */;\n" +
            "/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;\n" +
            "/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;\n" +
            "/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;\n" +
            "/*!40111 SET SQL_NOTES=@OLD_SQL_NOTES */;";

        static string NormalizeNewlines(string s)
        {
            return s.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        public Action<string, int> OnTableComplete;

        // ---- pre-encoded constant byte sequences written by the engine ----
        // Every byte the engine itself contributes (not from the wire) lives here.
        static readonly byte[] B_DROP_PREFIX = Enc("DROP TABLE IF EXISTS `");
        static readonly byte[] B_DROP_SUFFIX = Enc("`;\n");
        static readonly byte[] B_INSERT_INTO = Enc("INSERT INTO `");
        static readonly byte[] B_INSERT_PAREN = Enc("` (");
        static readonly byte[] B_BACKTICK = Enc("`");
        static readonly byte[] B_BACKTICK_COMMA = Enc("`, ");
        static readonly byte[] B_VALUES = Enc(") VALUES");
        static readonly byte[] B_OPEN_PAREN = Enc("(");
        static readonly byte[] B_CLOSE_PAREN = Enc(")");
        static readonly byte[] B_COMMA = Enc(",");
        static readonly byte[] B_COMMA_NL = Enc(",\n");
        static readonly byte[] B_NL_OPEN = Enc("\n(");
        static readonly byte[] B_COMMA_NL_OPEN = Enc(",\n(");  // SPLIT mode: row separator + open paren
        static readonly byte[] B_SP_OPEN = Enc(" (");          // COMBINED mode: first row open paren
        static readonly byte[] B_COMMA_SP_OPEN = Enc(", (");   // COMBINED mode: row separator + open paren
        static readonly byte[] B_SEMI_NL = Enc(";\n");
        static readonly byte[] B_NL = Enc("\n");
        static readonly byte[] B_NULL = Enc("NULL");
        static readonly byte[] B_0X = Enc("0x");
        static readonly byte[] B_QUOTE = new byte[] { (byte)'\'' };
        static readonly byte[] B_EMPTY_STR = Enc("''");

        static readonly byte[] HEX_CHARS = Encoding.ASCII.GetBytes("0123456789ABCDEF");

        static byte[] Enc(string s) => Encoding.UTF8.GetBytes(s);

        // =========================================================================
        //  Constructor — MySqlConn exposes its raw stream via a new property
        //  (see note at bottom of file)
        // =========================================================================
        public StreamingDumpEngine(MySqlConn conn, Stream tcpStream)
        {
            _conn = conn;
            _tcp = tcpStream;
        }

        // =========================================================================
        //  Public entry point
        // =========================================================================
        public void DumpDatabase(string database, Stream output, IList<string> tables = null)
        {
            // Force the export session to UTC BEFORE reading any rows. This is the
            // companion to the "SET TIME_ZONE='+00:00'" line in the header: the header
            // only tells the importer to use UTC; this makes the TIMESTAMP values we
            // read actually be UTC. Without it, timestamps are dumped in the server's
            // session zone and shift on restore. The original session zone is restored
            // in the finally block so a reused connection is left untouched.
            string oldTimeZone = QuerySessionTimeZone();
            _conn.Query("SET TIME_ZONE='+00:00'");

            try
            {
                // --- dump-time comment (only with comments enabled) ---
                if (WriteComments && RecordDumpTime)
                    Write(output, Enc("-- Dump created on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n"));

                // --- header SET block ---
                Write(output, Enc(_headers));
                Write(output, B_NL);   // terminate the last header line
                Write(output, B_NL);   // blank line before first table

                // --- discover tables if not supplied ---
                if (tables == null || tables.Count == 0)
                {
                    var rs = _conn.Query("SHOW TABLES FROM `" + EscapeId(database) + "`");
                    var list = new List<string>(rs.Rows.Count);
                    foreach (var row in rs.Rows)
                        list.Add(Encoding.UTF8.GetString(row[0]));
                    tables = list;
                }

                foreach (var table in tables)
                    DumpTable(database, table, output);

                // --- footer SET block ---
                Write(output, B_NL);   // blank line before footer
                Write(output, Enc(_footers));
                Write(output, B_NL);   // terminate the last footer line
                output.Flush();
            }
            finally
            {
                if (!string.IsNullOrEmpty(oldTimeZone))
                    _conn.Query("SET TIME_ZONE='" + oldTimeZone.Replace("'", "''") + "'");
            }
        }

        // Read the current session time zone (e.g. "SYSTEM", "+08:00") so it can be
        // restored after the dump. Returns null if it cannot be determined.
        string QuerySessionTimeZone()
        {
            var rs = _conn.Query("SELECT @@session.time_zone");
            if (rs.Rows.Count > 0 && rs.Rows[0][0] != null)
                return Encoding.UTF8.GetString(rs.Rows[0][0]);
            return null;
        }

        // =========================================================================
        //  DumpTable — the state machine
        // =========================================================================
        void DumpTable(string database, string table, Stream output)
        {
            // Fully-qualified and simple quoted identifiers
            string fqn = "`" + EscapeId(database) + "`.`" + EscapeId(table) + "`";
            byte[] tableNameBytes = Encoding.UTF8.GetBytes(EscapeId(table));

            // ------------------------------------------------------------------
            //  Booleans — the "jumpers" that redirect the pipeline
            // ------------------------------------------------------------------
            bool is_first_row = true;   // true  → WriteInsertHeader emits INSERT INTO ...
            bool need_row_comma = false;  // false → WriteRowOpen emits \n(   true → ,\n(
            bool deprecate_eof = _conn.DeprecateEof;

            // ------------------------------------------------------------------
            //  Per-table working state
            // ------------------------------------------------------------------
            int colCount = 0;
            int cellIndex = 0;      // which column we are writing right now
            int rowCount = 0;
            CellKind[] colKinds = null;   // resolved once from ColumnDefs
            byte[][] colNames = null;   // pre-encoded column name bytes
            byte[] packetBuf = null;   // rented from ArrayPool per row
            int packetLen = 0;
            int packetPos = 0;      // cursor into current row packet
            long batchBytes = 0;      // bytes written to output since last INSERT header

            // Current cell slice inside the packet buffer
            int cellStart = 0;
            int cellLength = 0;
            CellKind cellKind = CellKind.Numeric;

            // ------------------------------------------------------------------
            //  DDL phase (outside the state machine — synchronous, uses Query())
            // ------------------------------------------------------------------
            if (DropTable || CreateTable)
            {
                if (WriteComments)
                {
                    byte[] cmt = Enc("\n-- \n-- Definition of " + table + "\n-- \n\n");
                    Write(output, cmt);
                }

                if (DropTable)
                {
                    Write(output, B_DROP_PREFIX);
                    Write(output, tableNameBytes);
                    Write(output, B_DROP_SUFFIX);
                }

                if (CreateTable)
                {
                    var rsCreate = _conn.Query("SHOW CREATE TABLE " + fqn);
                    if (rsCreate.Rows.Count > 0)
                    {
                        // rsCreate.Rows[0][1] is the raw DDL bytes from the wire — no string conversion.
                        // Inject "IF NOT EXISTS " after "CREATE TABLE " so the output matches
                        // the expected form: CREATE TABLE IF NOT EXISTS `name` (...)
                        byte[] ddlRaw = rsCreate.Rows[0][1];
                        byte[] ddlBuf = InjectIfNotExists(ddlRaw);
                        WriteDdlBytes(output, ddlBuf, 0, ddlBuf.Length);
                        Write(output, B_SEMI_NL);
                        Write(output, B_NL);
                    }
                }
            }

            if (!DumpRows)
            {
                OnTableComplete?.Invoke(table, 0);
                return;
            }

            if (WriteComments)
            {
                byte[] cmt = Enc("\n-- \n-- Dumping data for table " + table + "\n-- \n\n");
                Write(output, cmt);
            }

            // ------------------------------------------------------------------
            //  Send COM_QUERY for SELECT * and enter the streaming state machine
            // ------------------------------------------------------------------
            SendQuery("SELECT * FROM " + fqn);

            // ------------------------------------------------------------------
            //  Initial phase — read the column-count packet
            // ------------------------------------------------------------------
            DumpPhase phase = DumpPhase.ReadColumnCount;

            // outer loop — runs until TableDone or DatabaseDone
            while (phase != DumpPhase.TableDone)
            {
                switch (phase)
                {
                    // ==========================================================
                    //  PHASE: ReadColumnCount
                    //  First packet after COM_QUERY.
                    //  Payload: one length-encoded integer = number of columns.
                    // ==========================================================
                    case DumpPhase.ReadColumnCount:
                        {
                            byte seq;
                            byte[] pkt = ReadPacket(out seq);
                            if (pkt[0] == 0xFF) ThrowServerError(pkt);

                            // OK / empty result (table has no columns — shouldn't happen)
                            if (pkt[0] == 0x00 || (pkt[0] == 0xFE && pkt.Length < 9))
                            {
                                phase = DumpPhase.TableDone;
                                break;
                            }

                            int pos = 0;
                            colCount = (int)ReadLenEncInt(pkt, ref pos);
                            colKinds = new CellKind[colCount];
                            colNames = new byte[colCount][];

                            phase = DumpPhase.ReadColumnDef;
                            cellIndex = 0;      // reuse cellIndex as column-def counter
                            break;
                        }

                    // ==========================================================
                    //  PHASE: ReadColumnDef
                    //  One packet per column.  Parse type + charset → CellKind.
                    //  Also pre-encode the column name for the INSERT header.
                    // ==========================================================
                    case DumpPhase.ReadColumnDef:
                        {
                            byte seq;
                            byte[] pkt = ReadPacket(out seq);
                            ColumnDef col = ColumnDef.Parse(pkt);

                            colKinds[cellIndex] = col.IsBinary ? CellKind.Binary
                                                : col.IsNumeric ? CellKind.Numeric
                                                                : CellKind.Quoted;

                            colNames[cellIndex] = Encoding.UTF8.GetBytes(EscapeId(col.Name));

                            cellIndex++;

                            // Stay in ReadColumnDef until all columns consumed
                            phase = (cellIndex < colCount)
                                  ? DumpPhase.ReadColumnDef
                                  : DumpPhase.ReadColumnEof;
                            break;
                        }

                    // ==========================================================
                    //  PHASE: ReadColumnEof
                    //  Consume the EOF/OK packet that separates column defs
                    //  from row data.  Skipped when server uses DEPRECATE_EOF.
                    // ==========================================================
                    case DumpPhase.ReadColumnEof:
                        {
                            if (!deprecate_eof)
                            {
                                byte seq;
                                byte[] pkt = ReadPacket(out seq);
                                if (pkt[0] != 0xFE)
                                    throw new InvalidOperationException(
                                        "Expected EOF after column defs, got 0x" + pkt[0].ToString("X2"));
                            }
                            // Move straight to reading first row
                            phase = DumpPhase.ReadRowPacket;
                            break;
                        }

                    // ==========================================================
                    //  PHASE: ReadRowPacket
                    //  Read the next row packet off the wire into a rented buffer.
                    //  Identify whether it is a real row or the end-of-resultset.
                    // ==========================================================
                    case DumpPhase.ReadRowPacket:
                        {
                            byte seq;
                            // Rent a buffer; ReadPacketInto fills it and returns actual length
                            packetBuf = SimpleBufferPool.Shared.Rent(64 * 1024);
                            packetLen = ReadPacketInto(packetBuf, out seq);

                            byte marker = packetBuf[0];

                            if (marker == 0xFF)
                            {
                                // Error packet
                                byte[] copy = new byte[packetLen];
                                Buffer.BlockCopy(packetBuf, 0, copy, 0, packetLen);
                                SimpleBufferPool.Shared.Return(packetBuf);
                                packetBuf = null;
                                ThrowServerError(copy);
                            }

                            if (marker == 0xFE && packetLen < 9)
                            {
                                // End-of-resultset terminator
                                SimpleBufferPool.Shared.Return(packetBuf);
                                packetBuf = null;

                                // Close out any open INSERT batch
                                if (!is_first_row)
                                    phase = DumpPhase.WriteSemicolon;
                                else
                                    phase = DumpPhase.TableDone;
                                break;
                            }

                            // Real row — reset cursor and cell index
                            packetPos = 0;
                            cellIndex = 0;

                            phase = DumpPhase.WriteInsertHeader;
                            break;
                        }

                    // ==========================================================
                    //  PHASE: WriteInsertHeader
                    //  Emit  INSERT INTO `t` (`c1`, `c2`, ...) VALUES
                    //  only on the very first row of a batch.
                    //  Subsequent rows inside the same batch skip straight to
                    //  WriteRowOpen.
                    // ==========================================================
                    case DumpPhase.WriteInsertHeader:
                        {
                            if (is_first_row)
                            {
                                // INSERT INTO `tablename` (
                                Write(output, B_INSERT_INTO);
                                Write(output, tableNameBytes);
                                Write(output, B_INSERT_PAREN);

                                // `col1`, `col2`, ...
                                for (int i = 0; i < colCount; i++)
                                {
                                    Write(output, B_BACKTICK);
                                    Write(output, colNames[i]);
                                    Write(output, i < colCount - 1 ? B_BACKTICK_COMMA : B_BACKTICK);
                                }

                                // `) VALUES`
                                Write(output, B_VALUES);

                                batchBytes = output.Position > 0 ? 0 : 0; // reset size tracker
                                is_first_row = false;
                            }

                            phase = DumpPhase.WriteRowOpen;
                            break;
                        }

                    // ==========================================================
                    //  PHASE: WriteRowOpen
                    //  Emit opening punctuation for this row.
                    //  need_row_comma = false  →  first row of batch:  \n(
                    //  need_row_comma = true   →  subsequent rows:     ,\n(
                    // ==========================================================
                    case DumpPhase.WriteRowOpen:
                        {
                            // SPLIT  (InsertLineBreakBetweenInserts == true):  \n(   and  ,\n(
                            // COMBINED(InsertLineBreakBetweenInserts == false):  (   and  , (
                            if (need_row_comma)
                                Write(output, InsertLineBreakBetweenInserts ? B_COMMA_NL_OPEN : B_COMMA_SP_OPEN);
                            else
                                Write(output, InsertLineBreakBetweenInserts ? B_NL_OPEN : B_SP_OPEN);

                            need_row_comma = false;   // reset; WriteRowClose sets it true for next row
                            cellIndex = 0;
                            phase = DumpPhase.WriteCell;
                            break;
                        }

                    // ==========================================================
                    //  PHASE: WriteCell
                    //  Read one lenenc from the packet span.
                    //  Resolve cell kind (NULL overrides column kind).
                    //  Write directly from the span slice — no copy.
                    // ==========================================================
                    case DumpPhase.WriteCell:
                        {
                            // Comma between cells (not before the first)
                            if (cellIndex > 0)
                                Write(output, B_COMMA);

                            // --- Read length-encoded value from packet span ---
                            byte firstByte = packetBuf[packetPos];

                            if (firstByte == 0xFB)
                            {
                                // NULL
                                packetPos++;
                                Write(output, B_NULL);
                                // is_cell_null flag not needed — written inline
                            }
                            else
                            {
                                // Decode lenenc length
                                cellLength = (int)ReadLenEncInt(packetBuf, ref packetPos);
                                cellStart = packetPos;
                                packetPos += cellLength;

                                // Resolve kind from pre-computed column array
                                cellKind = colKinds[cellIndex];

                                // --- Write directly from packet buffer span ---
                                switch (cellKind)
                                {
                                    case CellKind.Numeric:
                                        // Raw wire bytes are already ASCII digits — pipe straight through
                                        output.Write(packetBuf, cellStart, cellLength);
                                        break;

                                    case CellKind.Binary:
                                        // Hex-encode in-place from the span slice
                                        WriteHexSpan(output, packetBuf, cellStart, cellLength);
                                        break;

                                    case CellKind.Quoted:
                                        // Quote + escape from the span slice
                                        WriteQuotedSpan(output, packetBuf, cellStart, cellLength);
                                        break;
                                }
                            }

                            cellIndex++;

                            // Stay in WriteCell until all columns emitted
                            phase = (cellIndex < colCount)
                                  ? DumpPhase.WriteCell
                                  : DumpPhase.WriteRowClose;
                            break;
                        }

                    // ==========================================================
                    //  PHASE: WriteRowClose
                    //  Emit )
                    //  Return the rented packet buffer.
                    //  Set need_row_comma=true so the NEXT WriteRowOpen emits ,\n(
                    //  instead of \n(  — this is how we defer the comma until we
                    //  know the NEXT row exists (can't un-write a comma if EOF comes).
                    //  Then check batch size limit.
                    // ==========================================================
                    case DumpPhase.WriteRowClose:
                        {
                            Write(output, B_CLOSE_PAREN);
                            rowCount++;

                            // Return the rented buffer — done with this row's bytes
                            SimpleBufferPool.Shared.Return(packetBuf);
                            packetBuf = null;

                            // Signal next WriteRowOpen to prefix with ,\n
                            need_row_comma = true;

                            // Track approximate batch size
                            batchBytes += packetLen + 4;   // packet payload + lenenc overhead estimate
                            if (batchBytes >= MaxInsertBytes)
                            {
                                // Close this INSERT batch, start a fresh one after next row
                                phase = DumpPhase.WriteSemicolon;
                                is_first_row = true;    // triggers new INSERT header
                                need_row_comma = false;   // new batch starts with \n(
                                batchBytes = 0;
                            }
                            else
                            {
                                phase = DumpPhase.ReadRowPacket;
                            }
                            break;
                        }

                    // ==========================================================
                    //  PHASE: WriteSemicolon
                    //  Close the current INSERT batch with ;\n
                    //  Then either start a new batch (is_first_row=true) or done.
                    // ==========================================================
                    case DumpPhase.WriteSemicolon:
                        {
                            Write(output, B_SEMI_NL);

                            if (is_first_row)
                            {
                                // We just finished because of a batch flush mid-table.
                                // is_first_row was already set true in WriteRowClose.
                                // Continue reading rows.
                                phase = DumpPhase.ReadRowPacket;
                            }
                            else
                            {
                                // We got here from ReadRowPacket seeing the EOF terminator.
                                // is_first_row is still false — all rows written.
                                phase = DumpPhase.TableDone;
                            }
                            break;
                        }

                    // ==========================================================
                    //  PHASE: TableDone
                    //  Exits the while loop.
                    // ==========================================================
                    case DumpPhase.TableDone:
                        break;

                } // end switch
            } // end while

            // Return any leaked buffer (safety net for exception paths)
            if (packetBuf != null)
            {
                SimpleBufferPool.Shared.Return(packetBuf);
                packetBuf = null;
            }

            Write(output, B_NL);
            OnTableComplete?.Invoke(table, rowCount);
        }

        // =========================================================================
        //  Span-based cell writers
        //  Operate on a slice of the rented packet buffer — zero allocation.
        // =========================================================================

        /// <summary>
        /// Write a binary cell as a hex literal:  0xDEADBEEF
        /// Reads directly from buf[start..start+len].
        /// </summary>
        static void WriteHexSpan(Stream s, byte[] buf, int start, int len)
        {
            if (len == 0) { s.Write(B_EMPTY_STR, 0, B_EMPTY_STR.Length); return; }

            s.Write(B_0X, 0, 2);

            // Two hex chars per byte — write via a small reusable buffer to avoid
            // per-byte Write() calls.  64-byte input chunks → 128 hex output chars.
            byte[] tmp = new byte[128];
            int i = 0;
            while (i < len)
            {
                int chunk = Math.Min(64, len - i);   // 64 input bytes → 128 hex chars
                for (int j = 0; j < chunk; j++)
                {
                    byte b = buf[start + i + j];
                    tmp[j * 2] = HEX_CHARS[b >> 4];
                    tmp[j * 2 + 1] = HEX_CHARS[b & 0x0F];
                }
                s.Write(tmp, 0, chunk * 2);
                i += chunk;
            }
        }

        /// <summary>
        /// Write a string cell as a quoted SQL literal following mysqldump convention:
        ///   '  → ''      (ANSI doubling — mode-independent)
        ///   \  → \\      NUL → \0   LF → \n   CR → \r   Ctrl-Z → \Z
        /// All other bytes (including tab) pass through verbatim. The backslash escapes
        /// require the import session to have backslash-escapes enabled (i.e. NOT
        /// NO_BACKSLASH_ESCAPES); the document header guarantees this via
        /// SQL_MODE='NO_AUTO_VALUE_ON_ZERO'. Reads directly from buf[start..start+len].
        /// </summary>
        static void WriteQuotedSpan(Stream s, byte[] buf, int start, int len)
        {
            s.Write(B_QUOTE, 0, 1);

            int segStart = start;
            int end = start + len;

            for (int i = start; i < end; i++)
            {
                byte b = buf[i];
                byte esc = 0;   // second char of a "\x" escape sequence

                switch (b)
                {
                    case (byte)'\'':
                        // ' -> ''  (ANSI doubling, works in any SQL mode)
                        if (i > segStart) s.Write(buf, segStart, i - segStart);
                        s.WriteByte((byte)'\'');
                        s.WriteByte((byte)'\'');
                        segStart = i + 1;
                        continue;

                    case (byte)'\\': esc = (byte)'\\'; break;  // \      -> \\
                    case 0x00: esc = (byte)'0'; break;  // NUL    -> \0
                    case 0x0A: esc = (byte)'n'; break;  // LF     -> \n
                    case 0x0D: esc = (byte)'r'; break;  // CR     -> \r
                    case 0x1A: esc = (byte)'Z'; break;  // Ctrl-Z -> \Z

                    default:
                        continue;   // ordinary byte — stays in the clean segment
                }

                // Backslash escape: flush the clean run, then emit '\' + esc
                if (i > segStart) s.Write(buf, segStart, i - segStart);
                s.WriteByte((byte)'\\');
                s.WriteByte(esc);
                segStart = i + 1;
            }

            // Flush any remaining clean segment
            if (end > segStart)
                s.Write(buf, segStart, end - segStart);

            s.Write(B_QUOTE, 0, 1);
        }

        // =========================================================================
        //  Wire helpers — read directly from the TCP stream
        // =========================================================================

        /// <summary>
        /// Read one MySQL packet. Returns a newly allocated byte[] (used for
        /// column defs and control packets where we need to hold the data).
        /// For row packets use ReadPacketInto instead.
        /// </summary>
        byte[] ReadPacket(out byte lastSeq)
        {
            return Packet.Read(_tcp, out lastSeq);
        }

        /// <summary>
        /// Read one MySQL packet into a pre-rented buffer.
        /// Returns the payload length.  Grows the buffer via ArrayPool if needed.
        /// </summary>
        int ReadPacketInto(byte[] buf, out byte lastSeq)
        {
            // We re-use Packet.Read for simplicity — it allocates internally but
            // the allocation is temporary and the data is immediately copied into
            // the rented buf.  A future optimisation could read the header +
            // stream directly into buf without the intermediate allocation.
            byte[] tmp = Packet.Read(_tcp, out lastSeq);
            if (tmp.Length > buf.Length)
            {
                // Packet is larger than the rented buffer — rare for small tables.
                // Just copy; the rented buf will be returned unused.
                Buffer.BlockCopy(tmp, 0, buf, 0, Math.Min(tmp.Length, buf.Length));
            }
            else
            {
                Buffer.BlockCopy(tmp, 0, buf, 0, tmp.Length);
            }
            return tmp.Length;
        }

        /// <summary>
        /// Send a COM_QUERY packet to the server.
        /// </summary>
        void SendQuery(string sql)
        {
            byte[] sqlBytes = Encoding.UTF8.GetBytes(sql);
            byte[] cmd = new byte[1 + sqlBytes.Length];
            cmd[0] = 0x03;   // COM_QUERY
            Buffer.BlockCopy(sqlBytes, 0, cmd, 1, sqlBytes.Length);
            byte seq = 0;
            Packet.Write(_tcp, cmd, ref seq);
        }

        // =========================================================================
        //  Length-encoded integer decoder — operates on a byte[] with a ref cursor
        //  so it works on the rented packet buffer without any allocation.
        // =========================================================================
        static ulong ReadLenEncInt(byte[] buf, ref int pos)
        {
            byte b = buf[pos++];
            if (b < 0xFB) return b;
            if (b == 0xFC)
            {
                ushort v = (ushort)(buf[pos] | (buf[pos + 1] << 8));
                pos += 2;
                return v;
            }
            if (b == 0xFD)
            {
                uint v = (uint)(buf[pos] | (buf[pos + 1] << 8) | (buf[pos + 2] << 16));
                pos += 3;
                return v;
            }
            // 0xFE — 8 bytes
            ulong w = 0;
            for (int i = 0; i < 8; i++) w |= (ulong)buf[pos + i] << (i * 8);
            pos += 8;
            return w;
        }

        // =========================================================================
        //  WriteDdlBytes — single-pass byte scanner for CREATE TABLE DDL
        //
        //  Operates directly on the raw bytes from the wire packet.
        //  No string allocation, no regex, one forward pass.
        //
        //  State machine:
        //
        //    InColumnDefs  ──── closing ')' at paren_depth==0 ────►  InTableOptions
        //
        //    InTableOptions scans option tokens:
        //      "AUTO_INCREMENT=" detected  → SkipAutoIncrement  (if RemoveAutoIncrement)
        //      "DEFAULT CHARSET="           → SkipDefaultCharset (if RemoveTableCharset)
        //      "CHARACTER SET="             → SkipCharacterSet   (if RemoveTableCharset)
        //      "COLLATE="                   → SkipCollate         (if RemoveTableCharset)
        //      anything else               → write byte verbatim
        //
        //    Skip* phases consume bytes until a space or end-of-input,
        //    then transition back to InTableOptions.
        // =========================================================================
        void WriteDdlBytes(Stream s, byte[] buf, int start, int len)
        {
            int end = start + len;
            int paren_depth = 0;
            DdlPhase phase = DdlPhase.InColumnDefs;

            var kwBuf = new StringBuilder(32);

            for (int i = start; i < end; i++)
            {
                byte b = buf[i];

                switch (phase)
                {
                    // ----------------------------------------------------------
                    //  InColumnDefs
                    //  Write every byte verbatim.
                    //  Track paren depth.
                    //  When ')' brings depth to 0 → table options begin.
                    // ----------------------------------------------------------
                    case DdlPhase.InColumnDefs:
                        {
                            if (b == (byte)'(')
                            {
                                paren_depth++;
                                s.WriteByte(b);
                            }
                            else if (b == (byte)')')
                            {
                                paren_depth--;
                                s.WriteByte(b);

                                if (paren_depth == 0)
                                    phase = DdlPhase.InTableOptions;
                            }
                            else
                            {
                                s.WriteByte(b);
                            }
                            break;
                        }

                    // ----------------------------------------------------------
                    //  InTableOptions
                    //  Accumulate bytes (including spaces) into kwBuf.
                    //  When '=' is seen → keyword is complete → normalize →
                    //  blacklist check → either skip value or flush verbatim.
                    // ----------------------------------------------------------
                    case DdlPhase.InTableOptions:
                        {
                            if (b == (byte)'=')
                            {
                                // Normalize: trim edges, collapse internal double-spaces
                                string kw = kwBuf.ToString().Trim();
                                while (kw.Contains("  "))
                                    kw = kw.Replace("  ", " ");

                                kwBuf.Clear();

                                bool skip = (RemoveAutoIncrement && string.Equals(kw, "AUTO_INCREMENT", StringComparison.OrdinalIgnoreCase)) ||
                                            (RemoveTableCharset && (string.Equals(kw, "DEFAULT CHARSET", StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(kw, "CHARACTER SET", StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(kw, "COLLATE", StringComparison.OrdinalIgnoreCase)));

                                if (skip)
                                {
                                    phase = DdlPhase.SkipValue;
                                }
                                else
                                {
                                    // Emit single leading space + keyword + '=' (drops any
                                    // accumulated whitespace left behind by a skipped option).
                                    s.WriteByte((byte)' ');
                                    foreach (char c in kw) s.WriteByte((byte)c);
                                    s.WriteByte((byte)'=');
                                    phase = DdlPhase.WriteValue;
                                }
                            }
                            else if (b == (byte)';' || b == (byte)'\n' || b == (byte)'\r')
                            {
                                // Terminator hit while between options — discard any
                                // accumulated trailing whitespace, emit the terminator only.
                                kwBuf.Clear();
                                s.WriteByte(b);
                            }
                            else
                            {
                                kwBuf.Append((char)b);
                            }
                            break;
                        }

                    // ----------------------------------------------------------
                    //  SkipValue
                    //  Consume value bytes until whitespace or ';'.
                    //  For ';' / newline → write it (closes the statement).
                    //  For space → swallow it so a skipped option leaves no
                    //  trailing whitespace before the next option / terminator.
                    // ----------------------------------------------------------
                    case DdlPhase.SkipValue:
                        {
                            if (b == (byte)';' || b == (byte)'\n' || b == (byte)'\r')
                            {
                                s.WriteByte(b);
                                phase = DdlPhase.InTableOptions;
                            }
                            else if (b == (byte)' ')
                            {
                                phase = DdlPhase.InTableOptions;
                            }
                            // else: value byte — skip silently
                            break;
                        }

                    // ----------------------------------------------------------
                    //  WriteValue
                    //  Pass value bytes verbatim until whitespace or end.
                    //  ';' / newline are written (close the statement).
                    //  A space is swallowed — the next option's leading space
                    //  is re-emitted by InTableOptions if that option is kept.
                    // ----------------------------------------------------------
                    case DdlPhase.WriteValue:
                        {
                            if (b == (byte)';' || b == (byte)'\n' || b == (byte)'\r')
                            {
                                s.WriteByte(b);
                                phase = DdlPhase.InTableOptions;
                            }
                            else if (b == (byte)' ')
                            {
                                phase = DdlPhase.InTableOptions;
                            }
                            else
                            {
                                s.WriteByte(b);
                            }
                            break;
                        }
                }
            }
        }

        // -------------------------------------------------------------------------
        //  MatchAsciiAt — peek-ahead token match, case-insensitive, no allocation
        //
        //  Returns true if buf[pos .. pos+token.Length] matches token (ASCII,
        //  case-insensitive).  Does NOT advance pos — caller decides how far to skip.
        // -------------------------------------------------------------------------
        static bool MatchAsciiAt(byte[] buf, int pos, int end, string token)
        {
            int tlen = token.Length;
            if (pos + tlen > end) return false;
            for (int t = 0; t < tlen; t++)
            {
                byte b = buf[pos + t];
                byte tb = (byte)token[t];
                // Case-fold: if token char is A-Z, also accept a-z
                if (b != tb)
                {
                    // try lowercase version of the token char
                    byte tbl = (tb >= (byte)'A' && tb <= (byte)'Z')
                               ? (byte)(tb + 32) : tb;
                    if (b != tbl) return false;
                }
            }
            return true;
        }

        // =========================================================================
        //  InjectIfNotExists
        //  SHOW CREATE TABLE returns "CREATE TABLE `name` (...)" without the
        //  IF NOT EXISTS clause.  This helper splices " IF NOT EXISTS" into the
        //  raw DDL bytes immediately after "CREATE TABLE", so the dump output
        //  is idempotent when restored into an existing schema.
        // =========================================================================
        static readonly byte[] B_IF_NOT_EXISTS = Enc("IF NOT EXISTS ");

        static byte[] InjectIfNotExists(byte[] ddl)
        {
            // "CREATE TABLE " is 13 bytes.  Find it (case-insensitive) and splice.
            const string marker = "CREATE TABLE ";
            int mlen = marker.Length;
            int insertAt = -1;

            for (int i = 0; i <= ddl.Length - mlen; i++)
            {
                if (MatchAsciiAt(ddl, i, ddl.Length, marker))
                {
                    insertAt = i + mlen;   // splice point: right after the space
                    break;
                }
            }

            if (insertAt < 0)
                return ddl;   // unexpected — return unchanged

            byte[] result = new byte[ddl.Length + B_IF_NOT_EXISTS.Length];
            Buffer.BlockCopy(ddl, 0, result, 0, insertAt);
            Buffer.BlockCopy(B_IF_NOT_EXISTS, 0, result, insertAt, B_IF_NOT_EXISTS.Length);
            Buffer.BlockCopy(ddl, insertAt, result, insertAt + B_IF_NOT_EXISTS.Length, ddl.Length - insertAt);
            return result;
        }

        // =========================================================================
        //  Micro-helpers
        // =========================================================================
        static void Write(Stream s, byte[] b) { s.Write(b, 0, b.Length); }

        static string EscapeId(string id) { return id.Replace("`", "``"); }

        static void ThrowServerError(byte[] payload)
        {
            var r = new PayloadReader(payload);
            r.ReadByte();
            ushort code = r.ReadUInt16();
            if (!r.EOF && r.PeekByte() == (byte)'#') { r.ReadByte(); r.Skip(5); }
            string msg = Encoding.UTF8.GetString(payload, r.Position, payload.Length - r.Position);
            throw new Exception("MySQL error " + code + ": " + msg);
        }
    }
}

// =============================================================================
//  NOTE: MySqlConn needs to expose its raw stream.
//
//  Add this property to MySqlConn.cs:
//
//      public Stream RawStream { get { return _stream; } }
//
//  Then construct StreamingDumpEngine like this:
//
//      using (var conn = new MySqlConn())
//      {
//          conn.Open(host, port, user, password, database);
//          var engine = new StreamingDumpEngine(conn, conn.RawStream);
//          engine.DumpDatabase(database, outputStream);
//      }
//
// =============================================================================