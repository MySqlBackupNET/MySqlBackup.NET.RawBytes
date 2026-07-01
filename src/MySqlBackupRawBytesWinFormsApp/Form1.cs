using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using MySqlBackup.NET.RawBytes.Wire;
using MySqlBackup.NET.RawBytes.Dump;

namespace MySqlBackupRawBytesWinFormsApp
{
    // =====================================================================
    //  Single-screen UI for both EXPORT (dump) and IMPORT (restore) using
    //  the MySqlBackup.NET.RawBytes streaming engines.
    //
    //  - Connection string + options are persisted to "settings.txt" (INI)
    //    next to the EXE, 3 seconds after the last edit (debounced).
    //  - Any keystroke/paste/toggle restarts the 3s timer.
    // =====================================================================
    public partial class Form1 : Form
    {
        // ---- input controls ----
        TextBox _txtConn;
        TextBox _txtFile;
        Button _btnSelectFile;
        Button _btnExport;
        Button _btnImport;
        TextBox _txtLog;

        // ---- option controls ----
        CheckBox _chkDropTable;
        CheckBox _chkCreateTable;
        CheckBox _chkDumpRows;
        CheckBox _chkWriteComments;
        CheckBox _chkRemoveAutoInc;
        CheckBox _chkRemoveCharset;
        CheckBox _chkSplitInserts;
        CheckBox _chkRecordDumpTime;
        NumericUpDown _numMaxInsertBytes;

        // ---- debounced settings save ----
        readonly System.Windows.Forms.Timer _saveTimer = new System.Windows.Forms.Timer();
        bool _loadingSettings;   // suppress save while populating controls at startup

        static string SettingsPath
        {
            get
            {
                // Alongside the EXE.
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt");
            }
        }

        public Form1()
        {
            InitializeComponent();   // from Form1.Designer.cs (size/title stub)
            BuildUi();

            _saveTimer.Interval = 3000;         // 3 seconds
            _saveTimer.Tick += SaveTimer_Tick;

            LoadSettings();
        }

        // -----------------------------------------------------------------
        //  UI construction (done in code to keep the designer file minimal)
        // -----------------------------------------------------------------
        void BuildUi()
        {
            SuspendLayout();

            Text = "MySqlBackup.NET.RawBytes — Export / Import";
            ClientSize = new Size(720, 560);
            MinimumSize = new Size(560, 480);
            Font = new Font("Segoe UI", 9f);

            int margin = 12;
            int width = ClientSize.Width - margin * 2;
            int y = margin;

            // ---- connection string ----
            var lblConn = new Label
            {
                Text = "MySQL connection string:",
                Left = margin,
                Top = y,
                Width = width,
                Height = 18,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            y += 20;

            _txtConn = new TextBox
            {
                Left = margin,
                Top = y,
                Width = width,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _txtConn.TextChanged += AnyInput_Changed;
            y += 30;

            var lblConnHint = new Label
            {
                Text = "e.g. Server=127.0.0.1;Port=3306;Uid=root;Pwd=secret;Database=mydb;",
                Left = margin,
                Top = y,
                Width = width,
                Height = 16,
                ForeColor = SystemColors.GrayText,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            y += 26;

            // ---- options group ----
            var grp = new GroupBox
            {
                Text = "Export options",
                Left = margin,
                Top = y,
                Width = width,
                Height = 156,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            _chkDropTable      = MakeCheck("DROP TABLE before CREATE", 16, 22, true);
            _chkCreateTable    = MakeCheck("CREATE TABLE statements", 16, 46, true);
            _chkDumpRows       = MakeCheck("Dump row data (INSERTs)", 16, 70, true);
            _chkWriteComments  = MakeCheck("Write comments", 16, 94, false);
            _chkRecordDumpTime = MakeCheck("Record dump timestamp", 16, 118, false);

            _chkRemoveAutoInc  = MakeCheck("Remove AUTO_INCREMENT", 270, 22, false);
            _chkRemoveCharset  = MakeCheck("Remove table charset", 270, 46, false);
            _chkSplitInserts   = MakeCheck("Split INSERT (one row per line)", 270, 70, false);

            var lblMax = new Label
            {
                Text = "Max INSERT bytes:",
                Left = 270,
                Top = 96,
                Width = 110,
                Height = 20
            };
            _numMaxInsertBytes = new NumericUpDown
            {
                Left = 384,
                Top = 94,
                Width = 110,
                Minimum = 1024,
                Maximum = 67108864,    // 64 MB
                Increment = 1024,
                Value = 524288         // 512 KB (engine default)
            };
            _numMaxInsertBytes.ValueChanged += AnyInput_Changed;

            grp.Controls.Add(_chkDropTable);
            grp.Controls.Add(_chkCreateTable);
            grp.Controls.Add(_chkDumpRows);
            grp.Controls.Add(_chkWriteComments);
            grp.Controls.Add(_chkRecordDumpTime);
            grp.Controls.Add(_chkRemoveAutoInc);
            grp.Controls.Add(_chkRemoveCharset);
            grp.Controls.Add(_chkSplitInserts);
            grp.Controls.Add(lblMax);
            grp.Controls.Add(_numMaxInsertBytes);
            y += grp.Height + 10;

            // ---- file path ----
            var lblFile = new Label
            {
                Text = "SQL file path (export target / import source):",
                Left = margin,
                Top = y,
                Width = width,
                Height = 18,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            y += 20;

            _txtFile = new TextBox
            {
                Left = margin,
                Top = y,
                Width = width - 110,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _txtFile.TextChanged += AnyInput_Changed;

            _btnSelectFile = new Button
            {
                Text = "Select File…",
                Left = margin + width - 100,
                Top = y - 1,
                Width = 100,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnSelectFile.Click += BtnSelectFile_Click;
            y += 34;

            // ---- action buttons ----
            _btnExport = new Button
            {
                Text = "Export as File",
                Left = margin,
                Top = y,
                Width = 140,
                Height = 30,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            _btnExport.Click += BtnExport_Click;

            _btnImport = new Button
            {
                Text = "Import from File",
                Left = margin + 150,
                Top = y,
                Width = 140,
                Height = 30,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            _btnImport.Click += BtnImport_Click;
            y += 40;

            // ---- log ----
            _txtLog = new TextBox
            {
                Left = margin,
                Top = y,
                Width = width,
                Height = ClientSize.Height - y - margin,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            Controls.Add(lblConn);
            Controls.Add(_txtConn);
            Controls.Add(lblConnHint);
            Controls.Add(grp);
            Controls.Add(lblFile);
            Controls.Add(_txtFile);
            Controls.Add(_btnSelectFile);
            Controls.Add(_btnExport);
            Controls.Add(_btnImport);
            Controls.Add(_txtLog);

            ResumeLayout(false);
            PerformLayout();
        }

        CheckBox MakeCheck(string text, int left, int top, bool chk)
        {
            var c = new CheckBox
            {
                Text = text,
                Left = left,
                Top = top,
                Width = 240,
                Height = 20,
                Checked = chk
            };
            c.CheckedChanged += AnyInput_Changed;
            return c;
        }

        // -----------------------------------------------------------------
        //  Debounced save: any input restarts the 3s countdown
        // -----------------------------------------------------------------
        void AnyInput_Changed(object sender, EventArgs e)
        {
            if (_loadingSettings) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        void SaveTimer_Tick(object sender, EventArgs e)
        {
            _saveTimer.Stop();
            try
            {
                SaveSettings();
                Log("Settings saved to " + SettingsPath);
            }
            catch (Exception ex)
            {
                Log("Failed to save settings: " + ex.Message);
            }
        }

        // -----------------------------------------------------------------
        //  Settings persistence (simple INI)
        // -----------------------------------------------------------------
        void SaveSettings()
        {
            var sb = new StringBuilder();
            sb.AppendLine("; MySqlBackup.NET.RawBytes WinForms settings");
            sb.AppendLine("; Saved " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("[Connection]");
            sb.AppendLine("ConnectionString=" + _txtConn.Text);
            sb.AppendLine("FilePath=" + _txtFile.Text);
            sb.AppendLine();
            sb.AppendLine("[Options]");
            sb.AppendLine("DropTable=" + _chkDropTable.Checked);
            sb.AppendLine("CreateTable=" + _chkCreateTable.Checked);
            sb.AppendLine("DumpRows=" + _chkDumpRows.Checked);
            sb.AppendLine("WriteComments=" + _chkWriteComments.Checked);
            sb.AppendLine("RecordDumpTime=" + _chkRecordDumpTime.Checked);
            sb.AppendLine("RemoveAutoIncrement=" + _chkRemoveAutoInc.Checked);
            sb.AppendLine("RemoveTableCharset=" + _chkRemoveCharset.Checked);
            sb.AppendLine("InsertLineBreakBetweenInserts=" + _chkSplitInserts.Checked);
            sb.AppendLine("MaxInsertBytes=" + ((long)_numMaxInsertBytes.Value).ToString(CultureInfo.InvariantCulture));

            File.WriteAllText(SettingsPath, sb.ToString(), new UTF8Encoding(false));
        }

        void LoadSettings()
        {
            if (!File.Exists(SettingsPath)) return;

            _loadingSettings = true;
            try
            {
                var ini = ParseIni(File.ReadAllLines(SettingsPath));

                _txtConn.Text = Get(ini, "ConnectionString", "");
                _txtFile.Text = Get(ini, "FilePath", "");

                _chkDropTable.Checked      = GetBool(ini, "DropTable", true);
                _chkCreateTable.Checked    = GetBool(ini, "CreateTable", true);
                _chkDumpRows.Checked       = GetBool(ini, "DumpRows", true);
                _chkWriteComments.Checked  = GetBool(ini, "WriteComments", false);
                _chkRecordDumpTime.Checked = GetBool(ini, "RecordDumpTime", false);
                _chkRemoveAutoInc.Checked  = GetBool(ini, "RemoveAutoIncrement", false);
                _chkRemoveCharset.Checked  = GetBool(ini, "RemoveTableCharset", false);
                _chkSplitInserts.Checked   = GetBool(ini, "InsertLineBreakBetweenInserts", false);

                long mib;
                if (long.TryParse(Get(ini, "MaxInsertBytes", "524288"),
                                  NumberStyles.Integer, CultureInfo.InvariantCulture, out mib))
                {
                    if (mib < (long)_numMaxInsertBytes.Minimum) mib = (long)_numMaxInsertBytes.Minimum;
                    if (mib > (long)_numMaxInsertBytes.Maximum) mib = (long)_numMaxInsertBytes.Maximum;
                    _numMaxInsertBytes.Value = mib;
                }

                Log("Settings loaded from " + SettingsPath);
            }
            catch (Exception ex)
            {
                Log("Failed to load settings: " + ex.Message);
            }
            finally
            {
                _loadingSettings = false;
            }
        }

        // Minimal INI: flat key=value map (sections ignored; keys unique here).
        static Dictionary<string, string> ParseIni(string[] lines)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in lines)
            {
                string line = raw == null ? "" : raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[') continue;     // section header
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1);   // keep value verbatim (no trim — connection strings may matter)
                d[key] = val;
            }
            return d;
        }

        static string Get(Dictionary<string, string> d, string key, string fallback)
        {
            string v;
            return d.TryGetValue(key, out v) ? v : fallback;
        }

        static bool GetBool(Dictionary<string, string> d, string key, bool fallback)
        {
            string v;
            if (!d.TryGetValue(key, out v)) return fallback;
            bool b;
            return bool.TryParse(v.Trim(), out b) ? b : fallback;
        }

        // -----------------------------------------------------------------
        //  Connection string parsing → discrete parts for MySqlConn.Open
        // -----------------------------------------------------------------
        class ConnParts
        {
            public string Host = "127.0.0.1";
            public int Port = 3306;
            public string User = "root";
            public string Password = "";
            public string Database = "";
        }

        static ConnParts ParseConnString(string cs)
        {
            var p = new ConnParts();
            if (string.IsNullOrWhiteSpace(cs)) return p;

            foreach (var part in cs.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = part.Substring(0, eq).Trim().ToLowerInvariant();
                string v = part.Substring(eq + 1).Trim();

                switch (k)
                {
                    case "server":
                    case "host":
                    case "data source":
                    case "datasource":
                    case "addr":
                    case "address":
                        p.Host = v;
                        break;
                    case "port":
                        int pt;
                        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out pt)) p.Port = pt;
                        break;
                    case "uid":
                    case "user":
                    case "user id":
                    case "userid":
                    case "username":
                        p.User = v;
                        break;
                    case "pwd":
                    case "password":
                        p.Password = v;
                        break;
                    case "database":
                    case "initial catalog":
                        p.Database = v;
                        break;
                }
            }
            return p;
        }

        // -----------------------------------------------------------------
        //  File picker
        // -----------------------------------------------------------------
        void BtnSelectFile_Click(object sender, EventArgs e)
        {
            // Use Save dialog so a not-yet-existing export path can be chosen,
            // but it equally serves as a source picker for import (typing also works).
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Select SQL file";
                dlg.Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*";
                dlg.OverwritePrompt = false;        // we manage overwrite ourselves on export
                dlg.CheckFileExists = false;
                dlg.CheckPathExists = false;

                if (!string.IsNullOrWhiteSpace(_txtFile.Text))
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(_txtFile.Text);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
                        dlg.FileName = Path.GetFileName(_txtFile.Text);
                    }
                    catch { /* ignore malformed path */ }
                }

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txtFile.Text = dlg.FileName;   // triggers debounce save
                }
            }
        }

        // -----------------------------------------------------------------
        //  EXPORT
        // -----------------------------------------------------------------
        void BtnExport_Click(object sender, EventArgs e)
        {
            string file = _txtFile.Text == null ? "" : _txtFile.Text.Trim();
            if (file.Length == 0)
            {
                MessageBox.Show(this, "Please enter or select a file path for the export target.",
                    "Missing file path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var cp = ParseConnString(_txtConn.Text);
            if (string.IsNullOrWhiteSpace(cp.Database))
            {
                MessageBox.Show(this, "The connection string must include a Database=… value to export.",
                    "Missing database", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Ensure the target directory exists; auto-create if not.
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(file));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    Log("Created directory: " + dir);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not create the target directory:\r\n" + ex.Message,
                    "Directory error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // snapshot options for the worker thread
            var opt = SnapshotOptions();

            RunBusy("Export", () =>
            {
                using (var conn = new MySqlConn())
                {
                    conn.Open(cp.Host, cp.Port, cp.User, cp.Password, cp.Database);
                    LogAsync("Connected to " + cp.Host + ":" + cp.Port + " / " + cp.Database
                             + " (server " + conn.ServerVersion + ")");

                    var engine = new StreamingDumpEngine(conn, conn.RawStream);
                    engine.DropTable = opt.DropTable;
                    engine.CreateTable = opt.CreateTable;
                    engine.DumpRows = opt.DumpRows;
                    engine.WriteComments = opt.WriteComments;
                    engine.RecordDumpTime = opt.RecordDumpTime;
                    engine.RemoveAutoIncrement = opt.RemoveAutoIncrement;
                    engine.RemoveTableCharset = opt.RemoveTableCharset;
                    engine.InsertLineBreakBetweenInserts = opt.SplitInserts;
                    engine.MaxInsertBytes = opt.MaxInsertBytes;

                    int tables = 0;
                    engine.OnTableComplete = (t, n) =>
                    {
                        tables++;
                        LogAsync("  • " + t + " — " + n + " row(s)");
                    };

                    using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write))
                    {
                        engine.DumpDatabase(cp.Database, fs);
                    }
                    LogAsync("Export complete: " + tables + " table(s) → " + file);
                }
            });
        }

        // -----------------------------------------------------------------
        //  IMPORT
        // -----------------------------------------------------------------
        void BtnImport_Click(object sender, EventArgs e)
        {
            string file = _txtFile.Text == null ? "" : _txtFile.Text.Trim();
            if (file.Length == 0)
            {
                MessageBox.Show(this, "Please enter or select the SQL file to import.",
                    "Missing file path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Import requires the source file to exist — warn and stop if not.
            if (!File.Exists(file))
            {
                MessageBox.Show(this, "The file does not exist:\r\n" + file
                    + "\r\n\r\nImport cannot proceed.",
                    "File not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var cp = ParseConnString(_txtConn.Text);
            if (string.IsNullOrWhiteSpace(cp.Database))
            {
                MessageBox.Show(this, "The connection string must include a Database=… value to import into.",
                    "Missing database", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            RunBusy("Import", () =>
            {
                using (var conn = new MySqlConn())
                {
                    conn.Open(cp.Host, cp.Port, cp.User, cp.Password, cp.Database);
                    LogAsync("Connected to " + cp.Host + ":" + cp.Port + " / " + cp.Database
                             + " (server " + conn.ServerVersion + ")");

                    var engine = new StreamingRestoreEngine(conn, conn.RawStream);
                    int executed = 0;
                    engine.OnStatementExecuted = n => { executed = n; };
                    engine.OnStatementError = (code, msg) =>
                    {
                        LogAsync("  ! SQL error " + code + ": " + msg);
                        return true;   // continue past individual statement errors
                    };

                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read))
                    {
                        engine.RestoreDatabase(fs);
                    }
                    LogAsync("Import complete: " + executed + " statement(s) executed from " + file);
                }
            });
        }

        // -----------------------------------------------------------------
        //  Option snapshot (plain struct-ish holder, thread-safe to pass)
        // -----------------------------------------------------------------
        class OptSnapshot
        {
            public bool DropTable, CreateTable, DumpRows, WriteComments, RecordDumpTime;
            public bool RemoveAutoIncrement, RemoveTableCharset, SplitInserts;
            public int MaxInsertBytes;
        }

        OptSnapshot SnapshotOptions()
        {
            return new OptSnapshot
            {
                DropTable = _chkDropTable.Checked,
                CreateTable = _chkCreateTable.Checked,
                DumpRows = _chkDumpRows.Checked,
                WriteComments = _chkWriteComments.Checked,
                RecordDumpTime = _chkRecordDumpTime.Checked,
                RemoveAutoIncrement = _chkRemoveAutoInc.Checked,
                RemoveTableCharset = _chkRemoveCharset.Checked,
                SplitInserts = _chkSplitInserts.Checked,
                MaxInsertBytes = (int)Math.Min(int.MaxValue, (long)_numMaxInsertBytes.Value)
            };
        }

        // -----------------------------------------------------------------
        //  Run a blocking engine call off the UI thread with busy-state mgmt
        // -----------------------------------------------------------------
        void RunBusy(string what, Action work)
        {
            SetBusy(true);
            Log("── " + what + " started ──");

            var th = new Thread(() =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    LogAsync(what + " FAILED: " + ex.Message);
                    BeginInvoke((Action)(() =>
                        MessageBox.Show(this, what + " failed:\r\n" + ex.Message,
                            what + " error", MessageBoxButtons.OK, MessageBoxIcon.Error)));
                }
                finally
                {
                    BeginInvoke((Action)(() => SetBusy(false)));
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        void SetBusy(bool busy)
        {
            _btnExport.Enabled = !busy;
            _btnImport.Enabled = !busy;
            _btnSelectFile.Enabled = !busy;
            _txtConn.Enabled = !busy;
            _txtFile.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        // -----------------------------------------------------------------
        //  Logging (UI-thread safe)
        // -----------------------------------------------------------------
        void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + "\r\n";
            if (_txtLog.InvokeRequired)
            {
                _txtLog.BeginInvoke((Action)(() => { _txtLog.AppendText(line); }));
            }
            else
            {
                _txtLog.AppendText(line);
            }
        }

        // Always marshals — for use from worker threads.
        void LogAsync(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + "\r\n";
            try
            {
                _txtLog.BeginInvoke((Action)(() => { _txtLog.AppendText(line); }));
            }
            catch (InvalidOperationException) { /* handle not yet created / disposed */ }
        }
    }
}
