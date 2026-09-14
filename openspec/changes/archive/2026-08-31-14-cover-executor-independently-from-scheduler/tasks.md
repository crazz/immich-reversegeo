## 1. Reconcile applied prerequisites and inherited coverage

- [x] 1.1 Apply and re-read blocks 7–13, then inventory the finalized executor constructor, collaborator seams, result/reporter APIs, and all executor tests added by block 11.
- [x] 1.2 Map block 11’s direct-executor zero-count, representative mixed-run, cancellation/failure, and reporter-fault tests to this change’s matrix; retain host-delegation and DI-identity tests unchanged as regressions outside the shared fixture.
- [x] 1.3 Leave Phase 1 hosted lifecycle/state tests and blocks 7–10 contract/adapter/resolver tests in place; do not recreate their assertions in the direct-executor suite.

## 2. Build the reusable scheduler-free fixture

- [x] 2.1 Refactor only test code as needed so block 11 and block 14 executor tests share one direct-construction fixture with fail-fast unused collaborators.
- [x] 2.2 Add scriptable fakes for count/keyset batches, configuration and skipped snapshots, administrative and airport results, update and skipped persistence, and controllable batch delay, each with ordered call/effect history.
- [x] 2.3 Reuse the finalized recording/fault-injection reporter and fixed UTC seam; add asynchronous-continuation gates and a concurrency probe without sleeps or infrastructure.
- [x] 2.4 Add common assertions for exact request/result correlation, fixed UTC/order, count equation, outcome/failure-detail rules, activity cleanup, one healthy terminal event, and returned/reported result equality.

## 3. Cover empty, snapshots, batches, and cursor behavior

- [x] 3.1 Extend rather than duplicate block 11’s zero-count test to prove no skipped/config/batch/resolver/airport/persistence/delay calls and a completed empty result.
- [x] 3.2 Prove a non-empty run reads one skipped-ID snapshot and one configuration snapshot across multiple batches, unaffected by later fake source changes.
- [x] 3.3 Prove suppressed fetched IDs advance the keyset cursor but produce no resolver, persistence, disposition, or processed count.
- [x] 3.4 Prove the exact initial/successive/final-empty cursor sequence and one delay after every non-empty batch, only after that batch’s assets finish, with no delay after the empty sentinel.
- [x] 3.5 Prove positive eligibility followed by no fetched assets and lower/higher eligibility than fetched work terminate by batch responses while preserving the original eligibility event.

## 4. Cover bounded concurrency

- [x] 4.1 Add gated rows for parallelism below one, within range, and above thirty-two; assert observed active work is clamped to one, the configured bound, and thirty-two respectively.
- [x] 4.2 Release assets out of input order and assert only per-asset causal edges and complete accounting, not accidental global reporter order.
- [x] 4.3 Prove the next batch and its delay boundary cannot overtake unfinished work in the current batch.

## 5. Cover dispositions, source ordering, and fallback

- [x] 5.1 Add focused updated and no-country/no-admin skipped cases, plus an executable Country-fallback update and structural proof that the logger-only no-city guard remains unreachable; retain exact skipped-store and reporter boundaries.
- [x] 5.2 Add handled ordinary source failures for administrative resolution and airport lookup plus update/skipped persistence failures; assert one Failed disposition, one Error diagnostic, continued peer processing, and Completed outcome.
- [x] 5.3 At the same resolver boundary, inject an awaited activity/log reporter failure and prove it escapes as broken-session infrastructure failure rather than source unavailability or a Failed asset disposition.
- [x] 5.4 Prove administrative resolution precedes optional airport lookup and disabled airport configuration invokes no airport collaborator.
- [x] 5.5 Add separate rows for containing-airport override, non-containing airport preserving an admin city, and non-containing airport filling an absent admin city.
- [x] 5.6 Add executable City, State, and Country fallback rows to prove the exact final fallback order and update decisions, plus structural unreachable-guard proof; do not fabricate a no-fallback executor row.

## 6. Cover persistence order and partial effects

- [x] 6.1 Prove successful location and skipped-ID persistence each occurs before the matching disposition is accepted.
- [x] 6.2 Prove persistence failure produces no false Updated/Skipped disposition and follows the finalized ordinary-versus-critical taxonomy.
- [x] 6.3 Gate active cancellation during update and skipped persistence before success; prove no effect or disposition is recorded and the interrupted asset remains uncounted.
- [x] 6.4 Gate cancellation after successful update and skipped persistence but before disposition acceptance; prove the non-cancelled committed path publishes and counts each effect.
- [x] 6.5 Gate cancellation after a reachable committed handled Failed decision; prove its non-cancelled publication and count without inventing persistence or an unreachable no-city Skipped decision.
- [x] 6.6 Prove later cancellation or pass failure retains prior fake persistence effects and accepted counts without retry, rollback, compensation, or cross-store transaction.
- [x] 6.7 Inject a reporter disposition failure after persistence and prove the effect remains while the original reporter exception propagates without compensation or recursive terminal reporting.

## 7. Cover cancellation boundaries

- [x] 7.1 Add deterministic active-token cancellation rows before/during count, skipped/config snapshot, batch retrieval, administrative resolution, and airport lookup; assert eligibility/downstream calls and uncounted interrupted assets per boundary.
- [x] 7.2 Add cancellation between completed batches and during controlled delay; assert no later batch begins and prior dispositions remain.
- [x] 7.3 Retain or extend block 11’s cancellation-after-prior-effects case and assert Cancelled detail/count/timestamp invariants.
- [x] 7.4 Inject foreign OperationCanceledException at representative pass-level and per-asset boundaries with an active run token and prove neither is classified as active cancellation.

## 8. Cover pass, critical, repository, and reporter failures

- [x] 8.1 Add pass-level failure rows for count, skipped snapshot, configuration snapshot, batch retrieval, and controlled batch delay; assert no later batch, one healthy Failed terminal result, message-only detail, no fatal FailedCount increment, and retained prior counts where applicable.
- [x] 8.2 Add controlled OutOfMemoryException rows at representative non-reporter execution boundaries—resolution, airport, update/skipped persistence, and pass-level repository/delay—and assert escape from local handling, Failed outcome through a healthy session, and retained earlier effects.
- [x] 8.3 Extend block 11 reporter-fault coverage using the actual combined open/start boundary, representative midstream eligibility/log/activity/disposition/cleanup boundaries, and finish acceptance; include reporter-origin OOM and assert original exception propagation with no recursion/direct-state fallback.
- [x] 8.4 Prove open/start failure creates no usable session or terminal attempt, midstream failure makes no terminal attempt, and finish rejection propagates after one validated terminal attempt while ExecuteAsync returns no result.
- [x] 8.5 Prove exactly-one terminal acceptance only for healthy sessions.

## 9. Verify terminal invariants and scope

- [x] 9.1 Exercise completed mixed-disposition, cancelled partial, failed partial, and eligibility-divergence results through the common invariant assertions.
- [x] 9.2 Run focused tests with `dotnet test --project tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj --filter "FullyQualifiedName~ProcessingRunExecutor|FullyQualifiedName~ProcessingPipeline"`.
- [x] 9.3 Run `npm run test` with default Integration and Performance exclusions; do not run live integration tests unless a separately authorized implementation changes an integration-covered path.
- [x] 9.4 Confirm the executor tests instantiate no cron/scheduler/coordinator/host/Blazor/ProcessingState, PostgreSQL/SQLite repository, or real geodata/cache artifact and use no sleep-based ordering.
- [x] 9.5 Run `openspec validate 14-cover-executor-independently-from-scheduler --strict`, inspect final status, and scope-review the diff to block 14 only.
