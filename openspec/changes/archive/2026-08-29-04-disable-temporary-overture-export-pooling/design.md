## Context

See [proposal.md](proposal.md) for motivation and [specs/overture/temporary-export-resource-cleanup/spec.md](specs/overture/temporary-export-resource-cleanup/spec.md) for required behavior. `DownloadDataInternalAsync` creates `{ISO3}.{GUID}.tmp`, calls private `ExportOvertureDivisions`, validates the result, and moves it to `{ISO3}.db`; its catch deletes the temporary file. Inside the exporter, DuckDB is an in-memory, using-scoped reader. The Microsoft.Data.Sqlite output connection is opened with only `Data Source`, while `Close()` and `SqliteConnection.ClearPool(sqlite)` occur only after the transaction commits. Any DDL, metadata, row-copy, or commit exception after `Open()` bypasses that success-only pool clear; disposal closes the logical connection but a pooling-enabled provider may retain the native connection under the unique path.

Microsoft.Data.Sqlite 10.0.11 exposes `SqliteConnectionStringBuilder.Pooling`, `ClearPool`, and `ClearAllPools`, but no supported pool-count diagnostic. Existing Overture read connections, validation connections, and the GADM exporter already use `Pooling=false`. DuckDB.NET 1.5.0 exposes no corresponding pooling contract in the installed API documentation, so DuckDB lifetime is outside this defect boundary.

## Goals / Non-Goals

**Goals:**
- Prevent the one-shot temporary SQLite output from ever entering a reusable pool.
- Make the post-open failure boundary, cleanup, and repaired retry deterministic without Azure/Overture network access.
- Preserve public construction, cache content, and successful publication order.

**Non-Goals:**
- Change DuckDB setup, release discovery, cache schema/mapping, or final-cache read connections.
- Redesign in-flight task cleanup (block 5) or synchronous native cancellation (block 6).
- Use RSS, timing, reflection into provider internals, or cross-platform file-lock assumptions as acceptance oracles.

## Decisions

- Build the temporary output connection with `SqliteConnectionStringBuilder` and set `Pooling = false`, then use that connection only for the GUID `*.tmp` database. This is preferable to adding failure-path `ClearPool` calls because the provider never creates a path-specific pool. Keep the existing success `Close/ClearPool` sequence unless implementation cleanup proves it safely redundant; removing it is not required by this block.
- Extract an internal helper that creates and opens this temporary output connection. `ExportOvertureDivisions` must call the helper. An internal test can invoke the same helper for unique paths, inspect `new SqliteConnectionStringBuilder(connection.ConnectionString).Pooling`, inject a failure immediately after open, and dispose it. This is the stable provider-supported observable; Microsoft.Data.Sqlite exposes no public pool count.
- Add an internal exporter-operation constructor seam while leaving both public constructors and production DI behavior unchanged. Tests supply an operation that, on the first call, opens through the shared helper and throws; after repair it writes a minimal valid `division_area`/`_meta` database and returns one row. Grant only the test assembly internal visibility if needed. This drives the real `DownloadDataInternalAsync` cleanup, validation, move, and ready-state path without DuckDB or network access.
- Repeat the controlled failure with distinct generated temporary paths. For every attempt assert pooling is false, the thrown task faults, the observed `*.tmp` path is gone, and `CHE.db` is absent. Then disable the fault and assert a new path is used, `CHE.db` exists, `HasData("CHE")` is true, and metadata/status remain readable.

## Risks / Trade-offs

- [The public provider API cannot enumerate retained pools] → Verify the provider's supported non-pooling configuration on the exact connection helper used by production, plus deterministic file cleanup; any Windows handle check is supplementary only.
- [A test exporter seam could become an alternate production path] → Keep it internal, default it to the existing private exporter, and preserve the public constructors.
- [Retry assertions could overlap block 5] → Exercise only a failure inside the existing export try/finally, where this service already removes its in-flight entry; do not cover preflight failures or stale-task races.
- [SQLite cleanup in tests can be contaminated by unrelated pooled fixtures] → Use unique directories and non-pooled connections; do not call `ClearAllPools` as the behavior under test.

## Migration Plan

1. Add the internal connection helper/export-operation seam and a regression test that fails at the post-open boundary.
2. Set `Pooling = false` in the shared temporary-output helper and make the test pass.
3. Add the controlled repaired-retry/publication assertions and run the focused test repeatedly before the default suite.
4. Roll back by reverting the helper/seam and connection-string change; no persisted-data migration is involved.
