## 1. Reconcile applied prerequisites and inherited coverage

- [ ] 1.1 Apply and re-read blocks 7–13, then inventory the finalized executor constructor, collaborator seams, result/reporter APIs, and all executor tests added by block 11.
- [ ] 1.2 Map block 11’s direct-executor zero-count, representative mixed-run, cancellation/failure, and reporter-fault tests to this change’s matrix; retain host-delegation and DI-identity tests unchanged as regressions outside the shared fixture.
- [ ] 1.3 Leave Phase 1 hosted lifecycle/state tests and blocks 7–10 contract/adapter/resolver tests in place; do not recreate their assertions in the direct-executor suite.

## 2. Build the reusable scheduler-free fixture

- [ ] 2.1 Refactor only test code as needed so block 11 and block 14 executor tests share one direct-construction fixture with fail-fast unused collaborators.
- [ ] 2.2 Add scriptable fakes for count/keyset batches, configuration and skipped snapshots, administrative and airport results, update and skipped persistence, and controllable batch delay, each with ordered call/effect history.
- [ ] 2.3 Reuse the finalized recording/fault-injection reporter and fixed UTC seam; add asynchronous-continuation gates and a concurrency probe without sleeps or infrastructure.
- [ ] 2.4 Add common assertions for exact request/result correlation, fixed UTC/order, count equation, outcome/failure-detail rules, activity cleanup, one healthy terminal event, and returned/reported result equality.

## 3. Cover empty, snapshots, batches, and cursor behavior

- [ ] 3.1 Extend rather than duplicate block 11’s zero-count test to prove no skipped/config/batch/resolver/airport/persistence/delay calls and a completed empty result.
- [ ] 3.2 Prove a non-empty run reads one skipped-ID snapshot and one configuration snapshot across multiple batches, unaffected by later fake source changes.
- [ ] 3.3 Prove suppressed fetched IDs advance the keyset cursor but produce no resolver, persistence, disposition, or processed count.
- [ ] 3.4 Prove the exact initial/successive/final-empty cursor sequence and one delay after every non-empty batch, only after that batch’s assets finish, with no delay after the empty sentinel.
- [ ] 3.5 Prove positive eligibility followed by no fetched assets and lower/higher eligibility than fetched work terminate by batch responses while preserving the original eligibility event.

## 4. Cover bounded concurrency

- [ ] 4.1 Add gated rows for parallelism below one, within range, and above thirty-two; assert observed active work is clamped to one, the configured bound, and thirty-two respectively.
- [ ] 4.2 Release assets out of input order and assert only per-asset causal edges and complete accounting, not accidental global reporter order.
- [ ] 4.3 Prove the next batch and its delay boundary cannot overtake unfinished work in the current batch.

## 5. Cover dispositions, source ordering, and fallback

- [ ] 5.1 Add focused updated, no-country/no-admin skipped, and country-with-no-city skipped cases, including skipped-store and logger-only reporter boundaries.
- [ ] 5.2 Add handled ordinary source failures for administrative resolution and airport lookup plus update/skipped persistence failures; assert one Failed disposition, one Error diagnostic, continued peer processing, and Completed outcome.
- [ ] 5.3 At the same resolver boundary, inject an awaited activity/log reporter failure and prove it escapes as broken-session infrastructure failure rather than source unavailability or a Failed asset disposition.
- [ ] 5.4 Prove administrative resolution precedes optional airport lookup and disabled airport configuration invokes no airport collaborator.
- [ ] 5.5 Add separate rows for containing-airport override, non-containing airport preserving an admin city, and non-containing airport filling an absent admin city.
- [ ] 5.6 Add city, state, country-name, and no-fallback rows to prove the exact final fallback order and resulting update/skip decisions.

## 6. Cover persistence order and partial effects

- [ ] 6.1 Prove successful location and skipped-ID persistence each occurs before the matching disposition is accepted.
- [ ] 6.2 Prove persistence failure produces no false Updated/Skipped disposition and follows the finalized ordinary-versus-critical taxonomy.
- [ ] 6.3 Gate active cancellation during update and skipped persistence before success; prove no effect or disposition is recorded and the interrupted asset remains uncounted.
- [ ] 6.4 Gate cancellation after successful update and skipped persistence but before disposition acceptance; prove the non-cancelled committed path publishes and counts each effect.
- [ ] 6.5 Gate cancellation after committed no-city Skipped and handled Failed decisions; prove their non-cancelled publication and counts without inventing persistence.
- [ ] 6.6 Prove later cancellation or pass failure retains prior fake persistence effects and accepted counts without retry, rollback, compensation, or cross-store transaction.
- [ ] 6.7 Inject a reporter disposition failure after persistence and prove the effect remains while the original reporter exception propagates without compensation or recursive terminal reporting.

## 7. Cover cancellation boundaries

- [ ] 7.1 Add deterministic active-token cancellation rows before/during count, skipped/config snapshot, batch retrieval, administrative resolution, and airport lookup; assert eligibility/downstream calls and uncounted interrupted assets per boundary.
- [ ] 7.2 Add cancellation between completed batches and during controlled delay; assert no later batch begins and prior dispositions remain.
- [ ] 7.3 Retain or extend block 11’s cancellation-after-prior-effects case and assert Cancelled detail/count/timestamp invariants.
- [ ] 7.4 Inject foreign OperationCanceledException at representative pass-level and per-asset boundaries with an active run token and prove neither is classified as active cancellation.

## 8. Cover pass, critical, repository, and reporter failures

- [ ] 8.1 Add pass-level failure rows for count, skipped snapshot, configuration snapshot, batch retrieval, and controlled batch delay; assert no later batch, one healthy Failed terminal result, message-only detail, no fatal FailedCount increment, and retained prior counts where applicable.
- [ ] 8.2 Add controlled OutOfMemoryException rows at representative non-reporter execution boundaries—resolution, airport, update/skipped persistence, and pass-level repository/delay—and assert escape from local handling, Failed outcome through a healthy session, and retained earlier effects.
- [ ] 8.3 Extend block 11 reporter-fault coverage using the actual combined open/start boundary, representative midstream eligibility/log/activity/disposition/cleanup boundaries, and finish acceptance; include reporter-origin OOM and assert original exception propagation with no recursion/direct-state fallback.
- [ ] 8.4 Prove open/start failure creates no usable session or terminal attempt, midstream failure makes no terminal attempt, and finish rejection propagates after one validated terminal attempt while ExecuteAsync returns no result.
- [ ] 8.5 Prove exactly-one terminal acceptance only for healthy sessions.

## 9. Verify terminal invariants and scope

- [ ] 9.1 Exercise completed mixed-disposition, cancelled partial, failed partial, and eligibility-divergence results through the common invariant assertions.
- [ ] 9.2 Run focused tests with `dotnet test --project tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj --filter "FullyQualifiedName~ProcessingRunExecutor|FullyQualifiedName~ProcessingPipeline"`.
- [ ] 9.3 Run `npm run test` with default Integration and Performance exclusions; do not run live integration tests unless a separately authorized implementation changes an integration-covered path.
- [ ] 9.4 Confirm the executor tests instantiate no cron/scheduler/coordinator/host/Blazor/ProcessingState, PostgreSQL/SQLite repository, or real geodata/cache artifact and use no sleep-based ordering.
- [ ] 9.5 Run `openspec validate 14-cover-executor-independently-from-scheduler --strict`, inspect final status, and scope-review the diff to block 14 only.
