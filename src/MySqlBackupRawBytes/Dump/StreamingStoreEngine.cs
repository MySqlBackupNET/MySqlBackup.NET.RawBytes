using System;
using System.IO;
using System.Text;
using MySqlBackup.NET.RawBytes.Wire;

namespace MySqlBackup.NET.RawBytes.Dump
{
    // =========================================================================
    //  RestorePhase
    //  Names each state of the SQL-statement scanner.
    // =========================================================================
    internal enum RestorePhase
    {
        Scan,           // scanning forward, skipping whitespace/blank lines
        InStatement,    // accumulating bytes of a statement
        InSingleQuote,  // inside '...'  — semicolons are not terminators
        InDoubleQuote,  // inside "..."  — semicolons are not terminators
        InBacktick,     // inside `...`  — semicolons are not terminators
        InLineComment,  // after --     — skip to end of line
        InBlockComment, // inside /* ... */
        AfterStar,      // inside /* */ and last byte was '*' (possible end)
        AfterEscape,    // inside a quote and last byte was '\'
    }

    // =========================================================================
    //  StreamingRestoreEngine
    //
    //  Reads a SQL dump produced by StreamingDumpEngine (or MySqlBackup.NET)
    //  from any Stream and executes each statement directly against MySQL via
    //  the raw wire protocol — no full-file load, minimal allocations.
    //
    //  Design:
    //    • Reads the input in fixed-size chunks (ReadBufferSize).
    //    • Accumulates each statement into an ArrayPool<byte> buffer that
    //      grows by doubling when needed.
    //    • Sends each complete statement (without the trailing ';') as a
    //      COM_QUERY packet and reads + discards the server response.
    //    • Skips blank lines, line comments (--), block comments (/* */),
    //      and the /*!... */ conditional comments that MySQL interprets
    //      (these are executed, matching mysqldump / MySqlBackup.NET behaviour).
    //    • Correctly handles semicolons inside quoted strings and identifiers.
    // =========================================================================
    public class StreamingRestoreEngine
    {
        // ---- wiring --------------------------------------------------------
        readonly MySqlConn _conn;
        readonly Stream _tcp;

        // ---- options --------------------------------------------------------

        /// <summary>Called after each successfully executed statement.
        /// Argument is the byte length of the statement sent.</summary>
        public Action<int> OnStatementExecuted;

        /// <summary>Called when a statement fails.
        /// Return true to continue, false to abort.</summary>
        public Func<int, string, bool> OnStatementError;   // (errorCode, message) → continue?

        /// <summary>Size of the chunk used to read the input stream (default 64 KB).</summary>
        public int ReadBufferSize = 64 * 1024;

        /// <summary>Initial capacity of the per-statement accumulation buffer (default 256 KB).</summary>
        public int InitialStatementCapacity = 256 * 1024;

        // ---- pre-encoded constants ------------------------------------------
        static readonly byte[] B_SET_FK_OFF = Encoding.UTF8.GetBytes("SET FOREIGN_KEY_CHECKS=0");
        static readonly byte[] B_SET_FK_ON = Encoding.UTF8.GetBytes("SET FOREIGN_KEY_CHECKS=1");

        // =========================================================================
        //  Constructor
        // =========================================================================
        public StreamingRestoreEngine(MySqlConn conn, Stream tcpStream)
        {
            _conn = conn;
            _tcp = tcpStream;
        }

        // =========================================================================
        //  Public entry point
        //
        //  Reads 'input' from its current position to the end and executes
        //  every SQL statement found in the dump.
        //
        //  The caller is responsible for:
        //    • Opening the connection and selecting the target database.
        //    • Disposing the connection afterwards.
        // =========================================================================
        public void RestoreDatabase(Stream input)
        {
            // Rent a read buffer
            byte[] readBuf = SimpleBufferPool.Shared.Rent(ReadBufferSize);

            // Rent a statement accumulation buffer (grows by doubling)
            int stmtCap = InitialStatementCapacity;
            byte[] stmtBuf = SimpleBufferPool.Shared.Rent(stmtCap);
            int stmtLen = 0;

            // Scanner state
            RestorePhase phase = RestorePhase.Scan;
            RestorePhase quoteReturn = RestorePhase.InStatement; // phase to return to after escape
            byte openQuote = 0;                        // the opening quote byte

            try
            {
                int bytesRead;
                while ((bytesRead = input.Read(readBuf, 0, readBuf.Length)) > 0)
                {
                    for (int i = 0; i < bytesRead; i++)
                    {
                        byte b = readBuf[i];

                        switch (phase)
                        {
                            // --------------------------------------------------
                            //  Scan — skip whitespace between statements
                            // --------------------------------------------------
                            case RestorePhase.Scan:
                                if (b == (byte)' ' || b == (byte)'\t' ||
                                    b == (byte)'\r' || b == (byte)'\n')
                                    break; // skip

                                // Non-whitespace: start accumulating a statement
                                stmtLen = 0;
                                AppendByte(ref stmtBuf, ref stmtLen, ref stmtCap, b);
                                phase = RestorePhase.InStatement;

                                // Peek-ahead for -- comment (second char comes next iteration)
                                // handled in InStatement below
                                break;

                            // --------------------------------------------------
                            //  InStatement — accumulate until ';' (outside quotes)
                            // --------------------------------------------------
                            case RestorePhase.InStatement:
                                if (b == (byte)';')
                                {
                                    // Statement complete — trim trailing whitespace and execute
                                    ExecuteStatement(stmtBuf, stmtLen);
                                    stmtLen = 0;
                                    phase = RestorePhase.Scan;
                                    break;
                                }

                                AppendByte(ref stmtBuf, ref stmtLen, ref stmtCap, b);

                                if (b == (byte)'\'') { phase = RestorePhase.InSingleQuote; openQuote = b; break; }
                                if (b == (byte)'"') { phase = RestorePhase.InDoubleQuote; openQuote = b; break; }
                                if (b == (byte)'`') { phase = RestorePhase.InBacktick; openQuote = b; break; }

                                // Detect -- line comment
                                if (b == (byte)'-' && stmtLen >= 2 &&
                                    stmtBuf[stmtLen - 2] == (byte)'-')
                                {
                                    phase = RestorePhase.InLineComment;
                                    break;
                                }

                                // Detect /* block comment
                                if (b == (byte)'*' && stmtLen >= 2 &&
                                    stmtBuf[stmtLen - 2] == (byte)'/')
                                {
                                    phase = RestorePhase.InBlockComment;
                                    break;
                                }
                                break;

                            // --------------------------------------------------
                            //  InSingleQuote / InDoubleQuote / InBacktick
                            //  Accumulate everything; watch for escape and close.
                            // --------------------------------------------------
                            case RestorePhase.InSingleQuote:
                            case RestorePhase.InDoubleQuote:
                            case RestorePhase.InBacktick:
                                AppendByte(ref stmtBuf, ref stmtLen, ref stmtCap, b);

                                if (b == (byte)'\\' && phase != RestorePhase.InBacktick)
                                {
                                    // Backslash escape: next byte is literal, don't close quote
                                    quoteReturn = phase;
                                    phase = RestorePhase.AfterEscape;
                                    break;
                                }

                                // Check for closing quote (same byte as opening)
                                if (b == openQuote)
                                {
                                    phase = RestorePhase.InStatement;
                                }
                                break;

                            // --------------------------------------------------
                            //  AfterEscape — the byte after '\' inside a quote
                            // --------------------------------------------------
                            case RestorePhase.AfterEscape:
                                AppendByte(ref stmtBuf, ref stmtLen, ref stmtCap, b);
                                phase = quoteReturn;
                                break;

                            // --------------------------------------------------
                            //  InLineComment — skip to end of line, then resume
                            // --------------------------------------------------
                            case RestorePhase.InLineComment:
                                // Still accumulate so the statement text is preserved
                                // (the comment is part of, e.g., a /*!40101 ... */ construct)
                                AppendByte(ref stmtBuf, ref stmtLen, ref stmtCap, b);
                                if (b == (byte)'\n')
                                {
                                    // If the whole statement so far is only a comment, discard it
                                    if (IsOnlyComment(stmtBuf, stmtLen))
                                    {
                                        stmtLen = 0;
                                        phase = RestorePhase.Scan;
                                    }
                                    else
                                    {
                                        phase = RestorePhase.InStatement;
                                    }
                                }
                                break;

                            // --------------------------------------------------
                            //  InBlockComment / AfterStar
                            // --------------------------------------------------
                            case RestorePhase.InBlockComment:
                                AppendByte(ref stmtBuf, ref stmtLen, ref stmtCap, b);
                                if (b == (byte)'*') phase = RestorePhase.AfterStar;
                                break;

                            case RestorePhase.AfterStar:
                                AppendByte(ref stmtBuf, ref stmtLen, ref stmtCap, b);
                                if (b == (byte)'/')
                                {
                                    // End of block comment.
                                    // If the accumulated buffer is only this comment, discard.
                                    if (IsOnlyComment(stmtBuf, stmtLen))
                                    {
                                        stmtLen = 0;
                                        phase = RestorePhase.Scan;
                                    }
                                    else
                                    {
                                        phase = RestorePhase.InStatement;
                                    }
                                }
                                else if (b != (byte)'*')
                                {
                                    phase = RestorePhase.InBlockComment;
                                }
                                // if b == '*' stay in AfterStar
                                break;
                        }
                    }
                }

                // Flush any final statement that wasn't terminated by ';'
                if (stmtLen > 0)
                {
                    int trimmed = TrimRight(stmtBuf, stmtLen);
                    if (trimmed > 0)
                        ExecuteStatement(stmtBuf, trimmed);
                }
            }
            finally
            {
                SimpleBufferPool.Shared.Return(readBuf);
                SimpleBufferPool.Shared.Return(stmtBuf);
            }
        }

        // =========================================================================
        //  ExecuteStatement
        //  Sends one statement to MySQL via COM_QUERY and reads the response.
        //  The 'buf' slice must NOT include the trailing semicolon.
        // =========================================================================
        void ExecuteStatement(byte[] buf, int len)
        {
            // Trim trailing whitespace
            len = TrimRight(buf, len);
            if (len == 0) return;

            // Skip pure-whitespace or empty
            if (IsBlank(buf, len)) return;

            // Build COM_QUERY packet:  0x03 | sql_bytes
            byte[] cmd = new byte[1 + len];
            cmd[0] = 0x03;   // COM_QUERY
            Buffer.BlockCopy(buf, 0, cmd, 1, len);

            byte seq = 0;
            Packet.Write(_tcp, cmd, ref seq);

            // Read server response
            ReadQueryResponse(len);

            OnStatementExecuted?.Invoke(len);
        }

        // =========================================================================
        //  ReadQueryResponse
        //  Reads and discards the full server response after COM_QUERY.
        //  Handles:
        //    0x00  OK packet          — nothing to drain
        //    0xFF  ERR packet         — throws (or calls OnStatementError)
        //    0xFB  LOCAL INFILE req   — not expected, treated as error
        //    N     result set         — drain all column defs + all rows + EOF/OK
        // =========================================================================
        void ReadQueryResponse(int stmtLen)
        {
            byte seq;
            byte[] pkt = Packet.Read(_tcp, out seq);

            byte first = pkt[0];

            if (first == 0x00 || (first == 0xFE && pkt.Length < 9))
                return;   // OK or EOF — done

            if (first == 0xFF)
            {
                // ERR packet: 0xFF | error_code (2) | '#' | sqlstate (5) | message
                ushort code = (ushort)(pkt[1] | (pkt[2] << 8));
                int msgStart = 3;
                if (msgStart < pkt.Length && pkt[msgStart] == (byte)'#') msgStart += 6; // skip sqlstate
                string msg = Encoding.UTF8.GetString(pkt, msgStart, pkt.Length - msgStart);

                bool cont = OnStatementError?.Invoke(code, msg) ?? false;
                if (!cont)
                    throw new Exception("MySQL error " + code + " restoring statement (" + stmtLen + " bytes): " + msg);
                return;
            }

            // Result set: first packet is column count (lenenc int).
            // Drain: column defs → EOF/OK → rows → EOF/OK
            int pos = 0;
            ulong colCount = ReadLenEncInt(pkt, ref pos);

            bool deprecateEof = _conn.DeprecateEof;

            // Drain column definition packets (one per column)
            for (ulong c = 0; c < colCount; c++)
                Packet.Read(_tcp, out seq);

            // Drain column EOF (absent when DEPRECATE_EOF is set)
            if (!deprecateEof)
                Packet.Read(_tcp, out seq);

            // Drain row packets until EOF/OK
            while (true)
            {
                byte[] row = Packet.Read(_tcp, out seq);
                byte m = row[0];
                if (m == 0xFE && row.Length < 9) break;   // EOF terminator
                if (m == 0x00 && deprecateEof) break;   // OK terminator (DEPRECATE_EOF)
                if (m == 0xFF) break;                      // ERR — already broken
            }
        }

        // =========================================================================
        //  Helpers
        // =========================================================================

        /// <summary>
        /// Append one byte to the statement buffer.
        /// Doubles the rented buffer when capacity is reached.
        /// </summary>
        static void AppendByte(ref byte[] buf, ref int len, ref int cap, byte b)
        {
            if (len == cap)
            {
                int newCap = cap * 2;
                byte[] newBuf = SimpleBufferPool.Shared.Rent(newCap);
                Buffer.BlockCopy(buf, 0, newBuf, 0, len);
                SimpleBufferPool.Shared.Return(buf);
                buf = newBuf;
                cap = newCap;
            }
            buf[len++] = b;
        }

        /// <summary>
        /// Returns the length after stripping trailing whitespace bytes.
        /// Does not modify the buffer.
        /// </summary>
        static int TrimRight(byte[] buf, int len)
        {
            while (len > 0)
            {
                byte b = buf[len - 1];
                if (b == (byte)' ' || b == (byte)'\t' ||
                    b == (byte)'\r' || b == (byte)'\n')
                    len--;
                else
                    break;
            }
            return len;
        }

        /// <summary>
        /// Returns true if buf[0..len) contains only whitespace bytes.
        /// </summary>
        static bool IsBlank(byte[] buf, int len)
        {
            for (int i = 0; i < len; i++)
            {
                byte b = buf[i];
                if (b != (byte)' ' && b != (byte)'\t' &&
                    b != (byte)'\r' && b != (byte)'\n')
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Returns true if buf[0..len) is only a SQL comment (-- or /* */)
        /// possibly surrounded by whitespace — i.e. nothing worth executing.
        /// </summary>
        static bool IsOnlyComment(byte[] buf, int len)
        {
            int i = 0;
            // Skip leading whitespace
            while (i < len && (buf[i] == (byte)' ' || buf[i] == (byte)'\t' ||
                                buf[i] == (byte)'\r' || buf[i] == (byte)'\n'))
                i++;

            if (i + 1 >= len) return true;  // empty or single char

            // -- line comment
            if (buf[i] == (byte)'-' && buf[i + 1] == (byte)'-') return true;

            // /* block comment (must also end with */)
            if (buf[i] == (byte)'/' && buf[i + 1] == (byte)'*')
            {
                // MySQL conditional-execution comments — /*! ... */ and the versioned
                // form /*!NNNNN ... */ — are NOT throwaway comments: the server executes
                // their contents (this is how dump headers/footers like
                // "/*!40101 SET NAMES utf8mb4 */;" take effect). Keep them as statements;
                // only a plain /* ... */ comment is safe to discard.
                if (i + 2 < len && buf[i + 2] == (byte)'!')
                    return false;

                int t = TrimRight(buf, len);
                if (t >= 2 && buf[t - 1] == (byte)'/' && buf[t - 2] == (byte)'*')
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Decode a MySQL length-encoded integer from buf at position pos.
        /// Advances pos past the encoded bytes.
        /// </summary>
        static ulong ReadLenEncInt(byte[] buf, ref int pos)
        {
            byte b = buf[pos++];
            if (b < 0xFB) return b;
            if (b == 0xFC) { ushort v = (ushort)(buf[pos] | (buf[pos + 1] << 8)); pos += 2; return v; }
            if (b == 0xFD) { uint v = (uint)(buf[pos] | (buf[pos + 1] << 8) | (buf[pos + 2] << 16)); pos += 3; return v; }
            ulong w = 0;
            for (int i = 0; i < 8; i++) w |= (ulong)buf[pos + i] << (i * 8);
            pos += 8;
            return w;
        }
    }
}
