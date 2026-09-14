## 1. Verify prerequisites and map ownership

- [x] 1.1 Verify blocks 7–9 are applied in source and focused tests pass; apply block 10 first, then re-read its finalized reporter-backed administrative resolver API. Stop rather than duplicate or revise any missing prerequisite contract.
- [x] 1.2 Map every current pass operation in order: count, eligibility, zero gate, skipped snapshot, config snapshot, keyset batches/cursor, suppression, parallel assets, batch delay, terminal finish; record startup/schedule/contention/pending/request-arm/lock/CTS operations that must remain in the host.
- [x] 1.3 Map every per-asset branch and causal order, including administrative-first resolution, airport containment override/non-containing fallback, WithFallbackCity, verbose pre-write Trace, PostgreSQL write, skipped SQLite insert, the retained logger-only no-city compatibility guard and its post-fallback unreachability, warnings, handled failures, active cancellation, and critical exceptions.

## 2. Establish the execution boundary and deterministic seams

- [x] 2.1 Add ProcessingRunExecutor.ExecuteAsync(ProcessingRunRequest, IProcessingEventReporter, CancellationToken) returning the matching ProcessingRunResult; inject deterministic zero-offset UTC time and keep all invocation state local.
- [x] 2.2 Add or reuse narrow executor-facing seams for config read, eligibility/batch/write repository operations, skipped snapshot/add, finalized administrative resolution, and airport infrastructure lookup; do not move query, cache, source-ordering, or geometry logic into the executor.
- [x] 2.3 Register the stateless executor as singleton and alias collaborator interfaces to the exact existing singleton instances (or one stateless singleton adapter) so repository, resolver/cache, places, reporter-adapter, and hosted-service ownership is not duplicated.

## 3. Move the authoritative pass intact

- [x] 3.1 Open one run event session before the count, report eligibility only after successful count, preserve the zero short circuit before skipped/config reads, and return Completed/Cancelled/Failed results with session-owned committed counts.
- [x] 3.2 Preserve one skipped-ID snapshot and one non-empty-run config snapshot, AssetCursor.Initial, current database predicate/order, cursor advancement before suppression, batch size, clamped parallelism, run-local Updated count in batch logs, configured post-batch delay, and eventual empty fetch.
- [x] 3.3 Move the complete asset helper with its step diagnostics and preserve administrative resolution → optional airport lookup → city/state/country fallback → write/skip order and every existing warning/Trace/logger-only boundary, retaining the post-fallback no-city guard structurally while proving state/country fallback writes make it unreachable.
- [x] 3.4 Commit Updated only after a successful independent Immich write, reachable skipped-store branches only after successful independent SQLite insertion, Failed after a handled per-asset exception, and nothing for suppressed/cancellation-interrupted assets; prove the retained post-fallback no-city guard has no reachable disposition.
- [x] 3.5 Preserve active-token cancellation, unrelated cancellation-like failure, block-6 critical-exception propagation, ordinary finalized provider fallbacks, non-cancelled activity/disposition/terminal cleanup, partial persisted effects, and no transaction/retry/rollback additions.
- [x] 3.6 Propagate a broken reporter/session without recursive reporting or direct ProcessingState repair; do not return a falsely observed terminal result after required terminal acceptance fails.

## 4. Delegate from the existing host without moving control-plane ownership

- [x] 4.1 Remove count, config, skipped snapshot, batching, asset resolution, airport, persistence, and pipeline event logic from ProcessingBackgroundService; delegate accepted manual and scheduled requests to the executor in-process.
- [x] 4.2 Retain skipped-database startup initialization, cron calculation/waits, direct startup/schedule/contention logs, run lock, immediate MarkPending, block-9 request creation/adapter arming, manual run CTS, CancelRun, fire-and-forget manual dispatch, and lock release on every terminal/exception path.
- [x] 4.3 Preserve the existing concrete-singleton plus hosted-service alias registration and Dashboard-facing manual trigger/cancel surface; do not introduce block-12 scheduler seams or the block-13 coordinator.

## 5. Verify extraction deterministically

- [x] 5.1 Use fake seams, fixed time, recording/fault-injection reporter sessions, and TaskCompletionSource gates—never sleeps or live PostgreSQL/SQLite/geodata/cron/Blazor—to verify call order and parallel completion races.
- [x] 5.2 Verify zero count calls only count/session/terminal paths; no skipped/config/batch/resolver/airport/write/delay call occurs and zero accounting completes.
- [x] 5.3 Verify one representative mixed paged run: suppressed IDs are uncounted, cursor and delay behavior are unchanged, admin precedes airport, containment/fallback rules are preserved, writes and skipped inserts precede their dispositions, and terminal aggregate accounting is coherent.
- [x] 5.4 Verify active cancellation before disposition returns Cancelled with prior committed effects, unrelated OCE follows ordinary failure classification, handled asset failures can complete, critical/pass-level failure returns Failed without an extra per-asset failure, and open activities close before terminal.
- [x] 5.5 Verify reporter failure propagates without recursion/direct-state fallback, host manual/scheduled admission invokes the executor exactly once with the armed request/reporter/token, rejected triggers invoke it zero times, and locks remain recoverable through existing host tests.
- [x] 5.6 Verify `ReferenceEquals` for every direct-singleton factory alias, including the singleton reporter adapter and concrete/hosted `ProcessingBackgroundService`; where a thin adapter is necessary, verify all its interfaces alias that one adapter rather than claiming identity with its wrapped service. Confirm executor construction requires neither ProcessingState, Blazor, cron, nor a hosted service.

## 6. Validate compatibility and scope

- [x] 6.1 Run focused executor extraction, ProcessingBackgroundService, reporter/adapter, resolver-progress, and Phase 1 lifecycle/state tests using the repository's Microsoft.Testing.Platform command form.
- [x] 6.2 Run npm run test with default Integration/Performance exclusions; run integration tests only if an integration-covered path actually changes.
- [x] 6.3 Run openspec validate 11-extract-processing-run-executor --strict and require success.
- [x] 6.4 Review the diff to prove no block-10 artifact/code edits, schedule redesign, coordinator, work detector, UI/CTS/lock ownership move, protocol/process concern, database/schema/query change, transaction/retry behavior, geometry/source-ordering change, or other numbered-block work entered this change.
- [x] 6.5 Leave the exhaustive reusable scheduler-free executor fixture and broader empty/success/cancel/fatal matrix to block 14 while ensuring this block exposes the required deterministic seams.
