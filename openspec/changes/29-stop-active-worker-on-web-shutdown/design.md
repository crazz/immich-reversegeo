## Context

See `proposal.md` and `specs/worker-shutdown-control/spec.md`. Block 13 establishes a singleton coordinator with an atomic admission-open/active-handle gate and hosted shutdown participation. Block 25 gives every successfully started process an exclusive session whose completion waits for exit plus stdout/stderr finality and whose disposal is idempotent but deliberately non-escalating. Block 27 owns typed-event projection and nonterminal abandonment cleanup. Finalized block 28 places one shared exact-session Stop task on the owned handle/session: it latches during startup, starts one injected-`TimeProvider` deadline at first Stop with a validated internal 10-second production default, writes cancel only after execute flush, attempts whole-process-tree kill once when grace wins, awaits exit and both drains, disposes once, and retains ownership plus raw evidence on kill failure. Block 30 later classifies raw startup, protocol, terminal, exit, and kill observations.

The difficult boundary is not merely calling Cancel during `StopAsync`: shutdown can interleave with admission reservation, pending projection, OS process start, readiness, execute flush, normal terminal delivery, stream drainage, or stale cleanup. Generic Host also raises `ApplicationStopping` before or around hosted-service `StopAsync`, and supplies a shutdown token governed by `HostOptions.ShutdownTimeout`.

## Goals / Non-Goals

**Goals:**
- Linearize admission closure before cancellation and make every earlier accepted ownership record part of shutdown.
- Give application lifetime notification, hosted stop, user Stop, and disposal one shared cancellation/cleanup task.
- Preserve block-25 stream/process ownership through process exit, both pump finalities, and exactly-once disposal.
- Fit block 28's existing 10-second deadline and remaining kill/drain/disposal lifecycle inside the host budget without resetting, shortening, or wait-cancelling that owned operation.
- Leave projection and coordinator state clean without creating a domain terminal or block-30 classification.
- Prove race behavior deterministically with gates and the block-26 fixture.

**Non-Goals:**
- Redefine block 28's cancel eligibility, grace duration/default, command serialization, process-tree kill mechanics, or terminal behavior.
- Change block 25 framing, pumps, raw observations, stderr retention, startup taxonomy, or session ownership.
- Classify start failure, malformed protocol, missing terminal, crash, kill, or terminal/exit contradictions; block 30 owns all such meaning.
- Change worker protocol, worker command handling, ProcessingState's data model, scheduler policy, distributed locking, retry, public settings, or endpoint shutdown.
- Touch block 28 artifacts while its concurrent planning owner is working.

## Decisions

### Close admission in the lifetime callback and await cleanup in one hosted owner

Register the exact singleton coordinator/lifecycle owner with `IHostApplicationLifetime` during its successful start. The synchronous `ApplicationStopping` callback calls a nonblocking `BeginShutdown`: under the coordinator's existing short gate it changes admission from open to stopping, captures the exact active handle, and publishes a memoized shutdown task before leaving the gate. It invokes no user callback, process I/O, or asynchronous continuation while holding the gate.

The same singleton is factory-aliased to one hosted lifecycle contract. Its `StopAsync` calls `BeginShutdown` again and awaits the already-published task. This makes `ApplicationStopping` the earliest admission fence and hosted `StopAsync` the sole asynchronous wait owner; they are not independent shutdown implementations. Registration and callback disposal are idempotent, and callback registration failure or partial `StartAsync` failure invokes the same fence/cleanup path.

Alternative: do everything only in `StopAsync`. Rejected because triggers can race after application stopping has begun but before that service is stopped. Alternative: run asynchronous cleanup directly from an `ApplicationStopping` callback. Rejected because callbacks cannot be awaited and exceptions/ownership become unobservable. Alternative: add a second hosted service around the coordinator. Rejected because registration order and duplicate state owners make admission and active-session identity easier to split.

### Reuse the coordinator's exact active record across every lifecycle stage

The active record remains published from accepted admission through final cleanup. It contains the immutable request, coordinator cancellation ownership, execution/session completion, and exact cleanup identity. Child startup publishes an ownership-transfer promise before the OS-start attempt can escape coordination. Shutdown captures the record, requests cancellation, and awaits a single lifecycle task that handles these cases:

- **Pending / pre-launch:** cancellation prevents dispatch where possible; shutdown still awaits the startup ownership promise so a concurrently started process cannot escape.
- **Starting / started pre-ready / ready:** join/start block 28's task immediately so its one 10-second deadline begins at the first Stop; latch intent but write no cancel until that exact session has successfully written and flushed execute. Pre-ready failure, exit, timeout, sink rejection, or execute transport failure remains raw and receives no invalid cancel.
- **Running:** join or start the same block-28 cooperative-cancel/grace/tree-kill task without resetting its original deadline.
- **Terminal or exited but draining:** do not restart cancellation; await block-25 completion, both pump finalities, and disposal.
- **Coordinator cleanup in progress:** join the matching exact-handle completion; stale cleanup cannot affect another record.

A launch result with typed OS start failure has no session, so shutdown performs no process operation. A successful start racing cancellation must still return/publish its session per block 25 and immediately joins the captured record's shared task.

Alternative: snapshot only the current session at shutdown. Rejected because an admitted record can be between reservation and session publication. Alternative: detach admission as soon as cancellation is requested. Rejected because it permits a newer worker while the old process or streams remain live.

### Memoize one cancellation and cleanup task per active record

The active record exposes one lock-free or gated `GetOrStartStopTask` operation consumed by Dashboard Stop, host shutdown, and final disposal. The winner invokes block 28 once; all other callers await the same task. The task owns the entire sequence through process completion, stream finality, session disposal, bridge abandonment when necessary, and coordinator release. It records/observes all faults and settles exactly once.

The host-shutdown task is also memoized when there is no active record so repeated callbacks remain cheap and cannot reopen admission. If a user Stop is already running, shutdown closes admission and joins it; it does not reset grace or add a second kill. If normal completion wins first, shutdown joins normal drain/disposal.

Alternative: use separate user-cancel and shutdown cancellation sources. Rejected because they can write duplicate commands, race process-tree kills, dispose streams under active pumps, or produce inconsistent cleanup.

### Treat HostOptions.ShutdownTimeout as an outer budget around the finalized task

Block 28 supplies the sole deadline: a validated internal 10-second production default, measured by its injected `TimeProvider` from the first accepted Stop. Web composition does not override it. Instead, it determines an explicit reserve for one process-tree kill attempt, process exit, stdout/stderr finality, exactly-once disposal, and the stop-order cost of other hosted services, then configures/validates `HostOptions.ShutdownTimeout` to contain the remaining grace plus that reserve. If a Dashboard Stop began earlier, shutdown joins the already-elapsing task rather than restarting ten seconds. The exact reserve and hosted-service registration allocation remain apply-time reconciliation points because they depend on finalized composition order and applied member names.

The host stop token is not passed as cancellation of block 28's owned task or as cancellation of a wait that permits `StopAsync` to claim success. Token cancellation does not shorten/reset the fake-clock deadline, cancel block-25 pumps, abandon the session, detach the coordinator, or skip disposal. The lifecycle owner keeps observing the same task. When grace wins, block 28 alone performs its one whole-process-tree kill attempt and then awaits exit plus both drains. If that platform attempt fails while the process remains alive, the owner remains stopping with the exact session and typed raw failure; it must not dispose live streams or report clean shutdown merely because the Generic Host budget expired.

Alternative: use host-token cancellation to accelerate or duplicate kill. Rejected because that would redefine block 28's fixed deadline and single-attempt policy. Alternative: link the token directly to `WaitForCompletionAsync` and return on cancellation. Rejected because block 25 defines wait cancellation as wait-only and a live child would remain. Alternative: add another timeout around disposal. Rejected because it creates an abandonment path and competing policy.

### Complete resources before releasing projection or coordinator ownership

The cleanup order is: fence admission; join/start block 28; await process exit and stdout/stderr finality; asynchronously dispose the block-25 session once; close block-27 activity/projection ownership through its accepted terminal path or narrow nonterminal abandonment path; then detach only the matching coordinator handle and dispose coordinator cancellation resources. No later admission is possible because stopping is permanent.

Shutdown preserves accepted terminal and raw completion values. It does not call Completed/Cancelled/Failed itself, convert a kill into cancellation, add a fatal UI error, or decide what startup/protocol/exit evidence means. Start failure, no accepted terminal, and forced kill therefore clean ownership now while leaving classification to block 30's future consumer. All cleanup faults are observed and safely logged as host-control diagnostics without embedding raw protocol or secret material.

Alternative: force a Cancelled result during shutdown. Rejected because worker/session execution remains the terminal authority and block 30 owns missing/contradictory terminal classification. Alternative: release coordinator state before drains finish. Rejected because it creates an idle-looking control plane with live process resources.

### Make startup failure and teardown repetition use the same state machine

Lifecycle setup proceeds in an order where the admission fence exists before admission can open. If callback registration, hosted startup, or later Web startup fails, the owner transitions permanently to stopping and calls the same memoized cleanup. `StopAsync`, the application callback, and asynchronous disposal can repeat in any order; only the first transition captures ownership and all paths observe the same task. Callback registrations are disposed after cleanup and never reopen admission.

Alternative: rely on the service provider to dispose partially constructed services. Rejected because disposal order alone does not atomically fence admission or escalate a live process.

## Risks / Trade-offs

- [Applied coordinator/session member names may differ from planning names] → Re-read the applied blocks 13, 25, 27, and 28 source immediately before implementation and adapt block-29 composition without adding a parallel owner or Stop policy.
- [Generic Host timeout is shared across hosted services] → Make registration order explicit and validate the remaining part of block 28's 10-second grace plus kill/drain/disposal reserve against `HostOptions.ShutdownTimeout`; do not reset or shorten the shared task.
- [A platform kill or inherited pipe handle fails to settle] → Preserve exact-session ownership and stopping state, observe/log the typed raw failure, and never claim clean host cleanup or dispose live resources.
- [Projection cleanup could erase useful failure evidence] → Use block 27's exact identity-checked abandonment API and preserve raw observations for block 30.
- [Startup failure can occur before the normal stopping callback] → Fence admission before opening it and invoke the same cleanup from the hosted owner's startup failure path.
- [Hosted-service registration order can silently change semantics] → Add descriptor/reference-identity and real host lifecycle tests rather than depending only on unit-level callback tests.

## Migration Plan

1. Re-read the applied block-13 coordinator and blocks 25, 27, and finalized 28 APIs to map their exact names for active-handle ownership, startup ownership transfer, nonterminal abandonment, the shared 10-second `TimeProvider` Stop task, tree-kill facts, and process/stream finality; reconcile names only and do not invent a parallel seam.
2. Extend the existing singleton coordinator/lifecycle owner with the permanent admission fence and memoized host-shutdown task; factory-alias its one hosted contract and register the lifetime callback.
3. Compose and validate worker grace plus cleanup reserve against `HostOptions.ShutdownTimeout` without introducing a second cancellation policy.
4. Wire the exact active-record lifecycle to session publication, block-28 stop, block-25 completion/disposal, block-27 cleanup, and coordinator detachment in the specified order.
5. Add gated unit/host tests and block-26 fixture tests, then run focused tests and the normal suite.
6. Roll back the Web-host composition and tests as one unit; no persisted or wire migration is introduced.

## Audit Reconciliation

Shutdown is clean only after the exact owned worker has exited and both stdout and stderr drains have reached finality, followed by exact-handle cleanup. A rejected or failed tree kill leaves the session unresolved: shutdown must retain ownership/failure evidence and must not report clean completion, release the handle as settled, or treat a terminal frame alone as sufficient.

