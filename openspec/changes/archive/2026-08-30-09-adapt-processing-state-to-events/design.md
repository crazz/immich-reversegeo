## Context

See [proposal.md](proposal.md) for motivation and [specs/processing-event-state-adapter/spec.md](specs/processing-event-state-adapter/spec.md) for required behavior. Block 3 characterizes the synchronous singleton state observed by Dashboard, Logs, and NavMenu. Block 7 defines request/result identity, UTC timing, aggregate accounting, and outcomes. Block 8 defines an unwired asynchronous run session whose events are run started, eligibility determined, coherent progress, correlated activity, typed log, and run finished.

The active service currently calls `MarkPending()` immediately after either trigger acquires `_runLock`, but it calls `StartRun(total)` only after the count query. All main-pass lifecycle, count, log, and asset dispositions mutate `ProcessingState` directly. Resolver/cache progress is a separate nested bridge that also mutates state. Blocks 7 and 8 are complete planning dependencies but their source types are absent in the inspected checkout, so apply must verify those source prerequisites and stop if they are still absent.

## Goals / Non-Goals

**Goals:**
- Add one state-backed reporter whose lifetime and identity match the singleton state.
- Preserve the block-3 state contract and block-2 pending/terminal timing while mapping finalized block-8 events.
- Route the main production pass through one awaited run session without double mutation.
- Isolate state by admitted run identity and activity identity.
- Leave a precise, small block-10 boundary for resolver/cache progress.

**Non-Goals:**
- Do not extract the executor, scheduler, coordinator, or change nonblocking admission, manual CTS, scheduled host-token ownership, processing rules, writes, or results.
- Do not change Razor consumers, public state property meanings, log text, log capacity, configuration, persistence, or database behavior.
- Do not migrate the nested resolver/cache progress bridge or non-processing Lookup callers; that is block 10.
- Do not define any Phase 3 envelope, serialization name, protocol sequence/timestamp, framing, process, exit, or redaction behavior.
- Do not revise block-7 models or block-8 event/session vocabulary to make the adapter easier.

## Decisions

### Arm the singleton projection at admission

Register the adapter once as a concrete singleton and bind `IProcessingEventReporter` with a factory that resolves that exact instance. It owns the singleton `ProcessingState`, a small projection gate, the currently armed request identity, terminal state, last applied progress snapshot, and activity-ID-to-state-scope map. Do not register the interface and concrete type independently, which would create two correlation owners.

After a manual or scheduled invocation wins `_runLock`, preserve the existing immediate `MarkPending()` call, create its unique block-7 request, and synchronously arm the adapter before execution can open the session. Rejected invocations create neither request nor arm. Arming is a Web control-plane action; once armed, execution uses only the block-8 reporter/session API for main-pass reporting.

This admission handshake is stronger than letting the first `RunStarted` claim an idle singleton. It prevents a delayed old start from claiming a newly pending UI and gives deterministic stale/cross-run tests. An arm for a different identity while one is owned is rejected as an internal invariant violation; incoming unarmed, mismatched, post-terminal, or duplicate-terminal events are consumed as no-ops so they cannot break or corrupt the valid session.

**Alternative considered:** infer the active run only from `ProcessingState.IsRunning` and the first event. Rejected because pending carries no identity and cannot distinguish a new accepted request from stale work.

### Separate pending, execution start, and eligibility projection

Opening the session at `RunOnceAsync` entry emits `RunStarted` before the count, as block 8 requires. The adapter validates/correlates it but performs no state mutation, so the UI remains pending with its prior total/counters/error/start timestamp. `EligibilityDetermined(total)` invokes the existing start/reset behavior and then appends exactly one existing start line:
- zero: `Run started — nothing to process, all assets already have location data.`
- nonzero: `Run started. {total} assets to process.`

A cancellation or fatal failure before eligibility proceeds from pending directly to terminal. It does not fabricate a total or call start. Because block 3 deliberately did not contract stale pre-start values, block 9 chooses compatibility with the current service: the pre-eligibility summary uses the retained visible counters, and a pre-eligibility fatal error adds one to the retained UI error count; no new start timestamp is recorded.

State timestamps remain the state object's UTC `DateTime` values at projection time. Do not copy block-7 `DateTimeOffset` execution timestamps into the legacy state: the result remains the domain timing record, while the WebUI keeps its existing start-at-eligibility and completion-at-projection semantics.

**Alternative considered:** call start with total zero on count cancellation/failure. Rejected because zero would assert a false eligibility fact and erase the pending/start timing contract.

### Apply absolute snapshots and keep diagnostics from double-counting

Progress is authoritative as an absolute monotonic snapshot, not a delta inferred from callback count. Add only narrow state projection operations needed by the adapter:
- atomically replace the three run counters from one accepted snapshot, mapping `UpdatedCount` to legacy processed, `SkippedCount` to skipped, and `FailedCount` to ordinary errors, then notify;
- append an Error diagnostic while setting `LastError` without incrementing the error counter, timestamp/prefix once, then notify at least once.

Keep the existing public increment methods unchanged for block-3 compatibility and for the resolver path until it moves. Absolute replacement makes duplicate/replayed progress harmless after correlation filtering and avoids treating aggregate `ProcessedCount` as successful writes. It also separates block-8's handled-failure accounting from diagnostic text: the Error log sets the newest error and the progress event sets the count, so either accepted order remains monotonic and produces no duplicate line or count. Exact notification multiplicity and an intermediate callback between two distinct accepted events are not contracted; final accepted-event state is.

Warning logs become one `[WARN] {message}` append, Error logs use the error-diagnostic projection, and Trace/Information append the plain message. The adapter supplies no timestamp or duplicate severity marker; `ProcessingState` remains the only timestamp/cap/order owner.

**Alternative considered:** call `IncrementError` for both Error log and failed progress. Rejected because split events would double-count or duplicate the line. Inferring progress from log messages is also rejected because diagnostics are not accounting.

### Derive terminal compatibility behavior from the result

Block 8 accepts every required `ActivityEnded` before `RunFinished`, and the adapter projects those ends first. For the matching non-broken `RunFinished`, terminal projection is then serialized in this order:
1. Completed adds no outcome line; Cancelled appends exactly `Run cancelled.`; Failed uses the existing fatal-error operation with `Fatal: {FailureMessage}`, adding one legacy UI error without changing the result's per-asset count.
2. Defensively dispose/remove any remaining tracked adapter activity scopes for that run as idempotent safety cleanup only.
3. Invoke completion, which sets inactive, clears all state activity, records the projection-time UTC completion, retains totals/counters/error/logs, and notifies.
4. Snapshot the visible counters and append exactly `Run complete. Processed={processed} Skipped={skipped} Errors={errors}`.
5. Mark the request terminal and release arm ownership only after the summary mutation completes.

This preserves the current fatal/cancel/completion/summary order and Dashboard's running-to-not-running notification before the summary append. Cancellation does not clear an earlier handled `LastError`; after eligibility, start already cleared the prior-run error. A pre-eligibility cancellation retains prior snapshot/error exactly as the current path does.

**Alternative considered:** copy terminal result counts/timestamps into the state in one terminal operation. Rejected because it would move start timing, erase intermediate UI behavior, and alter existing notification/log ordering.

### Correlate activities by run and opaque identity

For each matching `ActivityStarted`, call `BeginActivity(label)` once and store the exact returned scope under (RunId, ActivityId). Equal labels with distinct IDs therefore produce two reference-counted scopes. For a matching end, atomically remove and dispose only that scope. Unknown or duplicate ends do nothing. Terminal cleanup removes/disposes the map before state completion; late ends cannot reach a newer run because request identity is checked first.

The adapter never uses `SetActivity` and never decrements by label itself. This retains block 3's equal-label and single-survivor behavior while eliminating the old-run/same-label decrement hazard at the adapter boundary.

**Alternative considered:** store only label counts in the adapter. Rejected because it discards the identity supplied by block 8 and cannot defend against duplicate or late ends.

### Route the main pass now and leave only resolver progress for block 10

Block 9 changes the accepted main pass as follows:
- create the trigger-specific request only after admission and arm the adapter;
- open the session at execution entry before eligibility count;
- report eligibility and await all UI-log/disposition operations;
- report Updated only after a successful Immich write, Skipped only at the existing actively evaluated no-write branches, and Failed only after a handled per-asset exception; previously suppressed IDs report nothing;
- preserve the no-city branch as logger-only plus Skipped, and preserve warning/error/Trace boundaries from block 8;
- build/finish the block-7 result through the finalized block-8 completion API and its session-owned counters, including committed dispositions that survive cancellation;
- use a run-local successful-write count for the existing batch message rather than read `ProcessingState` from the routed main pass.

Startup, next-schedule, scheduled-contention, and `MarkPending()` remain direct state control-plane calls. The service continues to inject state for those calls and for its nested `ProcessingResolutionProgress`. That nested production bridge and all resolver/cache `BeginActivity`/`Report` calls remain exactly direct in block 9, with no duplicate event emission. Block 10 removes that resolver/cache direct-state bridge; it does not need to redo main lifecycle, asset accounting, or adapter registration. Later executor/coordinator blocks remove the remaining broader service coupling.

**Alternative considered:** add events alongside all existing state calls. Rejected because it duplicates counters/logs and cannot prove that the event path is production-real. Moving resolver progress in the same block is rejected because it collapses block 10 and expands cancellation/concurrency scope.

### Preserve block-8 cancellation and broken-session rules

The active run token continues to determine Completed/Cancelled/Failed according to blocks 6–8. A committed disposition is awaited through the session's non-cancelled publication path before terminal construction, so cancellation after a write/decision cannot erase it. Ordinary report operations are awaited and never fire-and-forget.

If the reporter/session becomes broken, follow block 8: propagate/log the infrastructure fault, perform no recursive event reporting through the broken session, and do not misclassify it as a per-asset failure. The production state adapter has no queue or external I/O, so its normal acceptance is immediate; injected broken-reporter behavior belongs to block-8 contract tests rather than a second fallback mutation channel in block 9.

**Alternative considered:** catch reporter failures and directly repair state from execution. Rejected because it creates a hidden second source of truth and violates the block-8 no-recursion boundary.

### Verify behavior with signal-driven tests

Adapter tests use the finalized block-8 session or immutable event sink path, fixed unique IDs, and `TaskCompletionSource` gates with asynchronous continuations. They assert state values and stable log suffix/order, bracket legacy timestamps with `DateTime.UtcNow`, and count notifications only as at least one per accepted mutation. No test uses sleeps, wall-clock prefix equality, dictionary enumeration among multiple survivors, live PostgreSQL/geodata, or Phase 3 serialization.

Production-routing tests reuse the block-1/2 service seam. They gate count, write, disposition acceptance, and cancellation to prove admission/request/session order and absence of duplicate state mutations. A resolver-progress spy proves the nested direct bridge remains active and no event duplicate is created. DI verification resolves the concrete adapter and reporter interface and requires reference identity, alongside the existing concrete/hosted-service singleton ownership rule.

## Risks / Trade-offs

- [Blocks 7/8 planning is complete but source prerequisites are absent] → First apply task verifies their source and focused tests; stop rather than recreate or reinterpret them in block 9.
- [Error diagnostics and failed snapshots are separate events] → Use separate no-increment error projection plus absolute progress replacement; contract final state and at-least-one notification, not cross-event atomicity.
- [Ignoring correlation-invalid events can hide a producer defect] → Emit safe `ILogger` diagnostics outside ProcessingState while guaranteeing no user-state mutation; deterministic tests cover each rejection class.
- [Pre-eligibility terminal summaries can retain an older snapshot] → This deliberately preserves current pending behavior, which block 3 excluded from its reset contract; document and test it rather than fabricating eligibility.
- [The service still injects ProcessingState after first routing] → Keep only control-plane and resolver bridge uses; block 10 removes resolver reporting and later coordinator extraction removes orchestration coupling.
- [Synchronous `OnChanged` subscribers may enqueue renders out of order] → Preserve existing synchronous callback semantics and observable values; do not redesign Blazor dispatch in this refactor.

## Migration Plan

1. Verify blocks 7 and 8 are applied in source and their focused/default tests pass; otherwise stop.
2. Add narrow internal projection operations and focused state tests without changing existing public behavior.
3. Add the singleton adapter, correlation/activity state, and deterministic adapter tests.
4. Register the concrete singleton/interface alias and verify reference identity.
5. Arm accepted requests and route only the main pass through the session, retaining direct control-plane and resolver progress paths.
6. Run focused state/adapter/service tests, the default suite, strict OpenSpec validation, and a diff review limited to block 9.
7. Roll back by restoring direct main-pass mutations and removing the adapter binding/projection helpers; no persisted or external migration is required.
