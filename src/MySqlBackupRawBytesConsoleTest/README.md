# RawBytesDumpTest — console test harness for the RAW-bytes dump engine

Standalone console app that exercises the new RAW-bytes dump library
(`MySqlBackup.NET.RawBytes/Wire` + `MySqlBackup.NET.RawBytes/Dump`). The library
source is **linked** into this project (see `RawBytesDumpTest.csproj`) so the
`internal` engine classes are testable and compiled into one assembly — no DLL
reference, no code duplication. The new combined/split INSERT option therefore
lives in the real library, not a copy.

## Prerequisites

- .NET SDK 10 (`dotnet --version`)
- The local MySQL test server running on `127.0.0.1:3308`, `root` / `1234`,
  with the `rawbytes_test` database loaded from `seed.sql`:

  ```
  mysql --default-character-set=utf8mb4 -u root -p1234 -P 3308 -h 127.0.0.1 < seed.sql
  ```

  (`--default-character-set=utf8mb4` matters — without it the UTF-8 file is
  read as latin1 and the Unicode test rows get double-encoded.)

## Run

```
dotnet run -c Release
```

Dump files are written to `dumps\NN_*.sql`. Scenarios:

| File | What it shows |
|------|---------------|
| `01_combined_default`     | **Combined** single-line INSERTs (default) |
| `02_split_lines`          | **Split**: one value-tuple per line |
| `03_no_drop_table`        | `DropTable=false` |
| `04_data_only`            | rows only (no DROP / CREATE) |
| `05_structure_only`       | `DumpRows=false` |
| `06_strip_autoinc_charset`| `RemoveAutoIncrement` + `RemoveTableCharset` |
| `07_comments`             | `WriteComments=true` → includes `-- Dump created on` line |
| `07b_comments_no_dumptime`| comments on, `RecordDumpTime=false` → reproducible (no timestamp) |
| `08/09_*_smallbatch`      | tiny `MaxInsertBytes` → multiple INSERTs per table |
| `20_restore_source` + DB `rawbytes_restore_lib` | full round-trip: dump via `StreamingDumpEngine` → restore via `StreamingRestoreEngine` → fresh DB (no `mysql.exe`) |

Every dump carries the full mysqldump-compatible header/footer SET block
(`Headers`/`Footers`, plain strings normalized to `\n`), and the engine sets the
session time zone to UTC before reading rows so `TIMESTAMP` columns dump in UTC and
round-trip without shifting across servers.

## The new option

`StreamingDumpEngine.InsertLineBreakBetweenInserts`:

- `false` (default) → `INSERT INTO t (...) VALUES (a1,a2,a3), (b1,b2,b3), (c1,c2,c3);`
- `true`            → `INSERT INTO t (...) VALUES`<br>`(a1,a2,a3),`<br>`(b1,b2,b3),`<br>`(c1,c2,c3);`

## Verification

Both modes were round-trip imported into fresh databases; `CHECKSUM TABLE` is
byte-identical to the source across all tables (Unicode `世界 🌍` + emoji, single
quotes, backslashes, tabs, embedded newlines, BLOB/BINARY/BIT hex, NULLs,
decimals). See `OPTIONS_ANALYSIS.md` for the full ExportInformations port study.
