## 1. Reconcile prerequisites and ownership

- [ ] 1.1 Verify blocks 35–55 are applied and inventory the landed temporary scheduled gate, coordinator predispatch order, local finalizers, processing request, Standard/Web-only/Run-once registration slices, exact-count repository boundary, and test fakes; stop if these seams are absent rather than editing the pre-migration `ProcessingBackgroundService` monolith.
- [ ] 1.2 Record the block-57 edit surface and confirm block 56's artifacts/implementation, public settings, worker protocol, executor eligibility, and heavy geodata graph will remain untouched.

## 2. Add the lightweight detector contract

- [ ] 2.1 Add dependency-light immutable `ProcessingWorkDetectionRequest` and `ProcessingWorkDetectionSnapshot` types carrying only the existing admitted processing identity/trigger plus closed current scheduled-launch and full-eligibility logical values.
- [ ] 2.2 Add immutable `ProcessingWorkDetectionResult` and bounded diagnostics types with `HasWork`, implementation kind, logical coverage, and `UsedFallback`; exclude counts, duration, SQL/schema/connection data, arbitrary strings, exceptions, row/cursor identity, and work sets.
- [ ] 2.3 Add `IProcessingWorkDetector.DetectAsync` with the admitted cancellation token and exhaustive rejection of unsupported purpose/coverage values; keep it internal to the dependency-light control plane and out of worker protocol/public configuration.
- [ ] 2.4 Add contract tests for immutability, supported/unsupported values, safe metadata bounds, and proof that only `HasWork` can control launch decisions.

## 3. Implement the behavior-preserving adapter

- [ ] 3.1 Implement one stateless count-backed detector that calls the landed exact eligibility count once with the exact token and returns `HasWork = count > 0` with constant count-backed/full-eligibility/no-fallback metadata.
- [ ] 3.2 Verify predicate parity for null city and country, present latitude and longitude, and non-deleted assets, including near-miss cases for each predicate; change no SQL or eligibility rule in block 57.
- [ ] 3.3 Prove the adapter retains no request/result/connection state and performs no Immich mutation, skipped-store/config/batch/protocol/backend/worker/geodata/cache/airport operation or detector-state persistence.
- [ ] 3.4 Prove matching cancellation and repository failure propagate distinctly and are never converted to a successful no-work result.

## 4. Replace the temporary scheduled gate in place

- [ ] 4.1 Migrate the one block-35 scheduled predispatch call site to pass the exact admitted processing request, current immutable detection snapshot, and coordinator-owned token to the new detector exactly once.
- [ ] 4.2 Preserve active-handle/CTS publication, frozen plan/backend state, immediate `MarkPending()`, exact-request state arming, detector invocation, lazy backend resolution, and matching cleanup order without moving detection before admission or into cron/execution/worker code.
- [ ] 4.3 Feed only `result.HasWork` into the existing local no-work or positive-child branch; leave safe diagnostics observational and preserve the existing zero-state/log, cancellation, failure, abandonment, worker Busy, and exact-handle-release behavior.
- [ ] 4.4 Remove the temporary gate call path and fakes after migration; if a short compatibility alias is required by the landed API, resolve both interfaces to one singleton and prove one underlying query before removing the alias within this change.

## 5. Preserve count authority, races, and trigger boundaries

- [ ] 5.1 Keep Dashboard statistics on the exact repository count and the child executor on its independent authoritative exact count/zero gate; add no count, work set, settings, schedule data, detector metadata, or SQL detail to the worker request.
- [ ] 5.2 Add deterministic positive-Web/worker-zero and negative-Web/work-appears-later tests proving advisory non-atomic behavior, one child at most, closed local no-work, and no fallback, replay, replacement, catch-up, resubmission, or retry.
- [ ] 5.3 Prove Dashboard manual processing bypasses detection while retaining its admitted child lifecycle and that a statistics refresh remains a separate exact-count operation.
- [ ] 5.4 Prove Web-only starts no scheduler/detector activity for any saved schedule and public Run-once/private-worker composition neither registers nor invokes the Web scheduled detector before its advisory lock and authoritative count.

## 6. Register one stateless singleton and add reusable fakes

- [ ] 6.1 Register the concrete count-backed detector once as a singleton and map `IProcessingWorkDetector` to that exact instance in the landed Standard scheduling composition without making it hosted, eager, scoped, or disposable.
- [ ] 6.2 Preserve the landed scheduler concrete/hosted alias identity and verify provider construction/startup performs no detector query, PostgreSQL connection, worker launch, settings read, or heavy dependency resolution.
- [ ] 6.3 Add thread-safe constant, scripted FIFO, gated capture, matching-cancellation, throwing, counting, and fail-on-use detector fakes that expose results rather than counts and use signals rather than sleeps.
- [ ] 6.4 Add concurrent invocation tests proving singleton statelessness, exact request/snapshot/token identity, independent completion, and no mutable last-result or cursor state.
- [ ] 6.5 Add dependency/constructor guards showing the detector contract and Standard adapter close only over approved lightweight control-plane/repository dependencies and do not weaken or duplicate block 56's parallel architecture-policy work.

## 7. Verify scope and future extension boundary

- [ ] 7.1 Add compatibility tests demonstrating that a fake existence implementation can replace the count-backed adapter without changing caller/finalizer behavior or returning SQL/count data; do not implement the block-58 query.
- [ ] 7.2 Add contract-shape tests proving the finalized request/result exposes only current full-eligibility coverage and contains no watermark source, incremental coverage, reconciliation identity, cursor representation, persistence, or NAS-specific schedule mode; preserve the no-go decisions in blocks 61–64.
- [ ] 7.3 Run focused detector/repository/coordinator/scheduler/composition tests, relevant block-35/36 and mode-matrix suites, and `npm run test` with normal exclusions.
- [ ] 7.4 Run `openspec validate 57-introduce-processing-work-detector --strict` and `openspec status --change 57-introduce-processing-work-detector`; review a block-57-only diff and confirm no code, artifact, or MASTERPLAN edit for block 56 or blocks 58–64.
