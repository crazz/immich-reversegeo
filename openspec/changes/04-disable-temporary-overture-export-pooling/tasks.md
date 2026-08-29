## 1. Deterministic temporary-output seam and regression

- [ ] 1.1 Add an internal helper used by `ExportOvertureDivisions` to build and open the GUID temporary SQLite output connection, plus an internal exporter-operation constructor seam that leaves both public constructors and production DI behavior unchanged.
- [ ] 1.2 Expose only those internals to `ImmichReverseGeo.Tests` as needed; do not add a public exporter or provider abstraction.
- [ ] 1.3 Add a controlled exporter fixture in `OvertureDivisionCacheServiceTests.cs` that can open the real temporary-output helper, record each path/configuration, throw immediately after open, and later write a minimal valid one-row cache without DuckDB or network access.
- [ ] 1.4 Add a repeated-failure test proving unique temporary paths, faulted tasks, removed `*.tmp` files, no `CHE.db` publication, and a failing pre-fix assertion that every exact output connection has pooling disabled.

## 2. Disable temporary export pooling

- [ ] 2.1 Build the temporary output connection string with `SqliteConnectionStringBuilder` and set `Pooling = false`; change no other SQLite or DuckDB connection.
- [ ] 2.2 Preserve the existing DDL, metadata, row copy, transaction, validation, move-to-final, ready-cache, and release-lookup behavior; leave success-path `Close/ClearPool` removal outside this block unless required for correctness.
- [ ] 2.3 Extend the controlled test so removing the injected fault starts a new same-country attempt on a new temporary path, publishes `CHE.db`, makes `HasData("CHE")` true, and leaves readable row/release metadata.
- [ ] 2.4 Cover zero-row/invalid-output cleanup only if the seam naturally reaches those existing branches; do not add block 5 preflight/stale-task retry or block 6 cancellation behavior.

## 3. Verification

- [ ] 3.1 Run the focused regression five times: `for i in 1 2 3 4 5; do dotnet test --project tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj --filter "FullyQualifiedName~OvertureDivisionCacheServiceTests" || exit 1; done`.
- [ ] 3.2 Run `npm run test` with the repository's default Integration/Performance exclusions.
