## 1. Deterministic lifecycle seams and failing regressions

- [ ] 1.1 Reuse block 4's Overture internal test access/export seam and add a separate internal full-source-operation delegate used by the task lifecycle wrapper; preserve both public constructors and production DI behavior.
- [ ] 1.2 Add the equivalent narrow full-source-operation delegate and test access for `GadmDivisionCacheService` without merging the source implementations or bypassing GADM's download/export pipeline in production.
- [ ] 1.3 Build source-specific controlled-operation fixtures that record task identity, invocation count, and owner token, signal entry, and use `TaskCompletionSource` gates for success, fault, and cancellation without sleeps, live network access, DuckDB, or provider timing.
- [ ] 1.4 Add failing Overture and GADM tests proving a controlled failure at the start of the shared inner operation (the repeated readiness-preflight boundary), followed by repair, returns a new same-ISO3 task and invokes source work a second time.
- [ ] 1.5 Add failing source-specific tests proving owner-task cancellation is retryable, while cancelling only one caller's `WaitAsync` leaves the active exact task joinable by a later caller.
- [ ] 1.6 Add gated concurrent-caller tests for both sources proving one exact task and one source invocation, one `StartedDownload` result, and `AwaitedExistingDownload` for all losing callers.
- [ ] 1.7 Add handcrafted valid-cache tests for both sources proving `AlreadyReady`, zero task creation/source-operation calls, and unchanged readable cache status.
- [ ] 1.8 For each source's production exact-remove helper, add a deterministic test that replaces an old same-country lazy in a standalone concurrent dictionary, runs cleanup for the old value, and proves the replacement remains; do not expose either service's private map.

## 2. Race-safe task-map ownership

- [ ] 2.1 In `OvertureDivisionCacheService`, create each candidate lazy before insertion, evaluate only the `GetOrAdd` winner, derive starter/awaiter status by candidate/winner reference identity, and wrap the complete inner operation in success/fault/cancellation cleanup.
- [ ] 2.2 Make Overture terminal cleanup atomically remove the exact country/lazy pair used by the production stale-value test helper; do not use key-only removal or a non-atomic check-then-remove sequence.
- [ ] 2.3 Apply the corresponding winning-lazy lifecycle and atomic exact-value cleanup to `GadmDivisionCacheService`, retaining its distinct country mapping and package-download/export flow.
- [ ] 2.4 Remove redundant caller-side map removal from both `EnsureDataAsync` methods while preserving `WaitAsync(callerToken)`, the winning owner's captured operation token, and direct resolver-call compatibility.
- [ ] 2.5 Keep the outer `HasData` ready fast path outside map ownership, but ensure the wrapper's first `try/finally` encloses the repeated inner readiness check, mapping, directory/path setup, and the existing source operation.
- [ ] 2.6 Preserve Overture's block-4 non-pooled `*.tmp` cleanup/validation/atomic publication and separately preserve GADM's `*.tmp` and `*.gpkg.download` cleanup, validation, and publication behavior.

## 3. Terminal and recovery completion coverage

- [ ] 3.1 Complete the Overture and GADM fault, owner-cancellation, waiter-cancellation, concurrency, and stale-removal tests against the production lifecycle wrapper and exact-remove helper.
- [ ] 3.2 For each source, have the controlled success operation publish a minimal source-valid final cache using the source test fixture with every schema/metadata element required by `HasData` and status; assert it is ready/readable, delete it through the existing service path, and prove a later same-country request receives a new task and second source invocation. Do not simulate publication by only completing the delegate or mutating ready state.
- [ ] 3.3 Retain source-specific artifact assertions: no invalid final cache after fault/cancellation, Overture temporary export cleanup remains intact, and GADM temporary database/package artifacts remain cleaned by their existing boundaries.
- [ ] 3.4 Confirm `AdministrativeAreaResolverService.ResolveOvertureAsync` and `ResolveGadmAsync` require no caller cleanup or ordering change and continue to await the service-owned task directly.

## 4. Verification

- [ ] 4.1 Run `dotnet test --project tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj --filter "FullyQualifiedName~OvertureDivisionCacheServiceTests"`.
- [ ] 4.2 Run `dotnet test --project tests/ImmichReverseGeo.Gadm.Tests/ImmichReverseGeo.Gadm.Tests.csproj --filter "FullyQualifiedName~GadmDivisionCacheServiceTests"`.
- [ ] 4.3 Repeat both focused commands five times to guard the concurrency and replacement cases against order sensitivity while keeping all test coordination signal-based.
- [ ] 4.4 Run `npm run test` with the repository's default Integration and Performance exclusions; live GADM integration downloads are not required for this block.
