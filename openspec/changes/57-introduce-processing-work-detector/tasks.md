## 1. Reconcile prerequisites and ownership

- [x] 1.1 Bind to applied blocks 50/54/55/56 and inventory the landed temporary gate, pre-admission detection and cancellation/drain order, scheduler-local no-work/failure closure, admitted processing lifecycle, role registration slices, exact-count repository boundary, and test fakes; preserve block 50's superseding contract.
- [x] 1.2 Record the block-57 edit surface and preserve archived block 56, public settings, worker protocol, executor eligibility, and heavy geodata behavior; permit only required exact dependency-policy contract/factory entries and references without weakening or duplicating enforcement.

## 2. Add the lightweight detector contract

- [x] 2.1 Add dependency-light immutable `ProcessingWorkDetectionRequest` and `ProcessingWorkDetectionSnapshot` types carrying only the scheduled trigger plus closed current scheduled-launch and full-eligibility logical values; exclude RunId/JobId because detection precedes identity and admission.
- [x] 2.2 Add immutable `ProcessingWorkDetectionResult` and bounded diagnostics types with `HasWork`, implementation kind, logical coverage, and `UsedFallback`; exclude counts, duration, SQL/schema/connection data, arbitrary strings, exceptions, row/cursor identity, and work sets.
- [x] 2.3 Add `IProcessingWorkDetector.DetectAsync` with the exact existing preflight cancellation token and exhaustive rejection of unsupported trigger/purpose/coverage values; keep it internal to the dependency-light control plane and out of worker protocol/public configuration.
- [x] 2.4 Add contract tests for immutability, supported/unsupported values, safe metadata bounds, and proof that only `HasWork` can control launch decisions.

## 3. Implement the behavior-preserving adapter

- [x] 3.1 Implement one stateless count-backed detector that calls the landed exact eligibility count once with the exact token and returns `HasWork = count > 0` with constant count-backed/full-eligibility/no-fallback metadata.
- [x] 3.2 Verify predicate parity for null city and country, present latitude and longitude, and non-deleted assets, including near-miss cases for each predicate; change no SQL or eligibility rule in block 57.
- [x] 3.3 Prove the adapter retains no request/result/connection state and performs no Immich mutation, skipped-store/config/batch/protocol/backend/worker/geodata/cache/airport operation or detector-state persistence.
- [x] 3.4 Prove matching cancellation and repository failure propagate distinctly and are never converted to a successful no-work result.

## 4. Replace the temporary scheduled gate in place

- [x] 4.1 Migrate the one landed pre-admission scheduled call site to pass the exact immutable scheduled detection request/snapshot and existing preflight token to the new detector exactly once.
- [x] 4.2 Preserve detection before identity/admission/pending/arming, preflight cancellation/shutdown drain, and the positive path's shared admission, active-handle/CTS publication, frozen plan, immediate `MarkPending()`, exact-request arming, lazy backend dispatch, and matching cleanup; do not move detection into cron/execution/worker code.
- [x] 4.3 Feed only `result.HasWork` into existing branches; preserve logger-only no-work/cancel/failure closure with zero run artifacts, positive-detection admission loss with zero pending/child, and admitted abandonment/worker Busy/exact-handle cleanup. Leave safe diagnostics observational.
- [x] 4.4 Remove the temporary gate call path and fakes after migration; if a short compatibility alias is needed during implementation, remove it within this change and prove one underlying query per detection.

## 5. Preserve count authority, races, and trigger boundaries

- [x] 5.1 Keep Dashboard statistics on the exact repository count and the child executor on its independent authoritative exact count/zero gate; add no count, work set, settings, schedule data, detector metadata, or SQL detail to the worker request.
- [x] 5.2 Add deterministic positive-Web/worker-zero, positive-detection/admission-loss, and negative-Web/work-appears-later tests proving advisory non-atomic behavior, one child at most, closed pre-admission no-work with unchanged ProcessingState, and no fallback, replay, replacement, catch-up, resubmission, or retry.
- [x] 5.3 Prove Dashboard manual processing bypasses detection while retaining its admitted child lifecycle and that a statistics refresh remains a separate exact-count operation.
- [x] 5.4 Prove Web-only starts no scheduler/detector activity for any saved schedule and public Run-once/private-worker composition neither registers nor invokes the Web scheduled detector before its advisory lock and authoritative count.

## 6. Register one stateless singleton and add reusable fakes

- [x] 6.1 Register the concrete count-backed detector once as a singleton and map `IProcessingWorkDetector` to that exact instance in the landed Standard scheduling composition without making it hosted, eager, scoped, or disposable.
- [x] 6.2 Preserve the landed scheduler concrete/hosted alias identity and verify provider construction/startup performs no detector query, PostgreSQL connection, worker launch, settings read, or heavy dependency resolution.
- [x] 6.3 Add thread-safe constant, scripted FIFO, gated capture, matching-cancellation, throwing, counting, and fail-on-use detector fakes that expose results rather than counts and use signals rather than sleeps.
- [x] 6.4 Add concurrent invocation tests proving singleton statelessness, exact pre-admission request/snapshot/token forwarding, independent completion, and no mutable last-result or cursor state.
- [x] 6.5 Reuse block 56's dependency/constructor guards with required exact contract/factory manifest updates, showing the detector and Standard adapter close only over approved lightweight control-plane/repository dependencies without weakening or duplicating the policy.

## 7. Verify scope and future extension boundary

- [x] 7.1 Add compatibility tests demonstrating that a fake existence implementation can replace the count-backed adapter without changing caller/finalizer behavior or returning SQL/count data; do not implement the block-58 query.
- [x] 7.2 Add contract-shape tests proving the finalized request/result exposes only current full-eligibility coverage and contains no watermark source, incremental coverage, reconciliation identity, cursor representation, persistence, or NAS-specific schedule mode; preserve the no-go decisions in blocks 61–64.
- [x] 7.3 Run focused detector/repository/coordinator/scheduler/composition tests, relevant block-35/36 and mode-matrix suites, and `npm run test` with normal exclusions.
- [x] 7.4 Run `openspec validate 57-introduce-processing-work-detector --strict` and `openspec status --change 57-introduce-processing-work-detector`; review the block-57 diff, including only its required exact policy maintenance, and confirm archived block 56 and blocks 58–64 were not edited.
