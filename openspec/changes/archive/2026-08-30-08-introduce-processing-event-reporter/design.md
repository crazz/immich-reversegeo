## Context

See [proposal.md](proposal.md) for motivation and [specs/processing-event-reporting/spec.md](specs/processing-event-reporting/spec.md) for the behavioral contract. Block 7 defines immutable request/result identity, UTC timing, accounting, and outcomes, but its source types must exist before implementation. The active `ProcessingBackgroundService` still injects `ProcessingState`: orchestration owns startup/schedule logs, `MarkPending()`, the run lock, and manual CTS; execution owns count/start, counters, UI logs, completion, and a nested resolver-progress bridge.

Two ordering facts shape this design. Block 7's execution start precedes eligibility counting, and counting itself can cancel or fail, while current `ProcessingState.StartRun(total)` occurs only after a successful count. The event language therefore separates execution start from eligibility determination. Also, block 8 cannot route production events without the block-9 state adapter, so it introduces an unwired run-scoped session that can enforce the contract independently.

## Goals / Non-Goals

**Goals:**
- Define a dependency-light reporter/session API usable by the later executor, Web adapter, and worker bridge.
- Make lifecycle, accounting, activity, diagnostics, async ordering, cancellation races, backpressure, and reporter faults mechanically testable without production wiring.
- Preserve block 7 meanings and document exact later compatibility mapping to current UI state/log call sites.
- Supply no-op and deterministic recording/fault-injection support.

**Non-Goals:**
- Do not inject the reporter into `ProcessingBackgroundService`, replace direct state calls, implement a state adapter, or change DI in this block.
- Do not change `ProcessingState`, Razor, scheduling, admission, pending state, lock/CTS ownership, processing rules, writes, persistence, or user-visible behavior.
- Do not migrate resolver/cache progress (block 10), extract execution (block 11), or add a coordinator.
- Do not define protocol envelopes, JSON names, serializer attributes, wire timestamps/sequences, framing, exit codes, public redaction policy, or event coalescing.

## Decisions

### Open a validating run-scoped session rather than expose a bare sink

The conceptual API is an asynchronous `IProcessingEventReporter.OpenRunAsync(ProcessingRunRequest, DateTimeOffset startedAtUtc, CancellationToken)` returning an `IProcessingRunEventSession`. Opening emits `RunStarted`. The session exposes eligibility, disposition/progress, log, activity, and finish operations. Internally it serializes them, validates legal transitions, owns counters and activity identities, and sends immutable events to the reporter implementation.

This makes lifecycle/accounting guarantees testable in block 8 without touching the runtime pipeline. A bare `ReportAsync(ProcessingEvent)` sink could validate payloads but could not prevent duplicate terminals, regressing snapshots, or post-terminal events. Many unrelated reporter methods without a session would leave those rules to every producer.

The immutable event family is `RunStarted`, `EligibilityDetermined`, `ProgressChanged`, `ActivityStarted`, `ActivityEnded`, `LogEmitted`, and `RunFinished`. Every event retains the request; finish retains the validated block-7 result.

### Separate execution start from eligibility-known UI timing

`RunStarted` carries the request and zero-offset UTC execution-start timestamp and is accepted when the session opens, before counting. It has no total. `EligibilityDetermined` carries one non-negative total after a successful count. Before `EligibilityDetermined`, the session accepts no `ProgressChanged`, `LogEmitted`, `ActivityStarted`, or `ActivityEnded` operation. After `RunStarted`, the only legal next accepted event is `EligibilityDetermined` or, when counting cancels or fails, `RunFinished`. A cancelled or failed result may directly follow start when the count never completes.

This preserves every fact: block 7 still measures execution across the count query, cancellation/failure before count has valid lifecycle, and block 9 can ignore start for mutable UI purposes until eligibility arrives and then invoke current `StartRun(total)`. Rejected invocations open no session because block 7 creates no request before admission.

Alternatives considered: optional total on start creates two shapes and invites adapters to publish pending as active; fabricating zero on count failure is false; allowing a terminal event without any start weakens run lifetime.

### Put accounting and progress creation inside the session

The session owns non-negative long updated, skipped, and failed counts and derives processed as their checked sum. Producers report one terminal disposition, not a caller-built snapshot: updated only after a successful Immich write; skipped at an intentional no-write decision; failed after a handled per-asset exception. The session commits the disposition and emits the resulting immutable `ProgressChanged` under the same serialization gate, making snapshots monotonic and coherent.

A disposition is irreversible once the underlying write/decision/handled failure completes. Its accounting and snapshot use a non-cancelled publication path. Therefore cancellation after a successful write, while waiting for the session gate, or under reporter backpressure cannot erase the update; the eventual cancelled result includes it. Cancellation before a terminal disposition contributes nothing. This follows block 7's accounting rather than treating progress publication timing as the source of truth.

Alternative considered: accept arbitrary snapshots from parallel callers. Rejected because callers can race and publish stale/regressing combinations.

### Define linearizable acceptance and bounded backpressure precisely

Each session has one async serialization gate. An event linearizes when the underlying reporter has synchronously consumed the immutable value or copied it into reporter-owned bounded capacity. Successful operation completion means linearization occurred. Cancellation before that point emits nothing; cancellation after it cannot retract the event. Session operations are awaited and never fire-and-forget.

The reporter must be safe across concurrent sessions, but cross-session order is unspecified. Each session validates only its own stream. A queued implementation owns and bounds its capacity; unavailable capacity asynchronously backpressures the caller. Block 8 does not require a queue for the immediate no-op reporter, does not allow an unbounded producer queue, and does not drop/coalesce accepted events. Block 65 may later coalesce progress.

Producer locks for unrelated processing state must not be held while awaiting reporter acceptance. The session gate protects only session lifecycle/accounting/activity data.

### Make terminal closure own outstanding activities

Beginning activity allocates a non-empty opaque `Guid`, awaits `ActivityStarted`, and only then returns an asynchronous scope. First disposal reports `ActivityEnded` through a non-cancelled path; repeated disposal is a no-op. Equal labels never share identity.

`FinishAsync` acquires the session gate, closes any still-open activities in a defined stable order, emits their end events, marks their scopes locally closed, then attempts `RunFinished`. After finish linearizes, every later scope disposal is a local no-op and no event can follow terminal. Normal producers should still use structured async disposal before finish; finish-owned closure is the safety net for cancellation/fatal unwind.

Alternative considered: let late disposal emit after finish. Rejected because terminal would not be final. Receiver-only cleanup is insufficient because the stream itself would remain invalid.

### Distinguish lifecycle cancellation from cancellable publication

The active run token controls processing. If it ends execution, the validated result is Cancelled with accumulated irreversible dispositions. An unrelated `OperationCanceledException` is Failed. Cleanup-required operations—committed disposition publication, activity end, and finish—do not use the already-cancelled run token.

An ordinary event operation may accept a caller token while waiting before linearization. If cancelled first, it emits nothing and returns cancellation. This publication cancellation is not itself a per-asset failure. Runtime mapping of an unexpected reporter-publication cancellation is deferred until the session is wired, but the session treats it exactly like reporter infrastructure failure and becomes broken.

### Break, clean locally, and never recurse when the reporter fails

If the underlying reporter faults or cancels before accepting any event, propagate that failure and mark the session broken. A broken session rejects ordinary operations, marks tracked scopes locally closed, and does not attempt activity-end, log, or failed-terminal events through the same reporter. If finish acceptance fails, the producer may still own a valid `ProcessingRunResult`, but no terminal observation can be promised.

Thus exactly-one terminal means exactly one accepted terminal for a non-broken session, not magical delivery through a failed sink. Reporter infrastructure failure never increments per-asset `FailedCount`. Later execution wiring will treat the propagated fault as fatal infrastructure, but block 8 does not alter the current runtime catch flow.

Alternative considered: catch a reporter fault and report it through the same reporter. Rejected as recursion with no credible delivery guarantee.

### Preserve exact UI-log boundaries, not all ILogger diagnostics

`ProcessingLogLevel` has Trace, Information, Warning, and Error. Messages are non-blank plain text without timestamp/level prefixes; block 9 restores current formatting. Only existing `ProcessingState.AppendLog` and `IncrementError` UI-log call sites become `LogEmitted` during routing. `ILogger`-only diagnostics remain outside the event stream.

The current resolved-location line occurs before `WriteLocationAsync`; it is Trace resolution detail, not successful-write confirmation. If the write fails, Trace remains before Error, preserving current order. The current no-city path that only calls `ILogger.LogWarning` emits no new UI log when block 9 routes it, although it still records a skipped disposition. Other current UI warning/error call sites retain their messages and typed levels. Lifecycle start/empty/cancel/fatal/summary lines can be derived from start/eligibility/finish by block 9 rather than duplicated as events.

Messages may retain current in-process asset/location detail for compatibility. They are not declared public or wire-safe. No exception object, stack, credentials, connection strings, SQL, or arbitrary structured value crosses the contract; Phase 3 must decide exposure/redaction before serialization.

### Map current interactions without moving them

| Current interaction | Finalized event/projection decision |
|---|---|
| `MarkPending()`, run lock/CTS, startup/contention logs | Remain Web orchestration; no run event |
| execution entry before count | Open session; `RunStarted` |
| successful count and `StartRun(total)` | `EligibilityDetermined`; block 9 invokes state start here |
| successful write + `IncrementProcessed()` | Updated disposition/snapshot; UI maps UpdatedCount |
| intentional no-write + `IncrementSkipped()` | Skipped disposition; emit Warning only for current UI-log call sites |
| pre-write resolved-location UI log | Trace resolution detail before write |
| handled asset exception + `IncrementError()` | Error log plus failed disposition; run may complete |
| active-token cancellation | Cancelled result; only earlier committed dispositions retained |
| fatal pass exception + `IncrementError()` | Failed result, no per-asset increment; block 9 preserves legacy error presentation |
| `CompleteRun()` and summary | Finish projection; block 9 derives current terminal/summary behavior |
| nested resolver progress bridge | Unchanged until block 10 |

### Supply immediate no-op and deterministic test reporters

The production contract includes a stateless singleton no-op reporter with no queue and completed acceptance. Test support includes a thread-safe recorder grouped by session, deterministic gates before linearization, bounded-capacity simulation, and injected faults/cancellation at each event kind. Immutable snapshots permit assertions after concurrency completes.

The no-op is not wired into the active pipeline in block 8 because that would suppress UI updates. A temporary state-backed reporter is also rejected because it is the block-9 adapter.

## Risks / Trade-offs

- [Block 7 planning is complete but source models were absent in the inspected checkout] → Keep an explicit prerequisite task and stop rather than duplicate block 7.
- [Contract-only block 8 does not yet remove runtime Web-state coupling] → Block 9 performs adapter/routing; the session makes the eventual behavior enforceable without a transition regression.
- [Non-cancelled publication of irreversible dispositions can delay cancellation under backpressure] → Prefer accounting correctness; require bounded reporter capacity and keep active-token checks around, not inside, the committed publication.
- [Reporter failure can make a valid result unobservable] → Expose the infrastructure fault, break the session, and avoid false exactly-once claims or recursive reporting.
- [Diagnostic text may contain provider/asset detail] → Keep it explicitly in-process and message-only; require Phase 3 exposure policy.
- [Block 9's existing plan assumes a less precise vocabulary] → It must consume RunStarted plus EligibilityDetermined and preserve current UI timing without redefining block 8.

## Migration Plan

1. Verify block 7 source models/tests are applied; if absent, stop.
2. Add immutable events, validation, typed log level, reporter/session state machine, accounting gate, activity scope, and no-op reporter in the dependency-light layer.
3. Add recorder/gates/fault injection and focused contract tests; leave active service/state/DI/resolver/UI unchanged.
4. During apply, run focused tests and Phase 1/default regression verification, then inspect the diff for block boundaries.
5. Roll back by removing only new contract/test files; there is no runtime, data, configuration, UI, or protocol migration.
