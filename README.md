<h1 align="center">MySqlBackup.NET.RawBytes</h1>

<p align="center">
  <strong>The zero-dependency successor to <a href="https://github.com/MySqlBackupNET/MySqlBackup.Net">MySqlBackup.NET</a>.</strong><br>
  A pure-C# MySQL backup &amp; restore engine that speaks the MySQL wire protocol directly —<br>
  no <code>MySql.Data</code>, no Connector/NET, no external process. Just bytes on a socket.
</p>

<p align="center">
  <a href="#"><img alt="NuGet" src="https://img.shields.io/badge/nuget-MySqlBackup.NET.RawBytes-blue"></a>
  <a href="#"><img alt="License" src="https://img.shields.io/badge/license-Unlicense-green"></a>
  <a href="#"><img alt="Targets" src="https://img.shields.io/badge/.NET-Framework%204.8%20%7C%20.NET%208%2B-512bd4"></a>
  <a href="#"><img alt="Dependencies" src="https://img.shields.io/badge/dependencies-0-brightgreen"></a>
</p>

---

## Why this exists

The original **MySqlBackup.NET** has been trusted by the C# community for over a decade to back up and restore MySQL databases. It is excellent — but it is built **on top of** a MySQL ADO.NET driver (`MySql.Data` / Connector/NET). That means:

- Every value the server sends is materialized into a `DataReader`, boxed into a `.NET` object, converted to a string, then re-escaped back into SQL text. **The bytes make a round trip through the type system they never needed to take.**
- You inherit the driver's version constraints, its bugs, its breaking changes, and its memory profile.
- Large tables mean large allocations: a row becomes objects, objects become strings, strings become a `StringBuilder`, and the GC pays for all of it.

**MySqlBackup.NET.RawBytes** throws that whole layer away.

It implements the MySQL client/server protocol itself — handshake, authentication, `COM_QUERY`, result-set framing, length-encoded integers — and reads raw result bytes **straight off the TCP stream into a pooled buffer**, transforming them into SQL dump text in a single forward pass. Nothing is boxed. Nothing is converted to a `string` unless it has to be. For a backup tool, where the job is literally *"move bytes from the server into a file,"* this is the architecture the problem always wanted.

---

## Highlights

| | |
|---|---|
| 🚀 **Zero dependencies** | No `MySql.Data`, no Connector/NET, no `mysqldump.exe`. The entire client lives in a handful of `.cs` files under `Wire/`. |
| 🧠 **True streaming** | Rows are read one wire-packet at a time and written immediately. A 10 GB table dumps in roughly constant memory. |
| ♻️ **Allocation-free hot path** | Row buffers are rented from `ArrayPool<byte>`. Cell values are written **directly from the packet buffer** — numeric raw, binary as `0x…` hex, text quoted/escaped in place. No `byte[][]` accumulation, no per-row garbage. |
| 🔐 **Modern auth built in** | `mysql_native_password` **and** `caching_sha2_password` (MySQL 8 default), including the RSA-OAEP public-key full-auth exchange — implemented from scratch, no driver required. |
| 🌍 **Correct by construction** | UTF-8 / `utf8mb4` throughout (世界 🌍 round-trips exactly), UTC `TIMESTAMP` handling, binary/BLOB/BIT/GEOMETRY as hex, mysqldump-compatible header/footer `SET` blocks. |
| 🔁 **Streaming restore too** | The restore engine parses a `.sql` dump from any `Stream` and executes it statement-by-statement over the same raw connection — quote-, comment-, and `/*! … */`-aware, without loading the file into memory. |
| 🤝 **mysqldump-compatible output** | Dumps restore cleanly via the `mysql` CLI, and dumps produced by `mysqldump` / classic MySqlBackup.NET restore cleanly here. |
| 🎯 **Verified round-trips** | `CHECKSUM TABLE` is byte-identical between source and restored databases across Unicode, emoji, NULLs, blobs, and large multi-batch tables. |

---

## Architecture at a glance

```
┌─────────────────────────────────────────────────────────────┐
│                      Your application                         │
└───────────────┬──────────────────────────┬──────────────────┘
                │                          │
      StreamingDumpEngine        StreamingRestoreEngine
   (server bytes ─► SQL text)   (SQL text ─► server bytes)
                │                          │
                └───────────┬──────────────┘
                            │
                      Wire/  (the from-scratch MySQL client)
   ┌────────────┬───────────┬────────────┬──────────────────┐
   │ MySqlConn  │  Packet   │ Handshake  │ ResultSet /      │
   │ (connect/  │ (framing, │ (native +  │ ColumnDef        │
   │  auth/query)│ lenenc)  │ sha2 auth) │ (type decoding)  │
   └────────────┴───────────┴────────────┴──────────────────┘
                            │
                       raw TCP socket
                            │
                       MySQL server
```

The library is split into two namespaces:

- **`Wire/`** — a minimal, self-contained MySQL protocol client. Packet framing with multi-packet (`0xFFFFFF`) reassembly, length-encoded integers, the v10 handshake, both auth plugins, and result-set parsing with full `enum_field_types` knowledge for emission decisions.
- **`Dump/`** — the engines that turn result bytes into SQL and back:
  - `StreamingDumpEngine` — the flagship. A packet-driven state machine that never accumulates rows.
  - `StreamingRestoreEngine` — a byte-level SQL tokenizer that streams statements to the server.
  - `SqlEscape` — byte-level mysqldump-convention value escaping.

---

## Quick start

### Back up a database

```csharp
using MySqlBackup.NET.RawBytes.Wire;
using MySqlBackup.NET.RawBytes.Dump;

using (var conn = new MySqlConn())
{
    conn.Open("127.0.0.1", 3306, "root", "password", "my_database");

    var engine = new StreamingDumpEngine(conn, conn.RawStream)
    {
        // All options default to mysqldump-like behaviour
        InsertLineBreakBetweenInserts = false,  // combined multi-row INSERTs
        WriteComments                 = false,  // set true for -- comment blocks
        MaxInsertBytes                = 512 * 1024
    };

    using (var file = File.Create("my_database.sql"))
        engine.DumpDatabase("my_database", file);
}
```

### Restore a database

```csharp
using (var conn = new MySqlConn())
{
    conn.Open("127.0.0.1", 3306, "root", "password", "my_database");

    var restore = new StreamingRestoreEngine(conn, conn.RawStream)
    {
        OnStatementError = (code, msg) =>
        {
            Console.Error.WriteLine($"MySQL error {code}: {msg}");
            return true;   // continue past errors; return false to abort
        }
    };

    using (var file = File.OpenRead("my_database.sql"))
        restore.RestoreDatabase(file);
}
```

That's the whole API surface for the common case: **open, dump, done.**

---

## Dump options

Both the streaming and buffered engines expose the same option surface, mirroring `ExportInformations` from the classic library so migration is painless.

| Option | Default | Effect |
|---|---|---|
| `DropTable` | `true` | Emit `DROP TABLE IF EXISTS` before each table |
| `CreateTable` | `true` | Emit `CREATE TABLE IF NOT EXISTS …` |
| `DumpRows` | `true` | Emit row data as `INSERT` statements |
| `InsertLineBreakBetweenInserts` | `false` | `false` → combined one-line batches; `true` → one value-tuple per line |
| `MaxInsertBytes` | `512 KB` | Soft cap that splits a table's data into multiple `INSERT` batches |
| `WriteComments` | `false` | Emit human-readable `-- Definition of …` comment blocks |
| `RecordDumpTime` | `true` | When comments are on, write the `-- Dump created on …` line (turn off for byte-reproducible dumps) |
| `RemoveAutoIncrement` | `false` | Strip `AUTO_INCREMENT=N` from `CREATE TABLE` |
| `RemoveTableCharset` | `false` | Strip `DEFAULT CHARSET` / `CHARACTER SET` / `COLLATE` from `CREATE TABLE` |
| `Headers` / `Footers` | mysqldump block | Session `SET` boilerplate (charset save, `SET NAMES utf8mb4`, UTC, FK/UNIQUE off, restore) |

### Combined vs. split layout

```sql
-- InsertLineBreakBetweenInserts = false  (default, COMBINED)
INSERT INTO `t` (`a`, `b`) VALUES (1,2), (3,4), (5,6);

-- InsertLineBreakBetweenInserts = true   (SPLIT)
INSERT INTO `t` (`a`, `b`) VALUES
(1,2),
(3,4),
(5,6);
```

---

## How the value emission works

Every cell is written from the raw wire bytes using the column's declared type — no intermediate string:

- **Numeric** columns (`INT`, `DECIMAL`, `DOUBLE`, `YEAR`, …) → the server already sends ASCII digits in the text protocol, so the bytes are piped straight through, unquoted.
- **Binary** columns (`BLOB`, `BIT`, `GEOMETRY`, binary `CHAR`/`VARCHAR` via charset `63`) → hex-encoded in place as `0xDEADBEEF`.
- **Text** columns → wrapped in `'…'` with mysqldump-convention escaping (`'`→`''`, plus `\\ \0 \n \r \Z`) applied byte-by-byte. Because the escape bytes never collide with UTF-8 continuation bytes, this is both correct and fast for `utf8mb4`.
- **NULL** → the literal `NULL`.

`TIMESTAMP` correctness is handled at the session level: the engine sets `TIME_ZONE='+00:00'` before reading rows (matching the UTC line in the header block) and restores the original zone afterward, so timestamps round-trip without shifting between servers.

---

## Compatibility

- **Servers:** MySQL 5.7, MySQL 8.x (including the `caching_sha2_password` default), and MariaDB.
- **Frameworks:** .NET Framework 4.8 and .NET 8+ (the wire layer uses only `System.*` primitives; `ArrayPool`/`Span` come from `System.Buffers`).
- **Output:** compatible with the `mysql` CLI and interoperable with dumps from `mysqldump` and the original MySqlBackup.NET.

---

## Migrating from MySqlBackup.NET

The classic library wraps a `MySqlCommand`/`MySqlConnection` you supply. This one **is** the connection, so you drop the driver entirely:

```csharp
// Before — classic MySqlBackup.NET (requires MySql.Data)
using (var conn = new MySqlConnection(connString))
using (var cmd  = new MySqlCommand())
using (var mb   = new MySqlBackup(cmd))
{
    cmd.Connection = conn;
    conn.Open();
    mb.ExportToFile("dump.sql");
}

// After — MySqlBackup.NET.RawBytes (no driver at all)
using (var conn = new MySqlConn())
{
    conn.Open("127.0.0.1", 3306, "root", "password", "db");
    new StreamingDumpEngine(conn, conn.RawStream).DumpDatabase("db", File.Create("dump.sql"));
}
```

Option names (`InsertLineBreakBetweenInserts`, `RemoveAutoIncrement`, header/footer blocks, …) are intentionally kept aligned with `ExportInformations` so existing configuration maps over directly.

---

## Project layout

```
Wire/
  MySqlConn.cs      Connect, authenticate, run queries; exposes RawStream
  Packet.cs         Wire framing, length-encoded int/string read & write
  Handshake.cs      v10 handshake + native & caching_sha2 auth (incl. RSA)
  Capabilities.cs   Client/server capability flags
  ResultSet.cs      Column-def parsing, type decoding, buffered result reads
Dump/
  StreamingDumpEngine.cs    Packet-driven, allocation-free dump state machine
  StreamingRestoreEngine.cs Streaming SQL restore engine
  SqlEscape.cs              Byte-level value escaping
```

A console **test harness** (`Program.cs`) drives the engines through a dozen option scenarios and writes the results to `dumps/NN_*.sql` for inspection and round-trip verification. The harness is *not* the product — it is a demonstration and regression check for the engines above.

---

## Roadmap

- Single-call convenience facade (`MySqlBackup.Export(...)` / `.Import(...)`)
- TLS connection support
- Per-table and per-row progress events on the public surface
- Optional gzip output stream wrapper
- NuGet packaging for .NET Framework + .NET 8 multi-target

---

## Acknowledgements

Inspired by and intended as the spiritual successor to the long-serving **[MySqlBackup.NET](https://github.com/MySqlBackupNET/MySqlBackup.Net)** by a generation of contributors who made reliable MySQL backups easy for C# developers. This project carries that mission forward with a leaner, driver-free core.

---

## License

This project is released into the **public domain** under [The Unlicense](https://unlicense.org). You are free to copy, modify, publish, use, compile, sell, or distribute it, for any purpose, commercial or non-commercial, with or without attribution. See [`LICENSE`](LICENSE).

<p align="center"><sub>Built for the global C# &amp; MySQL community. Contributions, issues, and benchmarks welcome.</sub></p>
