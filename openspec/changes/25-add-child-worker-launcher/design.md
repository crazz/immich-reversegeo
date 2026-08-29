## Context

See [proposal.md](proposal.md) for motivation and [specs/child-worker-launching/spec.md](specs/child-worker-launching/spec.md) for behavior. Blocks 15, 17, and 21–23 fix the worker protocol, controller execute encoding, ready/event validation, byte limits, stdin lifecycle, and raw exit semantics. Block 24 concurrently owns the shell-free executable/argument/environment descriptor. This change must consume those finalized types rather than create parallel command, protocol, result, or job identities.

A redirected child has three independently progressing boundaries: stdin handshaking, stdout protocol, and stderr diagnostics. Waiting on readiness or exit before continuously draining both output pipes can deadlock. Conversely, treating process exit alone as completion can lose trailing terminal or stderr bytes.

## Goals / Non-Goals

**Goals:**
- Define one launcher call, one typed launch result, and one exclusive session owner for a single worker process.
- Make process start, ready validation/timeout, execute write/flush, accepted-event delivery, raw exit, stream finality, and bounded stderr independently observable.
- Keep both output pumps and exit observation concurrent from process start through completion.
- Make lifecycle tests deterministic without starting production workers.

**Non-Goals:**
- Coordinator backend selection or scheduler wiring.
- `ProcessingState` projection (block 27).
- Sending cancel, choosing a grace period, or killing a process tree (block 28), including Web-shutdown composition (block 29).
- Crash/protocol-failure classification, terminal/exit consistency policy, retry, or UI messaging (block 30).
- Changing worker protocol, stdin, stdout, or exit-code ownership from blocks 15–23.

## Decisions

### Return ownership immediately after process creation

`IChildWorkerLauncher.LaunchAsync` consumes the finalized block-24 command descriptor, immutable `ProcessingRunRequest`, one `IWorkerProtocolEventSink`, and launcher options. Its discriminated `ChildWorkerLaunchResult` is either `StartFailed` or `Started(ChildWorkerSession)`. The started result is returned once the process and all observation tasks are owned, not after readiness. The session exposes `ProcessId`, `RunId` (the sole job ID), `Startup`, and `Completion`, plus cancellation-aware wait methods and `IAsyncDisposable`.

This split prevents start exceptions from masquerading as later protocol failure, gives the caller an owner even when ready never arrives, and makes wait cancellation safe. Alternative considered: keep `LaunchAsync` pending through ready and request flush. Rejected because cancellation or timeout after OS start could return without a process owner.

`ChildWorkerStartupObservation` distinguishes ready accepted/request flushed, ready timeout, pre-ready EOF/exit, framing/codec/stream-validation failure, and execute write/flush failure. `ChildWorkerCompletionObservation` carries startup finality, raw exit availability/code, stdout/stderr finality, the first protocol/sink observation, accepted terminal if any, stderr tail/truncation, and process ID/run ID. These are observations, not block-30 classifications.

### Isolate platform process mechanics behind an owned adapter

Production adapts `System.Diagnostics.Process` behind `IChildProcessFactory` and `IChildProcess`. The factory translates the general `ChildProcessStartDescriptor` into platform start configuration; the launcher accepts only a validated production `WorkerCommandInvocation` from block 24, while block 26 may create a general descriptor for fixture-only arguments. The adapter exposes PID, owned byte streams, one exit task, raw exit code after exit, and asynchronous disposal; it does not expose `Process` to the launcher caller.

Tests use a gated in-memory process implementation with controllable streams, start exceptions, PID, exit signal/code, and disposal counters, plus injected `TimeProvider`. This proves ordering and deadlock freedom without sleeps. Block 26 later provides a real fixture executable and boundary tests; production code must not depend on that fixture. Alternative considered: mock `Process` or add a fixture mode now. Rejected because `Process` is not a useful deterministic seam and fixture ownership belongs to block 26.

### Start stdout, stderr, and exit observation as one atomic ownership transfer

Immediately after the factory returns a child, the launcher starts the stdout pump, stderr pump, and exit observation before returning `Started`. A single internal lifecycle task coordinates them. Completion uses an all-settled approach: it observes process exit and both pump finalities even when one path faults, then creates one immutable observation and disposes through the session owner. No path synchronously blocks on async I/O, calls `ReadToEnd` after waiting for exit, or stops draining stderr because stdout became invalid.

Alternative considered: await ready, then begin stderr drainage, then await exit and read remaining stdout. Rejected because each ordering permits a full redirected pipe to block the child.

### Use the shared bounded protocol path and serialize sink callbacks

The stdout pump performs bounded incremental LF framing in bytes, preserving the Phase 3 one-MiB object limit and strict UTF-8 rules, then uses the shared codec and one stateful stream validator. It sends only accepted events through a serialized async sink. Ready is the first accepted callback. A sink failure is recorded once; later callbacks are suppressed, but the byte pump continues parsing/draining so a consumer bug cannot deadlock the child. Invalid or oversized stdout is likewise recorded without echoing payload bytes and drainage continues to EOF.

Alternative considered: expose raw lines and let each caller parse. Rejected because it duplicates compatibility/order state and lets downstream code consume unvalidated events.

### Make readiness and execute delivery an explicit startup task

A finite internal readiness timeout is represented in options and driven by an injected `TimeProvider`; the required default is exactly 30 seconds and tests advance fake time. Valid ready wins only when accepted by the shared stream validator before the deadline and successfully delivered through the serialized sink. Then the launcher uses block 17's canonical controller-to-worker codec to produce exactly one execute frame, writes all bytes, flushes, and leaves stdin open. A ready sink failure settles startup and writes no execute bytes while both output pumps continue. Timeout, invalid first frame, EOF/exit before ready, and request write/flush failure settle `Startup` distinctly. They do not classify the process or invent a terminal.

Alternative considered: close stdin after execute. Rejected because block 22 keeps reading correlated controls and block 28 must later send cancel on the same session.

### Retain a fixed raw stderr byte tail

The stderr pump always reads to EOF and appends to a 65,536-byte ring buffer. It tracks total bytes/truncation and decodes snapshots with replacement fallback only when exposing text; retention is byte-bounded regardless of character width. The limit is an internal constant for this change, not a user setting. Safe worker-side summaries from block 23 may appear in the tail, but the launcher does not interpret them.

Alternative considered: retain all stderr or line-count bounds. Rejected because diagnostics can be unbounded and UTF-16/line bounds do not cap incoming pipe bytes.

### Separate caller wait cancellation from session policy

Cancellation tokens on `WaitForStartupAsync` and `WaitForCompletionAsync` use wait-only cancellation; they never flow into the owned pumps, stdin, or process. The token passed to `LaunchAsync` is honored only before the process-start attempt. Once the factory returns a child, the launcher completes ownership transfer and returns `Started` even if cancellation races with that return, so no live process can be abandoned without a session. After `Started` is returned all policy acts through the session. `DisposeAsync` is idempotent: it closes stdin and suppresses callbacks, then awaits the same pumps/exit and releases handles. It intentionally neither sends cancel nor kills, so it can wait indefinitely for a noncooperative accepted run until block 28 supplies bounded escalation.

Alternative considered: map any caller cancellation to process kill. Rejected because it would preempt block 28's graceful policy and blur a cancelled wait with worker cancellation.

## Risks / Trade-offs

- [Disposal can wait on a live noncooperative worker] → Make this explicit and let block 28 add cancel/grace/kill policy without changing session ownership.
- [Continuing to drain after protocol or sink failure consumes resources] → Keep framing and stderr retention bounded and suppress further callbacks; drainage is required to prevent pipe deadlock and preserve raw finality.
- [A fixed 30-second ready timeout may later need deployment tuning] → Keep it in internal launcher options and injectable in tests; do not add public configuration in this block.
- [Trailing stderr may start mid-UTF-8 sequence] → Retain raw bytes and use replacement fallback for the optional text snapshot.
- [Process exit code can conflict with accepted terminal] → Preserve both unchanged and defer consistency/classification to block 30.

## Migration Plan

1. Treat blocks 18–24 as hard ordered prerequisites: re-read the applied block-24 `WorkerCommandInvocation` and blocks 15/17/21–23 APIs; use their exact names and stop for reconciliation if any required contract is absent.
2. Add launcher/result/session/event-sink contracts and raw observation values without coordinator or `ProcessingState` wiring.
3. Add the process factory/adapter, bounded stderr tail, stdout protocol pump, readiness/request startup task, and shared completion/disposal lifecycle.
4. Add deterministic fake-process and fake-time tests. After block 26 lands, add its real process-boundary scenarios without changing launcher ownership.
5. Roll back by removing the launcher registrations/contracts/adapters and tests; no persisted data or wire migration is introduced.
