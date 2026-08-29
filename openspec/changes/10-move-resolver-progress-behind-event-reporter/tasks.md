## 1. Verify Immutable Prerequisites

- [ ] 1.1 Verify blocks 6–9 are present in source and their focused tests pass, including block 6 cancellation/OOM classification, block 8 run-session log/activity/no-op/broken semantics, and block 9's singleton adapter plus one-session main-pass routing; stop rather than recreate or modify those contracts.
- [ ] 1.2 Record the finalized `IProcessingRunEventSession` log, activity, cleanup, and failure APIs that block 10 will consume, and identify the exact block-9 method parameter carrying the already-open session into per-asset work.

## 2. Replace the Resolver Progress Boundary

- [ ] 2.1 Replace `IAdministrativeAreaResolutionProgress` with explicit no-report and run-session `AdministrativeAreaResolverService.ResolveAsync` overloads, keeping reporting invocation-local and preserving the cancellation-only call shape for non-processing consumers.
- [ ] 2.2 Convert every existing resolver/cache progress message to an awaited Information session log with unchanged text and ordering, while a missing session performs no reporting and an explicit no-op session remains behaviorally inert.
- [ ] 2.3 Convert non-ready Overture/GADM cache waits to finalized async session activity scopes, preserving exact Downloading-versus-Waiting source labels, unique opaque identities, no activity for AlreadyReady, and matching non-cancelled end cleanup on every unwind path.
- [ ] 2.4 Restructure tolerant source catches only as needed to keep reporter faults, active caller cancellation, and OOM propagating while preserving Overture failure propagation, GADM ordinary unavailability/fallback, foreign cancellation-like classification, readiness/query ordering, and shared cache ownership.

## 3. Remove the Direct State Bridge Without Rerouting Block 9

- [ ] 3.1 Thread block 9's already-open run session through the existing per-asset call into the resolver; do not create a request, arm/open a session, or emit any duplicate lifecycle, log, disposition, or terminal event.
- [ ] 3.2 Remove nested `ProcessingResolutionProgress` and verify neither the resolver nor either cache service references `ProcessingState`; retain the background service's block-9 control-plane state uses.
- [ ] 3.3 Verify production DI still resolves the exact block-9 reporter/adapter singleton and concrete/hosted background-service singleton, with unchanged resolver/cache lifetimes and no new reporter registration or scoped correlation owner.
- [ ] 3.4 Verify `Lookup.razor` and its page-local cache/status helpers are unchanged and cannot obtain or emit to an active processing session.

## 4. Add Deterministic Resolver Reporting Coverage

- [ ] 4.1 Replace territory-test `RecordingProgress` usage with the finalized recording run session and retain all resolver result, territory, source-preference, and exact readiness/query assertions.
- [ ] 4.2 Add controlled StartedDownload, AwaitedExistingDownload, and AlreadyReady tests for both sources, asserting exact Information messages, source-specific labels, one opaque identity per accepted start, matching ends, and readiness only after successful waits.
- [ ] 4.3 Add signal-gated concurrent tests for equal-label and Overture/GADM overlapping activities, release them out of order, and prove each survivor remains visible/recorded until its own matching end without asserting unrelated global order.
- [ ] 4.4 Add deterministic matrices for ordinary GADM unavailability/fallback, propagating Overture failure, active cancellation, foreign cancellation-like failure, OOM, and begin/log/end reporter faults; assert accepted activity cleanup, no false readiness, no reporter-to-source normalization, and no recursive broken-session reporting.
- [ ] 4.5 Add no-report, explicit no-op-session, concurrent different/no-session, one-session production-routing, and Lookup-overlap isolation tests using controlled gates rather than sleeps, live downloads, or timing assumptions.

## 5. Validate Scope and Regressions

- [ ] 5.1 Run the focused administrative resolver, processing reporter/adapter, ProcessingState, ProcessingBackgroundService, cancellation, cache, DI, and Lookup tests under the normal default exclusions.
- [ ] 5.2 Run `npm run test` and record any failure proven unrelated to block 10.
- [ ] 5.3 Run `openspec validate 10-move-resolver-progress-behind-event-reporter --strict` and `openspec status --change 10-move-resolver-progress-behind-event-reporter`.
- [ ] 5.4 Review the implementation diff to prove only block 10 changed: no block-9 main-pass rerouting, new event vocabulary, cache synchronization/source-order/result changes, Lookup coupling, DI lifetime changes, or implementation/test edits from any other numbered block.
