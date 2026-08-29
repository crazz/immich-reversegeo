## Context

See [proposal.md](proposal.md) and [specs/worker-progress-coalescing/spec.md](specs/worker-progress-coalescing/spec.md). Finalized v1 blocks 15/16 require every raw frame to retain strict contiguous stream sequence and fail-closed validation. Block 21 owns worker-side bounded lossless stdout emission and per-frame flush. Block 27 currently projects accepted processing events synchronously and exactly-next through ProcessingState, so UI notification work can backpressure the stdout sink while stderr and exit observation remain independently drained. Finalized block 47 adds v2 typed jobs while preserving v1 bytes, one identity, stream finality, and ProcessAssets parity. Blocks 44, 49, and 51 define separate process-wide worker status and page-independent Lookup/cache state with generation, stale-callback, cancellation, and finality rules.

The design must reconcile three properties that cannot all be unconditional for arbitrary infinite lossless traffic: bounded memory, no loss, and a producer that never waits. This change chooses bounded memory plus losslessness, uses asynchronous backpressure only when the bounded lossless path saturates, and structurally removes Blazor rendering from that wait chain. “Stdout always drains” therefore means its pump remains an independently scheduled active drain and is not held by UI cadence or renderer callbacks; it is not a promise of infinite-rate lossless intake with finite memory.

## Goals / Non-Goals

**Goals:**
- Reduce repeated full-state progress projection and UI notification while retaining every accepted diagnostic, activity, lifecycle, result, and terminal fact.
- Preserve primary v1/v2 sequence validation and make any downstream coalesced gap explicit, narrow, and auditable.
- Keep child stdout/stderr/process finality live under normal bursts and make saturation/shutdown behavior deadlock-free.
- Apply one policy model to v1 ProcessAssets, v2 ProcessAssets, and explicitly declared v2 capability snapshots without merging their read models.
- Make capacity, cadence, and observation deterministic and measurable.

**Non-Goals:**
- Change protocol bytes, framing, codec compatibility, worker emission, flush policy, or protocol sequence allocation.
- Coalesce the protocol reader/validator, lifecycle, activity, logs, warnings/errors, terminals, results, or classifier observations.
- Change log message/retention bounds, public settings, block 64 scheduling, block 66 metric names/exporters, or job arbitration.
- Make Lookup/cache transient state part of ProcessingState or expose internal job identities to UI.

## Decisions

### 1. Insert coalescing after primary acceptance and before read-model projection

The launcher continues reading complete stdout frames, decoding them, and advancing the finalized v1/v2 validator for every event. Only the already accepted typed-event sink hands items to a new per-session delivery stage. Thus a missing wire sequence remains a protocol failure; coalescing cannot hide it. This stage must consume the finalized launcher sink rather than parse bytes, and block 27's defense-in-depth projection checks remain, narrowed to accept only authenticated coalesced-range evidence.

Alternative: coalesce in the worker emitter or stdout reader. Rejected because it would revise block 21's lossless contract or skip block 15/47 validation and make real gaps indistinguishable from intentional replacement.

### 2. Use a bounded lossless FIFO plus one replaceable slot

Each active session owns:
- immutable exact version/kind/identity/owner generation;
- one bounded FIFO for lossless accepted items and barriers;
- one pending replaceable slot containing the latest snapshot and the contiguous superseded sequence range;
- one dedicated asynchronous consumer/projection loop;
- one atomic intake/terminal/disposal state and finality receipt.

The initial production policy is 256 FIFO entries plus one snapshot slot. Both are named internal options with smaller test injection, not protocol, AppConfig, environment, or UI settings. Apply must run the required measurement before enabling the production path; retain 256 only if its documented queue high-water/wait/memory evidence is safe. Latest-wins replacement never waits. Lossless enqueue waits asynchronously on full. The dedicated consumer and launcher stderr/exit tasks do not need a renderer callback or process exit to advance, eliminating circular waits.

Alternative: one drop-oldest channel. Rejected because it can evict a log/activity/terminal. Alternative: an unbounded lossless side queue. Rejected because log/event bursts could consume Web memory. Alternative: one FIFO containing all progress. Rejected because it preserves the bottleneck.

### 3. Treat replaceability as typed descriptor metadata

The closed default is lossless. V1 progress-changed and the v2 ProcessAssets absolute-count equivalent are declared replaceable. A v2 descriptor may declare a specific concrete progress payload replaceable only if it is a complete current-state snapshot. Lookup/cache discrete transitions remain lossless unless their finalized applied contracts demonstrate that their payload is a replaceable current-status snapshot; do not infer replaceability from a type or property name. All common logs, including Trace and Information, remain lossless because their own payload/storage limits are separate contracts. Activities remain lossless to preserve pairing and terminal cleanup.

Alternative: treat warning/error as lossless but sample information logs. Rejected because this block was explicitly bounded to progress; changing diagnostic retention is a separate contract change.

### 4. Preserve source sequence and carry a verified suppression span

Items retain their source sequence. Replacing snapshots extends only a contiguous span consisting entirely of accepted replaceable events for the same session. Delivery includes an internal value equivalent to `DeliveredEvent(event, sourceSequence, suppressedReplaceableStart, suppressedReplaceableEnd)`. It is created only by the coalescer and cannot be supplied by protocol input.

Block 27/v2 projection cursors gain a narrow advance rule: exact next sequence normally, or a jump whose omitted closed range exactly matches trusted suppression evidence. The cursor still validates identity, kind, event type, snapshot monotonic/coherent values, lifecycle, and terminal facts. Lossless events are barriers: flush the pending snapshot first, then deliver the lossless event. Therefore a suppression span can never cross a log, activity edge, eligibility, lifecycle event, result, or terminal.

Alternative: renumber delivered events. Rejected because diagnostic correlation would diverge from protocol evidence. Alternative: relax the bridge to merely increasing sequences. Rejected because unexplained loss would become invisible.

### 5. Make terminal a two-level barrier

When a validated terminal reaches intake, one atomic transition closes the session. The consumer delivers: any pending pre-terminal latest snapshot, every preceding FIFO item, then terminal. The terminal bridge projection completes state mutation and requests an immediate final read-model notification. Only after that notification is accepted/completed does the coalescer finality receipt settle. Existing session/classifier/admission cleanup includes that receipt in its process exit + stdout EOF + stderr EOF + protocol finalization + bridge cleanup barrier.

A crash, malformed stream, missing terminal, forced stop, or abandonment never creates a protocol terminal. The coalescer closes/wakes waiters, finishes already in-flight projection according to the finalized sink contract, and returns a bounded nonterminal observation for the classifier. An uncertain projection is not retried.

Alternative: discard pending progress at terminal. Rejected because terminal should be preceded by the latest applicable state. Alternative: let process exit release admission while UI drain continues. Rejected because stale callbacks could mutate a later job.

### 6. Separate state mutation from notification cadence

Read models continue applying lossless events in order and replacing snapshots atomically, but notification becomes a revisioned dirty-signal scheduler. It uses `TimeProvider` and an asynchronous timer, never `Task.Delay`, wall-clock sleeps, or synchronous renderer calls. The initial default is 100 ms (at most 10 ordinary notifications/second per read model), injectable in tests and subject to the same pre-enable measurement gate.

The first mutation in an idle window schedules one notification; later mutations only advance the dirty revision. At the tick, subscribers observe the latest immutable snapshot. Terminal, retained failure, cancellation-finality, and disposal-finality request an immediate final notification, cancel/supersede the scheduled revision, and complete the barrier after dispatch. Component handlers remain responsible for `InvokeAsync(StateHasChanged)` and disposed/generation checks; the producer never synchronously waits on Blazor rendering. Reconnected components read current snapshots immediately, preserving block 44.

Alternative: notify lossless logs immediately and throttle only progress. Rejected because high-rate bounded logs can still trigger render storms even though their state mutations must remain lossless. Alternative: debounce from the last event. Rejected because a continuous stream could starve UI refresh; fixed-rate leading-window scheduling guarantees bounded latency.

### 7. Preserve state ownership and stale filtering

Processing v1/v2 uses the ProcessingState adapter and equivalent final state. Worker lifecycle status remains the separate block-44 read model. Lookup and cache use their page/controller state and operation generation and never publish transient frames into ProcessingState. Queue items, timer callbacks, barrier receipts, and owner release all carry/check the landed exact identity, kind, session generation, owner handle, and page generation as applicable. Disposal marks stale rendering first, joins the existing idempotent stop/session cleanup, and releases only after coalescer and existing stream finality.

### 8. Expose facts for block 66 without implementing telemetry

Maintain bounded numeric observation facts: accepted replaceable/lossless counts, replaced count, delivered snapshot count, FIFO high-water, asynchronous enqueue-wait count/duration, projection duration, cadence notification count, terminal flush duration, stale rejection count, and abnormal abandonment count. Expose them as an internal immutable snapshot or callback with only closed kind/category labels already approved by the job descriptor. Do not add instruments, names, meters, exporters, dashboards, alerts, raw messages, job IDs, or exception text; block 66 owns that translation.

### 9. Verify from pure scheduling through real processes and Blazor seams

Use injected TimeProvider, capacity-one channels, gated projection, and barrier-driven producers for deterministic unit tests. Extend the real child fixture to emit high-rate valid v1/v2 progress interleaved with logs/activities and terminal, plus blocked projection, cancellation, malformed gap, crash, and shutdown modes. Parse every raw frame through production validators and assert the stdout/stderr/exit tasks settle. Test read-model subscribers with fake renderer dispatch and virtual time rather than sleeps; add focused Razor/Blazor component seams for Dashboard/Logs/NavMenu, Lookup, and Data status only where necessary to prove bounded rerenders, immediate terminal state, reconnection snapshots, and disposal suppression.

## Risks / Trade-offs

- [Finite lossless capacity can backpressure stdout] → Keep the consumer independent of UI rendering, stderr, and exit tasks; measure high-water/waits and state the finite-memory impossibility explicitly rather than silently dropping.
- [Suppression evidence could weaken validation] → Create it only after primary acceptance, restrict it to contiguous same-session declared snapshots, and retain fail-closed bridge checks for every unexplained gap.
- [A 100 ms cadence can briefly hide intermediate activity/log changes] → Preserve all mutations and expose the newest complete snapshot within the fixed bound; terminal/failure boundaries notify immediately.
- [A 256-entry default may be too large or small] → Gate enablement on representative measurement and record any change; keep values internal and test-injectable.
- [Descriptor metadata can over-classify future events] → Default to lossless and require capability tests proving full-snapshot semantics before opt-in.
- [Disposal can race terminal and timer callbacks] → Use one atomic intake close, idempotent drain task, revision/generation fencing, and finality-before-release tests.

## Migration Plan

1. Re-read the applied APIs from blocks 15, 16, 21, 27, 44, 47, 49, and 51; stop rather than edit prerequisites or invent parallel protocol/job/state contracts.
2. Add the post-validation delivery policy/value types and observation snapshot behind the accepted-event sink, initially disabled for production selection.
3. Add bounded FIFO/latest-slot consumption, suppression-span validation, barriers, and cancellation/shutdown/disposal finality.
4. Add TimeProvider-based revisioned notification scheduling to the existing read-model boundaries without changing their public state semantics.
5. Prove v1/v2 ProcessAssets parity and opt in only capability events whose applied typed contracts satisfy full-snapshot replaceability.
6. Run deterministic burst, process, and Blazor measurements; record environment/results and confirm or revise 256 entries and 100 ms before enabling the production path.
7. Leave the bounded observation seam for block 66, then run focused/default tests and strict OpenSpec validation.

Rollback disables/removes the post-validation coalescer and cadence scheduler and restores direct accepted-event projection. Protocol bytes, persisted data, settings, and worker requests require no migration.
