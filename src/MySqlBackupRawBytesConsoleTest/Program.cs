using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MySqlBackup.NET.RawBytes.Dump;
using MySqlBackup.NET.RawBytes.Wire;

namespace RawBytesDumpTest
{
    // =====================================================================
    //  Console test harness for the RAW-bytes dump engine.
    //
    //  Connects to the local test server (127.0.0.1:3308, root/1234) and
    //  dumps the `rawbytes_test` database under a range of option sets,
    //  writing each result to dumps\NN_name.sql so the formatting can be
    //  eyeballed and round-trip imported.
    // =====================================================================
    internal static class Program
    {
        const string Host = "127.0.0.1";
        const int    Port = 3308;
        const string User = "root";
        const string Pass = "1234";
        const string Db   = "rawbytes_test";

        static string OutDir;

        static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            OutDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "dumps");
            OutDir = Path.GetFullPath(OutDir);
            Directory.CreateDirectory(OutDir);

            Console.WriteLine("RAW-bytes dump test harness");
            Console.WriteLine("Output folder: " + OutDir);
            Console.WriteLine(new string('=', 70));

            try
            {
                // ----- streaming engine scenarios -----
                RunStreaming("01_combined_default", o =>
                {
                    o.InsertLineBreakBetweenInserts = false;   // COMBINED (default)
                });

                RunStreaming("02_split_lines", o =>
                {
                    o.InsertLineBreakBetweenInserts = true;    // SPLIT one tuple per line
                });

                RunStreaming("03_no_drop_table", o =>
                {
                    o.DropTable = false;
                });

                RunStreaming("04_data_only", o =>
                {
                    o.DropTable = false;
                    o.CreateTable = false;
                });

                RunStreaming("05_structure_only", o =>
                {
                    o.DumpRows = false;
                });

                RunStreaming("06_strip_autoinc_charset", o =>
                {
                    o.RemoveAutoIncrement = true;
                    o.RemoveTableCharset = true;
                });

                RunStreaming("07_comments", o =>
                {
                    o.WriteComments = true;   // RecordDumpTime defaults true → timestamp line present
                });

                RunStreaming("07b_comments_no_dumptime", o =>
                {
                    o.WriteComments = true;
                    o.RecordDumpTime = false; // comments kept, timestamp suppressed → reproducible
                });

                RunStreaming("08_combined_smallbatch", o =>
                {
                    o.InsertLineBreakBetweenInserts = false;
                    o.MaxInsertBytes = 64;   // tiny → forces multiple INSERT statements per table
                });

                RunStreaming("09_split_smallbatch", o =>
                {
                    o.InsertLineBreakBetweenInserts = true;
                    o.MaxInsertBytes = 64;
                });

                // ----- full library round-trip: dump engine -> restore engine -----
                RestoreRoundTrip("rawbytes_restore_lib");

                Console.WriteLine(new string('=', 70));
                Console.WriteLine("All scenarios completed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                return 1;
            }
        }

        // -----------------------------------------------------------------
        //  Run the streaming (zero-alloc, raw-wire) engine
        // -----------------------------------------------------------------
        static void RunStreaming(string name, Action<StreamingDumpEngine> configure)
        {
            string path = Path.Combine(OutDir, name + ".sql");
            var rowCounts = new List<string>();

            using (var conn = new MySqlConn())
            {
                conn.Open(Host, Port, User, Pass, Db);
                var engine = new StreamingDumpEngine(conn, conn.RawStream);
                configure(engine);
                engine.OnTableComplete = (t, n) => rowCounts.Add(t + "=" + n);

                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    engine.DumpDatabase(Db, fs);
                }
            }

            Report("streaming", name, path, rowCounts);
        }

        // -----------------------------------------------------------------
        //  Full library round-trip:
        //    StreamingDumpEngine  → .sql file → StreamingRestoreEngine → fresh DB
        //  No mysql.exe involved — this exercises the library's own SQL parser
        //  and wire-level statement execution.
        // -----------------------------------------------------------------
        static void RestoreRoundTrip(string targetDb)
        {
            string dumpPath = Path.Combine(OutDir, "20_restore_source.sql");

            // 1) Produce a dump with the dump engine (combined default).
            using (var conn = new MySqlConn())
            {
                conn.Open(Host, Port, User, Pass, Db);
                var engine = new StreamingDumpEngine(conn, conn.RawStream);
                using (var fs = new FileStream(dumpPath, FileMode.Create, FileAccess.Write))
                    engine.DumpDatabase(Db, fs);
            }

            // 2) (Re)create the empty target database.
            using (var admin = new MySqlConn())
            {
                admin.Open(Host, Port, User, Pass, Db);
                admin.Query("DROP DATABASE IF EXISTS `" + targetDb + "`");
                admin.Query("CREATE DATABASE `" + targetDb + "` CHARACTER SET utf8mb4");
            }

            // 3) Restore the dump into the target DB using the restore engine.
            int statements = 0;
            var errors = new List<string>();
            using (var conn = new MySqlConn())
            {
                conn.Open(Host, Port, User, Pass, targetDb);
                var restore = new StreamingRestoreEngine(conn, conn.RawStream);
                restore.OnStatementExecuted = n => statements++;
                restore.OnStatementError = (code, msg) => { errors.Add(code + ": " + msg); return true; };
                using (var fs = new FileStream(dumpPath, FileMode.Open, FileAccess.Read))
                    restore.RestoreDatabase(fs);
            }

            Console.WriteLine($"[restore  ] {targetDb,-26} {statements,3} statements executed, {errors.Count} error(s)");
            foreach (var e in errors)
                Console.WriteLine("             ERROR " + e);
        }

        static void Report(string engine, string name, string path, List<string> rowCounts)
        {
            var fi = new FileInfo(path);
            Console.WriteLine($"[{engine,-9}] {name,-26} {fi.Length,8} bytes   rows: {string.Join(", ", rowCounts)}");
        }
    }
}
