## Context

See `proposal.md` and `specs/child-worker-cancellation/spec.md`. Block 13 gives one coordinator an identity-checked active handle and an idempotent cooperative Stop surface. Blocks 17 and 22 define one canonical correlated cancel command and a worker-side cancellation latch. Block 25 owns the exact process, stdin, concurrent output pumps, raw completion, and idempotent disposal; its wait tokens are deliberately wait-only and its current disposal can remain pending forever. Block 26 supplies armed cooperative and unresponsive real-process modes. Block 23 keeps a flushed terminal authoritative and leaves forced termination unmapped; block 30 later classifies the facts.

## Goals / Non-Goals

**Goals:**
- Linearize one Stop operation against the coordinator's exact active run and child session.
- Send at most one valid cancel frame, preserve a cooperative terminal/exit when available, and escalate after a fake-clock-testable deadline.
- Keep stdout/stderr draining through cooperative exit or forced termination and release every owned resource once.
- Expose complete typed raw evidence for later classification and make all important races deterministic in tests.

**Non-Goals:**
- Closing admission or composing ASP.NET host shutdown timeouts; block 29 owns those behaviors.
- Classifying crashes, missing terminals, terminal/exit contradictions, kill failures, or UI failure text; block 30 owns them.
- Changing the v1 cancel schema, adding acknowledgements, retries, command reasons, another identity, or reusable jobs.
- Adding a persisted/public grace setting, deployment-mode setting, Settings UI, or documentation for such a setting. Later mode/configuration work owns that surface.
- Interrupting arbitrary synchronous native calls cooperatively; escalation is the bounded fallback when code does not observe the token.

## Decisions

### 1. Put cancellation policy on the exact owned session and join concurrent callers

The coordinator captures the current active handle under its existing gate. The first Stop transitions that handle to stopping and creates one shared asynchronous cancellation operation. Later Stop calls for the same handle return the same operation/result; idle Stop is a harmless no-op. Cleanup remains identity-checked, so a late caller or completion from run A cannot target run B.

The shared operation captures the session, run ID, process generation, and initial monotonic timestamp once. It never re-reads a mutable “current session” when writing or killing. This prevents a delayed command from reaching a replacement worker. Dashboard Stop returns promptly once the current handle has accepted/joined the operation; it shows stopping and does not claim cancellation succeeded before terminal/process evidence exists.

Alternative: let every caller send cancel and start a timer. Rejected because valid repeated Phase 3 cancels still consume distinct sequence values and duplicate writers/deadlines create avoidable races. Alternative: keep cancellation in Dashboard. Rejected because shutdown and later callers must reuse one process-owner policy.

### 2. Latch early Stop but write only to a request-accepted session

Stop may arrive after process ownership but before readiness or execute delivery. The operation latches intent and starts the grace deadline immediately, but it writes no cancel until that exact session reports successful complete execute write and flush. If startup becomes request-accepted before the deadline and the process has not exited, the session serializes one canonical correlated `control/cancel` frame at the next controller-input sequence, writes all bytes, and flushes under its sole stdin-writer gate.

No cancel is written for start failure, pre-ready failure/exit, ready timeout, ready-sink rejection, execute write/flush failure, already completed/exited process, closed stdin, or a different/replacement session. A write or flush fault is retained as a typed cancellation-transport fact; it does not fabricate acceptance, close stdout/stderr, or short-circuit the grace deadline while the process remains alive.

Alternative: write cancel immediately after process start. Rejected because block 17 requires execute sequence 1 and exact run correlation. Alternative: close stdin to signal cancellation. Rejected because EOF is explicitly not cancellation.

### 3. Use a 10-second internal default driven by TimeProvider

A validated cancellation policy value defaults to 10 seconds in production and is supplied beside launcher/session lifecycle options. It must be finite and greater than zero. Tests override it directly and advance an injected `TimeProvider`; production behavior does not use `Task.Delay`, wall-clock subtraction, polling, or sleeps.

The deadline is measured from the first accepted Stop, not from cancel flush. This bounds pre-ready, command-write-failed, and unresponsive sessions under one policy. A successful process exit before the deadline wins and suppresses escalation. A terminal frame alone does not suppress escalation because the process can still remain alive and hold redirected streams.

This is internal configurability, not a persisted `AppConfig` or Settings-page option. A later explicitly owned configuration/mode change may expose or relocate it without changing this cancellation contract.

Alternative: reuse the 30-second readiness timeout. Rejected because readiness and cancellation have different operational purposes. Alternative: leave the value open. Rejected because block 29 needs a concrete reusable policy and deterministic tests need one production default.

### 4. Preserve cooperative authority and continue the existing completion lifecycle

On the worker, the block-22 lease cancellation source links with host stopping and feeds the exact executor token. A cancel accepted before executor entry is already requested at entry; during execution it requests the same token; after terminal it is effect-idempotent. The worker still owns terminal emission and orderly exit mapping. Cancellation is cooperative only at code paths that observe the token; a synchronous database/native/geospatial call that does not accept or check it may not return before grace expiry.

On the controller, a valid cancelled terminal and orderly exit 130 are retained as cooperative evidence, but cancellation does not require that exact pair to settle transport ownership. Any completed/failed/cancelled terminal, missing terminal, contradictory exit, protocol/sink observation, or stdin transport fault remains unchanged for block 30. The shared operation waits for raw process exit and both stream pumps before claiming settled cleanup.

Alternative: treat a cancelled terminal as completion and dispose immediately. Rejected because the worker may still be alive or have trailing stdout/stderr bytes.

### 5. Escalate once with whole-process-tree kill and preserve platform failures

When the grace deadline wins and exit is not already observed, the process adapter makes one escalation attempt using `Kill(entireProcessTree: true)`. Exit racing the call is rechecked and recorded as already exited rather than a kill success/failure. A successful kill request is not finality: the operation awaits the same block-25 exit task and both output pumps so trailing bytes and handles are not lost.

If the platform reports unsupported access, permission, invalid state, or another safe-normalized kill failure while the process remains alive, the operation records one typed escalation-failed fact, performs no blind retry or PID reacquisition, and does not falsely report stopped or release session ownership. The coordinator remains stopping; if the process later exits, the existing completion path drains and disposes it. Block 30 decides presentation/classification, and block 29 separately decides how host shutdown timeout composes with this unresolved ownership.

Alternative: fall back to PID lookup or non-tree kill. Rejected because PID reuse and partial descendant termination weaken exact ownership. Alternative: dispose streams after kill failure. Rejected because that can strand a live child or hide diagnostics.

### 6. Keep cancellation observations raw and disposal single-owner

Cancellation contributes immutable facts to block 25 completion: first Stop/deadline timestamps, whether request acceptance occurred, cancel serialization/write/flush outcome, process exit before/during control, grace expiry, tree-kill attempted/accepted/already-exited/failed, and the existing accepted terminal, raw exit, protocol/sink, stdin, stdout/stderr finality, and stderr-tail facts. These facts carry stable safe categories only; no raw command, exception text, stack, secret, retry advice, or projected failure is added.

Stop, completion, and `DisposeAsync` converge on one lifecycle. After exit plus both drains, stdin closes, callback suppression/bridge abandonment follows its existing owner, and redirected streams, timers, cancellation sources, process adapter, and session are disposed exactly once. Multiple Stop/dispose/wait callers observe the same settled tasks. Cancellation tokens passed by callers cancel only their wait for the shared result and never the owned cancellation operation.

## Risks / Trade-offs

- [A native operation ignores the worker token] → Start grace at first Stop and use whole-tree kill after 10 seconds.
- [The process exits while cancel or kill is in flight] → Serialize against exact-session state, recheck exit, and retain the race outcome without issuing another action.
- [Cancel stdin write/flush fails] → Preserve the transport fact, continue drains, and escalate only if the process remains alive at the deadline.
- [Whole-tree kill is unavailable or denied] → Return a typed escalation failure, keep ownership/stopping state, and never claim cleanup that did not occur.
- [Terminal and raw exit disagree] → Preserve both unchanged for block 30; terminal authority and exit taxonomy remain block 23 contracts.
- [A delayed task targets a new process] → Capture session/run/process generation and detach only by exact identity.
- [A test uses timing as coordination] → Require fixture protocol markers and fake-time advancement; use real-time watchdogs only for failure cleanup.

## Migration Plan

1. Re-read the applied block-13 coordinator, block-17 input codec/validator, block-22 request lease, block-23 exits, block-25 session/process adapter, block-26 fixture, and block-27 bridge APIs; stop rather than duplicate missing contracts.
2. Add typed cancellation policy/observations and exact-session shared-operation state without classification or host-shutdown composition.
3. Add the sole serialized cancel writer, early-stop latch, `TimeProvider` deadline, whole-tree escalation seam, and converged exit/drain/disposal lifecycle.
4. Link the worker lease cancellation source to the executor token using existing block-20/22 ownership and verify terminal/exit behavior.
5. Route Dashboard/coordinator Stop to the shared operation and add deterministic fake-process plus cooperative/unresponsive fixture tests.
6. Run focused tests, `npm run test`, strict OpenSpec validation, and scope review. Rollback removes the block-28 policy while restoring block-25 non-escalating disposal; no data or protocol migration is needed.

## Audit Reconciliation

The one bounded escalation decision uses exactly one internal, exact-session 10-second deadline measured through `TimeProvider`; it is not configurable and creates no current or future public setting. After that deadline, raw process exit suppresses one tree-kill attempt; a live owned process receives at most one attempt. A terminal frame alone never settles process ownership.

