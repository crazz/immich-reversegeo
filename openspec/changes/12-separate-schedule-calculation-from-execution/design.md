## Context

See [proposal.md](proposal.md) and [specs/processing-schedule-orchestration/spec.md](specs/processing-schedule-orchestration/spec.md). Block 11 is a planning prerequisite, not evidence that its source has landed. At apply time, verify its required source APIs, registrations, and focused tests exist and pass; if absent, stop and apply it first rather than recreating or assuming its contract here. On that verified baseline, block 11 leaves ProcessingBackgroundService with skipped-storage startup, a Cronos loop, direct schedule/startup/contention logs, nonblocking admission, request/reporter setup, manual CTS and Dashboard methods, and in-process delegation to ProcessingRunExecutor. Its executor owns the exact count before the zero gate and every later processing fact. ConfigService rereads settings.json on each call but has no change notification; ScheduleEditorState only maps the existing presets to standard five-field cron text and does not define runtime time-zone behavior.

The current loop initializes skipped storage, appends `Service started. Waiting for next scheduled run.`, then reads one config snapshot per iteration. Disabled snapshots wait one minute. Invalid/no-next expressions wait five minutes. Valid expressions use Cronos Standard with DateTime.UtcNow and TimeZoneInfo.Utc, append `Next run scheduled at {next:u}` only for a positive delay, await that delay, and then attempt one scheduled admission. Accepted scheduled execution is awaited to terminal before the next config read; contention appends the existing skipped line. A save does not wake an active wait.

## Goals / Non-Goals

**Goals:**
- Make cron evaluation a pure, explicit-UTC calculation and make every wait controllable by TimeProvider/fake time.
- Keep schedule configuration, timing, next-run visibility, and Scheduled trigger generation independent from admission and execution ownership.
- Preserve startup ordering, retry durations, config reevaluation points, no-overlap observations, shutdown cancellation, and existing manual APIs.
- Define the narrow scheduler-facing contract block 13 can implement without changing schedule behavior.

**Non-Goals:**
- No eligibility/work detector, count, skipped-ID snapshot, processing-config snapshot, batch, resolver, geodata, write, or result logic; block 11 remains authoritative.
- No coordinator implementation or migration of lock, request creation, reporter arming, MarkPending, CTS, executor dispatch, terminal release, or Dashboard ownership; block 13 owns that move.
- No ScheduleConfig, ConfigService persistence, ScheduleEditorState, Settings UI, cron syntax, time-zone selector, next-run property, UI copy, or log text redesign.
- No worker process/protocol, advisory lock, run history, missed-occurrence replay, catch-up queue, or configuration file watcher.

## Decisions

### Represent each snapshot as a deterministic schedule plan

Add a small calculation collaborator that accepts only the copied Enabled flag, cron text, and an explicit DateTimeOffset UTC instant and returns one immutable plan: DisabledRetry (one minute), InvalidRetry (five minutes), or Due with a zero-offset next occurrence. Parse with Cronos `CronFormat.Standard` and calculate against `TimeZoneInfo.Utc`; reject/normalize non-UTC input at the boundary rather than consulting DateTime.Now or the host zone. Cronos's default exclusive search preserves the strictly-future rule.

The plan contains no request, run ID, admission result, asset count, or processing configuration. Invalid parse and no-next collapse to the same five-minute retry, as today. Alternative: reuse ScheduleEditorState parsing. Rejected because its regexes recognize editor presets, clamp display input, and intentionally do not implement full Cronos syntax or runtime occurrence calculation.

### Use one TimeProvider for wall clock and cancellable waits

Inject TimeProvider (System in production) into the schedule loop. Capture UTC now for plan calculation, then capture the wait-start instant to derive the positive relative delay; await with the TimeProvider-aware delay and hosted stopping token. This preserves the current relative-wait model while eliminating sleeps in tests. A clock that has advanced beyond the due instant produces no next-run line/delay and emits the already planned trigger once; the scheduler does not calculate a replacement occurrence in the same iteration.

Alternative: abstract only Task.Delay or use periodic polling. Rejected because separate uncontrolled DateTime.UtcNow calls retain boundary races, while polling changes timing and configuration responsiveness. No local/DST conversion is introduced. Wall-clock changes after a positive relative wait begins do not cause rescheduling; the next iteration recalculates from fresh UTC time, matching the existing delay model.

### Separate scheduled-trigger generation from downstream run control

Define a narrow scheduler-facing asynchronous boundary taking the Scheduled trigger origin and hosted token and returning RejectedAlreadyRunning or AcceptedAfterTerminal. The temporary production adapter delegates to block 11's existing host control path: it alone performs nonblocking admission, MarkPending, request creation/reporter arming, executor invocation, and release. The temporary adapter MUST NOT be a DI service that injects `ProcessingBackgroundService` while `ProcessingBackgroundService` injects the scheduler-facing boundary. The host either directly implements the internal boundary or constructs a private non-DI adapter over its own method delegate; `Program.cs` MUST NOT register an adapter-to-host back-edge. An accepted scheduled call remains awaited until execution terminates; a rejection returns immediately. ProcessingBackgroundService may retain Dashboard forwarding methods during this intermediate block, but schedule collaborators do not expose or call them.

This result shape is deliberately not the final coordinator API. Block 13 replaces the adapter and moves ownership without changing the scheduler-facing semantics. Alternative: have the scheduler inspect ProcessingState or acquire the semaphore itself. Rejected because state is observational rather than authoritative admission, and lock ownership in scheduling would conflict with block 13.

### Preserve visibility and configuration snapshots exactly

Before a positive due wait, append exactly one `Next run scheduled at {due.UtcDateTime:u}` line through the existing ProcessingState log path. Disabled, invalid, and nonpositive waits append no next-run line. Keep the startup and contention strings and their UI visibility unchanged; no separate NextRun property is introduced.

Read ConfigService once per loop iteration and copy only Schedule.Enabled/Cron into the plan input. Because ConfigService has no notification contract, saves during a wait do not wake it: disabled/invalid changes are seen after one/five minutes, while a change during a valid due wait is seen only after that planned due trigger is rejected or its accepted run terminates. This surprising behavior is preserved rather than silently introducing a watcher in a refactor. ScheduleEditorState and Settings remain untouched.

### Keep startup initialization as an explicit lifecycle prerequisite

Keep the exact concrete-singleton plus hosted-service alias: concrete `ProcessingBackgroundService` and its `IHostedService` alias are `ReferenceEquals`; any private temporary trigger adapter is not a second hosted or control-plane owner. ExecuteAsync must await the existing skipped-storage initialization boundary, retain its ILogger timing lines, append the service-started UI line, and only then enter schedule evaluation. If avoiding a direct skipped repository dependency is needed to prove scheduler layering, use a narrow startup-initializer adapter over the same singleton; do not move initialization into schedule calculation, duplicate it, or change the existing race surface for manual UI calls during host startup.

The hosted stopping token cancels all schedule waits and flows through an accepted scheduled call to the executor path. Cancellation exits the hosted loop without generating a trigger or ordinary failure log. Manual CancelRun continues to target only the existing manual CTS in this block; block 13 later consolidates cancellation ownership.

### Keep block 11's exact count authoritative and add no preflight

The scheduler emits a due Scheduled trigger regardless of likely work. The accepted path always invokes ProcessingRunExecutor, which opens its reporter session, performs GetUnprocessedCountAsync, publishes eligibility on success, and preserves the zero-count lifecycle. Block 12 introduces no repository seam and never labels a lightweight probe as eligibility.

A future work detector may avoid expensive downstream startup, but it must sit outside this scheduler capability, remain advisory, and never replace the executor count because the database is mutable and block 11 explicitly documents count-versus-batch races and skipped-ID inclusion. Alternative: perform `AnyAsync` or count in the scheduler. Rejected because it duplicates eligibility ownership, can suppress empty-run lifecycle events, and drags database policy into timing.

## Risks / Trade-offs

- [A temporary adapter resembles a partial coordinator] → Move no ownership or new arbitration policy; wrap the exact block-11 path, expose only the scheduler result needed for timing/logging, and require block 13 to replace it.
- [Config edits appear delayed] → Preserve and test existing reevaluation points; explicitly defer live wake/reschedule behavior rather than changing it accidentally.
- [Clock boundary or host-zone differences shift execution] → Use explicit zero-offset inputs, Cronos Standard plus UTC, one fake TimeProvider, and exact boundary tests.
- [Awaiting a trigger returns too early and causes extra contention logs] → Contract acceptance as completion-after-terminal and test that no reevaluation occurs while the accepted execution is gated.
- [A preflight steals eligibility ownership] → Ban repository/count/geodata dependencies from schedule calculation and host loop; assert the executor is invoked even when its authoritative count is zero.
- [Hosted shutdown leaves a wait or scheduled run alive] → Thread the stopping token through TimeProvider delay and accepted scheduled invocation and verify cancellation with gates, not sleeps.
- [Startup dependency removal changes initialization order] → Retain the existing initialization boundary and concrete/hosted singleton identity; only narrow its adapter if needed.

## Migration Plan

1. Verify whether blocks 1–11 are applied in source; if any required baseline is absent, apply it first, then re-read the final block-11 executor/host APIs rather than duplicating missing prerequisites.
2. Add immutable schedule-plan types and pure UTC Cronos calculation, then register TimeProvider.System and deterministic test substitutions.
3. Add the scheduler-facing trigger contract and a temporary adapter over the unchanged block-11 admission/execution path.
4. Refactor the hosted loop to initialize, snapshot, plan, log, wait, and emit Scheduled triggers; retain Dashboard methods and singleton/hosted identity.
5. Remove every count/repository/resolver/geodata/executor dependency from schedule collaborators and verify accepted empty execution remains executor-owned.
6. Add fake-time and signal-gated tests, run focused/default suites, strict validation, and a scope diff limited to block 12.
7. In block 13, replace the temporary adapter with the coordinator while preserving this schedule contract. Roll back block 12 by restoring the block-11 inline cron loop; no persisted migration or compensation is required.
