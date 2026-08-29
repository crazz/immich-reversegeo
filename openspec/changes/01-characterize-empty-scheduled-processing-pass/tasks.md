## 1. Internal test seam

- [ ] 1.1 Add friend-assembly access for `ImmichReverseGeo.Tests` and an internal invocation path for the existing pass method; do not expose a new public API.
- [ ] 1.2 Add the internal immutable operation set described in `design.md`, including the exact-count, config, skipped-store, batch, resolver, airport, skipped-write, and location-write calls needed to make unused operations fail fast in tests.
- [ ] 1.3 Wire the unchanged public production constructor to the existing concrete collaborator methods and keep `ExecuteAsync` and `TriggerRunAsync` calling the same pass in the same order.

## 2. Empty-pass characterization

- [ ] 2.1 Add a focused `ProcessingBackgroundService` MSTest whose count spy returns zero and verify it is awaited exactly once.
- [ ] 2.2 Configure every post-count operation to fail the test if invoked, including the configuration read and skipped-record loading, batch retrieval, resolver, airport, skipped-write, and location-write operations.
- [ ] 2.3 Assert `TotalUnprocessed`, processed, skipped, and error counts are zero; `IsRunning` is false; `LastError` is null; and start/completion timestamps are present after the pass.
- [ ] 2.4 Assert via `GetRecentLog()` that the nothing-to-process message precedes `Run complete. Processed=0 Skipped=0 Errors=0`, without matching timestamp prefixes.

## 3. Verification

- [ ] 3.1 Run `dotnet test --project tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj --filter "FullyQualifiedName~ProcessingBackgroundServiceTests"`.
- [ ] 3.2 Run `npm run test` and confirm the repository's default Integration/Performance exclusions remain in effect.
