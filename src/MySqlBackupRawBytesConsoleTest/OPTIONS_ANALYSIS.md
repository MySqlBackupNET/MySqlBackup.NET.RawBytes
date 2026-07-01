# ExportInformations → RAW-bytes engine: options port (final scope)

Source studied: `MySqlBackup.NET.RawBytes/MySqlBackupNET/InfoObjects/ExportInformations.cs`
Target: `MySqlBackup.NET.RawBytes/Dump/StreamingDumpEngine.cs` (raw-wire streaming).
Scope: **export to `INSERT INTO` only**.

`ExportInformations` exposes ~30 members. For an INSERT-only raw-bytes exporter, only
the set below is in scope; everything else is intentionally dropped (listed at the end).

---

## Implemented in the engine

| ExportInformations member        | Engine option                       | Notes |
|-----------------------------------|-------------------------------------|-------|
| `ExportTableStructure`            | `CreateTable`                       | |
| `AddDropTable`                    | `DropTable`                         | |
| `ExportRows`                      | `DumpRows`                          | |
| `EnableComment`                   | `WriteComments`                     | gates all `--` comments |
| `MaxSqlLength`                    | `MaxInsertBytes`                    | batch-size flush |
| `InsertLineBreakBetweenInserts`   | `InsertLineBreakBetweenInserts`     | combined (`false`, default) vs split (`true`) |
| `RecordDumpTime`                  | `RecordDumpTime`                    | separate toggle, gated by comments (see below) |
| `GetDocumentHeaders/Footers`      | `Headers` / `Footers` (plain `string`) | full session SET block (see below) |
| (engine extra)                    | `RemoveAutoIncrement`               | strips `AUTO_INCREMENT=` from DDL |
| (engine extra)                    | `RemoveTableCharset`                | strips `DEFAULT CHARSET=`/`COLLATE=` from DDL |

### Combined vs split layout — `InsertLineBreakBetweenInserts`
- `false` (default) → **COMBINED**: `INSERT INTO t (...) VALUES (a1,a2,a3), (b1,b2,b3), (c1,c2,c3);`
- `true`            → **SPLIT**:    `VALUES` then one `(tuple)` per line.

### Headers / Footers — plain `string`, not `string[]`
- Single settable `string` each; assigning **normalizes CRLF/CR → LF** so the dump never
  carries mixed line endings. Defaults (`DefaultHeaders` / `DefaultFooters`) are the full
  mysqldump-compatible block: charset/collation save + `SET NAMES utf8mb4`, UTC time zone,
  `SQL_MODE='NO_AUTO_VALUE_ON_ZERO'`, FK/UNIQUE/NOTES off — restored by the footer.
- `SET NAMES utf8mb4` is hardcoded (correct: `MySqlConn` always negotiates the connection
  as utf8mb4, so all wire bytes are utf8mb4 regardless of column charset).
- **Pairing requirement:** the header's `SET TIME_ZONE='+00:00'` only instructs the importer.
  The engine therefore also runs `SET TIME_ZONE='+00:00'` on the connection **before** reading
  rows (and restores the original zone afterward), so the dumped `TIMESTAMP` values are actually
  UTC. Verified: a value inserted as local `12:00:00` dumps as UTC `04:00:00` and round-trips
  back to `12:00:00` — the instant is preserved across server time zones.

### Dump time — `RecordDumpTime`
Kept as a separate boolean (default `true`, ignored unless `WriteComments`) rather than folded
into `WriteComments`. Reason: lets callers keep structural comments while suppressing the
non-deterministic timestamp line, so dumps stay **byte-reproducible** for diff/hash comparison.

---

## Dropped (out of scope for INSERT-only raw bytes)

Everything else in `ExportInformations` is intentionally not ported:

- **Routine/object export** — `ExportProcedures`, `ExportFunctions`, `ExportTriggers`,
  `ExportViews`, `ExportEvents`, `ScriptsDelimiter`, `ExportRoutinesWithoutDefiner`.
- **Non-INSERT row modes** — `RowsExportMode` (`INSERT IGNORE` / `REPLACE` /
  `ON DUPLICATE KEY UPDATE` / `UPDATE`).
- **Table/row selection** — `ExcludeTables`, `ExcludeRowsForTables`,
  `TablesToBeExportedList`, `TablesToBeExportedDic` (the explicit `tables` argument to
  `DumpDatabase` covers basic whitelisting for now).
- **Database-level DDL** — `AddCreateDatabase`, `AddDropDatabase`.
- **Transcoding / value hooks** — `TextEncoding` (raw bytes are utf8mb4 by design),
  `TableColumnValueAdjustments` (needs per-value object decode — defeats byte passthrough).
- **Consistency / progress / misc** — `WrapWithinTransaction`, `EnableLockTablesWrite`,
  `ResetAutoIncrement`, `GetTotalRowsMode`, `IntervalForProgressReport`
  (progress is served by the `OnTableComplete(table, rows)` callback),
  `EnableParallelProcessing`, and the deprecated `SetTimeZoneUTC`.
