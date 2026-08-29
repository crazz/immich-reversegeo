## Context

See [proposal.md](proposal.md) for motivation and [specs/worker-event-state-bridge/spec.md](specs/worker-event-state-bridge/spec.md) for behavior. Block 9 defines the singleton Web adapter that owns request correlation, absolute state projection, activity scopes, terminal ordering, and `OnChanged`; block 15 defines typed accepted worker envelopes and stream order; block 21 maps the transport-neutral run events to worker frames; block 25 exposes a serialized asynchronous accepted-event sink and retains sink/protocol observations.

The inspected checkout currently contains only the pre-migration `ProcessingState` and direct `ProcessingBackgroundService` mutations. The block-7/8/9/15/21/25 source APIs do not yet exist even though their planning artifacts are complete. Apply must therefore re-read and consume their applied APIs, and stop for prerequisite reconciliation if they remain absent. This change must not invent parallel request, reporter, protocol, launcher, or terminal types.

The launcher calls its sink for `ready` before writing execute. Ready is process-scoped (sequence 1, null run ID); the existing adapter is run-scoped and deliberately defers `StartRun(total)` until eligibility. Launcher stream validation normally rejects malformed or illegal events before the sink, but the state boundary still needs fail-closed projection preconditions so direct/synthetic callers cannot mutate state with mismatched accepted-event objects.

## Goals / Non-Goals

**Goals:**
- Add one controller-side, one-request sink that maps validated worker events into the existing transport-neutral state adapter.
- Preserve block-9 pending/eligibility/terminal timing, counter/log/error meanings, activity behavior, and notifications exactly once.
- Keep projection ordered, awaited, deterministic, and lossless under backpressure.
- Return correlation/lifecycle/result contradictions as safe typed sink observations for block 30 while guaranteeing no partial state mutation.
- Clean up projected activity scopes deterministically on terminal or bridge abandonment.

**Non-Goals:**
- Parse raw NDJSON, own codec/stream validation, emit worker frames, launch a process, or change execute handshaking (blocks 15, 21, and 25).
- Classify malformed output, protocol incompatibility, absent terminal, sink failure, crash, exit-code contradiction, or stderr diagnostics (block 30).
- Add cancel/grace/kill behavior (block 28), production worker dispatch/coordinator switching (later Phase 5), or block-26 fixture work.
- Change `ProcessingState`'s public model, add PID/job fields, alter Razor components, or redesign logs/counters/history.
- Route existing in-process events through a second adapter path or duplicate direct state mutations.

## Decisions

### Consume the launcher accepted-event sink, then map to transport-neutral events

Implement the bridge against block 25's finalized asynchronous accepted-event sink contract. It receives the already decoded typed envelope, never bytes or JSON, and maps each run event to the exact immutable block-8 event accepted by the block-9 adapter. It does not open a producer-side reporter session or call disposition methods, because the worker already owns accounting and reopening a session would allocate new identities or count dispositions twice.

The bridge is constructed for one admitted `ProcessingRunRequest` and the exact singleton state adapter that was armed after admission/`MarkPending`. PID stays on `ChildWorkerSession`; run ID stays in the request/bridge/session. The bridge does not add either to `ProcessingState`.

**Alternative considered:** make the launcher itself call `ProcessingState`. Rejected because it couples reusable process I/O to UI semantics and violates block 25. **Alternative considered:** replay worker payloads through a fresh block-8 reporting session. Rejected because it creates a second accounting/lifecycle owner and can duplicate progress, logs, and terminal cleanup.

### Ready advances only the bridge handshake cursor

The bridge cursor begins expecting sequence 1 `lifecycle/ready` with null run ID. Successful ready records bridge readiness and returns without touching the adapter or state. The next event must be sequence 2 `lifecycle/run-started` with the expected run ID. Run-started maps to the adapter's correlation-only start event; eligibility maps to the adapter operation that calls `StartRun(total)` and derives the existing zero/nonzero line.

The controller admission path remains responsible for `MarkPending` and adapter arming before the launcher can callback. A launch/start/ready failure can therefore leave a pending run without terminal; the bridge does not repair it. Block 30 consumes the launcher observation and owns failed-run presentation.

**Alternative considered:** arm or start state on ready. Rejected because ready precedes execute/request acceptance and has no run identity. **Alternative considered:** call `StartRun` on run-started. Rejected because that would fabricate eligibility and break pre-count failure behavior.

### Add a projection cursor as a defense-in-depth gate, not a second wire parser

Under one async serialization gate, track expected sequence, ready/run-start/eligibility/terminal state, expected run ID, latest coherent progress, and open activity IDs. Before calling the adapter, check:
- exact next sequence and the block-15 closed category/type/payload combination;
- null run ID only for ready and the exact request run ID for every later event;
- legal cardinality/order, terminal finality, and non-empty unique activity IDs;
- monotonic/coherent absolute progress and block-7 count invariant;
- terminal request/trigger/outcome/type/timestamps/failure detail and final-count coherence.

This cursor validates typed projection preconditions only. Raw framing, UTF-8, codec compatibility, payload deserialization, and primary stream lifecycle validation remain in blocks 15/25. On a contradiction, return/throw the finalized safe typed sink-rejection shape so block 25 retains it as the first sink observation and suppresses later callbacks. Do not call the adapter and do not advance the projection cursor on rejection. Block 30 later classifies that retained observation.

Although the block-9 adapter still ignores stale/cross-run/post-terminal input as its own safety net, the worker bridge treats such input as a rejected launcher sink event rather than silently declaring success. This reconciles block 9's state-integrity rule with block 30's runtime-failure handoff.

**Alternative considered:** trust that launcher validation makes bridge checks unnecessary. Rejected because synthetic tests and alternate accepted-event sources could bypass that validator, and terminal result-to-state coherence is a projection concern. **Alternative considered:** duplicate the full protocol validator. Rejected because it would create two compatibility authorities.

### Project absolute snapshots and diagnostics through existing adapter operations

For eligibility, logs, progress, activity, and terminal, await the exact adapter event path introduced by block 9. Progress is absolute: `UpdatedCount -> ProcessedThisRun`, `SkippedCount -> SkippedThisRun`, and `FailedCount -> ErrorsThisRun`. `ProcessedCount` is validated as the aggregate but is never displayed as successful writes.

A handled asset Error log updates `LastError` and appends one line; its failed disposition is represented by the absolute progress snapshot. The bridge does not call `IncrementError` for both. A completed run may retain per-asset failures. A failed terminal preserves those counts and lets the adapter add exactly one legacy fatal error. Cancellation only emits the existing cancellation line and does not replace `LastError`.

Log severity and text are passed without timestamp or severity decoration; `ProcessingState` retains projection-time timestamps, prefixes, insertion order, and the newest-100 cap. The bridge awaits the adapter mutation so all synchronous `OnChanged` callbacks finish before sink acceptance returns.

**Alternative considered:** calculate deltas from the prior snapshot. Rejected because duplicate/replay safety and out-of-order rejection are simpler with absolute snapshots and block 9 already defines replacement semantics.

### Preserve launcher ordering by adding no second queue

Block 25 serializes sink callbacks in accepted stream order. The bridge additionally guards its mutable cursor and adapter call with one async gate so direct concurrent synthetic callers linearize. It does not enqueue, coalesce, batch, drop, or fire-and-forget. The gate is held through the awaited adapter projection; therefore backpressure flows to the stdout pump while stderr drainage and exit observation continue independently inside the launcher.

Cancellation of a caller waiting for launcher startup/completion is unrelated to bridge acceptance. Once the bridge accepts an event into its ordered gate, later wait cancellation cannot retract its state mutation. Use the finalized sink token semantics rather than introducing a worker-cancellation policy.

### Cross-check terminal authority before handing it to the adapter

Map terminal type to exactly one outcome: completed, cancelled, or failed. Reconstruct/use the block-7 result and verify exact request identity and trigger, type/outcome agreement, UTC/order/failure-detail invariants, and count equation. If progress was observed, terminal counts must equal the latest snapshot. If terminal legally follows run-started before eligibility, all disposition counts must be zero. Eligibility total is not required to equal processed count because suppressed or not-yet-processed assets can remain.

Only after every check succeeds does the bridge call the adapter terminal path. The adapter remains authoritative for outcome line, `CompleteRun`, summary, and arm release order only after the bridge verifies that every activity has ended; a terminal with an open activity is rejected without projection. The bridge marks itself terminal only after that awaited projection succeeds, making retry after an uncertain adapter exception impossible; such an exception becomes a sink observation for block 30 rather than a second terminal attempt.

Exit code, accepted terminal versus exit contradiction, and missing-terminal interpretation stay in block 30. A valid terminal is not overridden here by later raw process facts.

### Separate normal terminal cleanup from nonterminal abandonment

Activity mapping uses the exact protocol activity ID and label. The block-9 adapter owns the actual `BeginActivity(label)` scopes; the bridge cursor mirrors IDs only to validate pairing. Normal terminal invokes adapter finish, which ends all remaining scopes before completion. A duplicate/unknown end is rejected before adapter mutation.

The bridge is idempotently asynchronously disposable. Disposal suppresses new callbacks and waits for an in-flight accepted projection. If no terminal succeeded, it invokes a narrow adapter abandonment/cleanup operation for the expected run that disposes only that run's projected activity scopes and cannot affect a later arm. It does not call `CompleteRun`, append a summary/fatal line, clear or reuse run correlation as if terminal succeeded, or fabricate a protocol event. It returns a bounded nonterminal observation to the owner for block 30. The finalized adapter API should expose this narrow cleanup; do not expose protocol types from the adapter or change general `ProcessingState` lifecycle.

**Alternative considered:** synthesize failed terminal during disposal. Rejected because the bridge lacks authoritative crash/protocol/exit diagnostics and that policy belongs to block 30. **Alternative considered:** leave activity visible until block 30. Rejected because activity scopes are bridge-created projection resources and deterministic abandonment must not leak them.

### Test at typed boundaries with deterministic gates

Use synthetic typed accepted envelopes and a recording/gated adapter or real block-9 adapter with fixed requests, sequences, timestamps, progress, and activity IDs. Do not use raw JSON, real processes, sleeps, live geodata, or block-26 fixture modes. Tests subscribe to `OnChanged`, snapshot state before rejection, and use `TaskCompletionSource` with asynchronous continuations to prove ordered awaited projection and disposal races.

Boundary tests also prove that no production class in this change parses stdout, writes NDJSON, starts/kills a process, consumes stderr, changes Dashboard/Logs, or classifies launcher completion.

## Risks / Trade-offs

- [Predecessor source APIs are absent in the current checkout] → Make source verification the first apply task and stop for reconciliation rather than guessing signatures or recreating contracts.
- [Defense-in-depth cursor can drift from the Phase 3 validator] → Limit it to typed projection preconditions and reuse finalized enums/value validators; keep codec compatibility and raw lifecycle authority in block 15.
- [Awaiting synchronous UI projection slows stdout consumption] → This is intentional lossless backpressure; block 25 keeps both drains active and event volume is bounded by existing worker reporting.
- [Adapter projection could fail after partially mutating state] → Keep adapter operations atomic under its existing projection gate and never retry an uncertain accepted event; surface the sink observation to block 30.
- [Nonterminal disposal clears activity while the run remains pending/running] → This prevents leaked UI activity without inventing an outcome; block 30 is explicitly responsible for terminal repair and diagnostics.
- [Two safety layers treat invalid input differently] → The bridge rejects to preserve a runtime observation, while the adapter's no-op correlation guard remains the final protection against state corruption.

## Migration Plan

1. Verify blocks 7–9, 15, 21, and 25 are applied and record their exact request/result/event/adapter/sink/session APIs; stop if any prerequisite is absent or incompatible.
2. Add the typed one-request accepted-event bridge and safe rejection/nonterminal observation values in the Web/controller layer, using finalized protocol values rather than new wire contracts.
3. Add only the narrow adapter abandonment/activity-cleanup operation needed for nonterminal bridge disposal; keep `ProcessingState` and Razor public surfaces unchanged.
4. Bind the bridge factory/owner to the exact singleton block-9 adapter and block-25 accepted-event sink boundary without switching production execution backend.
5. Add synthetic deterministic tests for the full accepted lifecycle, rejection handoff, ordered backpressure, notifications, terminal cross-checks, and disposal cleanup.

Rollback removes the bridge registration/factory and narrow abandonment operation; in-process reporting remains on the existing block-9 path. No persisted data or protocol version migration is involved.

## Audit Reconciliation

A terminal received while this bridge has any open projected activity is a typed terminal-coherence rejection, not an instruction to close activities. Only a coherent accepted terminal performs normal terminal cleanup. Forced activity cleanup is limited to nonterminal bridge/session abandonment. A terminal that follows eligibility but no accepted progress is coherent only when all four result counts (`ProcessedCount`, `UpdatedCount`, `SkippedCount`, and `FailedCount`) are zero; eligibility alone never permits nonzero counts.

