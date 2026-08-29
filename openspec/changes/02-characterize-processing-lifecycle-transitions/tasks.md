## 1. Deterministic admission fixture

- [x] 1.1 Apply block 1 first, then extend its internal operation/pass seam with task-completion signals that can hold exact-count evaluation and the first token-aware post-start operation without real storage or geodata.
- [x] 1.2 Extract the existing due-schedule semaphore acquisition, immediate `MarkPending()`, pass invocation, contention log, and `finally` release into one internal async scheduled-admission method used unchanged by `ExecuteAsync`.
- [x] 1.3 Add fixture helpers that await explicit pass-entered, active, and terminal signals; do not use sleeps, poll private fields, or treat `TriggerRunAsync`'s completed task as pass completion.

## 2. Lifecycle characterization

- [x] 2.1 Add a manual-trigger test that holds eligibility evaluation and proves pending state is running without a start timestamp; release the count and prove `StartRun` then makes active execution observable with a start timestamp.
- [x] 2.2 Add a successful active-pass case that reaches inactive state with a completion timestamp and the current completion summary.
- [x] 2.3 Add a manual `CancelRun` case at a token-aware post-start boundary and prove inactive completion, the cancellation entry, no ordinary error, and summary ordering.
- [x] 2.4 Add a post-start pass-level exception case and prove inactive completion, one exposed error carrying the injected message, and fatal-entry-before-summary ordering.

## 3. Arbitration and ownership recovery

- [x] 3.1 While a signaled run owns execution, prove a duplicate manual trigger starts no additional pass, does not call `MarkPending` or change the owning run's start timestamp, and adds no contention log entry.
- [x] 3.2 While a signaled run owns execution, invoke scheduled admission and prove it starts no additional pass, does not call `MarkPending` or change the owning run's start timestamp, and appends exactly `Scheduled run skipped because a processing pass is already in progress.`.
- [x] 3.3 After terminal cleanup has completed for each successful, manually cancelled, and failed path, use deterministic cases to prove a later manual trigger and a later scheduled admission can each acquire ownership and start a pass.

## 4. Verification

- [x] 4.1 Run `dotnet test --project tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj --filter "FullyQualifiedName~ProcessingBackgroundServiceTests"`.
- [x] 4.2 Run `npm run test` and confirm the default Integration and Performance exclusions remain in effect.
