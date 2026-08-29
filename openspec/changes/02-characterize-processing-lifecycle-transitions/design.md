## Context

See proposal.md for motivation and the lifecycle specification for required observations. `ProcessingBackgroundService` currently combines admission and execution: scheduled work awaits a nonblocking semaphore acquisition, while `TriggerRunAsync` returns immediately after dispatching fire-and-forget work. Only an admitted trigger calls `ProcessingState.MarkPending()` immediately after acquiring the semaphore; a rejected trigger makes no state mutation. `StartRun` records a start timestamp and resets run data only after the eligibility count has completed. The pass catches token cancellation and pass-level failures, then always completes state and logs a summary.

Block 1 plans an internal immutable operation set and direct pass invocation so tests can replace database, skipped-store, and geodata calls. That seam is a required implementation dependency and is not yet present. The current scheduler also has real configuration, Cronos, delay, and skipped-database startup dependencies, so driving the whole hosted loop would make contention tests nondeterministic.

## Goals / Non-Goals

**Goals:**
- Exercise the production manual and scheduled admission logic without real time, storage, or geodata.
- Distinguish pending from active execution using existing timestamps and operation boundaries.
- Infer success, cancellation, and failure from current state, counters, and logs.
- Prove contention and ownership release using operation counts and later admissions rather than private semaphore inspection.

**Non-Goals:**
- Testing cron calculation, scheduler delays, skipped-database startup, host shutdown, or scheduled host-token cancellation.
- Characterizing cancellation swallowed inside per-asset handling or promising immediate interruption of synchronous native work.
- Changing `TriggerRunAsync` to return run completion, making `CancelRun` affect scheduled runs, or adding a terminal-outcome enum.
- Extracting the Phase 2 executor/coordinator or changing production DI registration and public APIs.

## Decisions

### Reuse the block 1 pass seam and add one scheduled-admission seam

Extend block 1's internal operation set with no broader abstraction than the calls already made by `RunOnceAsync`. Extract the existing due-schedule lock/mark/run/release branch into an internal async scheduled-admission method that the hosted loop calls unchanged and tests can invoke directly. This preserves real admission and pass handling while avoiding cron, wall-clock delay, and startup initialization.

Starting the whole hosted service was rejected because its real clock and configuration dependencies obscure the lock behavior under test. Reflection over private methods or `_runLock` was rejected as brittle. A public scheduler/executor interface was rejected because it would implement later architecture early.

### Coordinate every transition with task-completion signals

Use `TaskCompletionSource` delegates configured with asynchronous continuations. Hold the exact-count operation to observe pending state before `StartRun` and prove no start timestamp exists until the count completes. Return a positive count, then hold the first token-aware post-start operation to observe active state and to drive success, cancellation, or a pass-level exception. Test fixtures expose explicit entered and completed signals because awaiting `TriggerRunAsync` does not await its background pass.

Delay-based polling was rejected because it makes fire-and-forget tests timing-sensitive. A zero-count pass alone was rejected for active-state assertions because `StartRun` and terminal cleanup occur without a controllable observation boundary.

### Characterize only terminal paths that cross StartRun

Drive cancellation and failure after `StartRun` has reset state. Cancellation uses the manual CTS through `CancelRun` at a token-aware operation boundary. Failure injects a non-cancellation exception that reaches `RunOnceAsync`'s outer catch. Assert inactive state, completion timestamp, stable log content/order, and error observations; do not add a terminal representation.

Pre-start cancellation or failure was rejected for this contract because current `MarkPending` does not reset totals, counters, or `LastError`, so those cases can retain prior-run data and would materially widen block 2. Scheduled cancellation was rejected because `CancelRun` currently controls only manual runs; host-token behavior belongs with scheduler/worker cancellation work.

### Cover trigger-specific contention and recovery explicitly

While a signaled run owns execution, invoke a second manual trigger and the internal scheduled-admission method separately. Assert neither increments the pass-entry count nor calls `MarkPending` or changes the owning run's start timestamp; manual contention adds no log, and scheduled contention adds the exact stable message `Scheduled run skipped because a processing pass is already in progress.`. After terminal cleanup has completed for each of success, cancellation, and failure, use parameterized cases so both manual and scheduled origins demonstrate later admission.

This is preferred over checking semaphore counts because it captures observable effects and remains valid when ownership moves into a coordinator.

## Risks / Trade-offs

- [The scheduled-admission method is a production seam introduced for tests] → Keep it internal, make the real due-schedule branch its only production caller, and preserve the branch's current call order and cancellation token.
- [Fire-and-forget manual dispatch can hide assertion races] → Await fixture-owned entered/completed signals and never treat the returned trigger task as run completion.
- [Exact log matching can become brittle] → Match stable message content and ordering while ignoring timestamp prefixes and notification counts.
- [Block 2 depends on a not-yet-implemented block 1 seam] → Apply block 1 first; extend that seam rather than creating a competing fixture abstraction.

## Migration Plan

1. Apply block 1's internal operation/pass seam.
2. Extract the narrow internal scheduled-admission method without changing the hosted loop's behavior.
3. Add deterministic lifecycle, contention, and recovery tests in `ImmichReverseGeo.Tests`.
4. Run the focused service tests, then the default repository suite.
5. No deployment or data migration is required; rollback removes the block 2 admission seam and tests while leaving block 1 intact.
