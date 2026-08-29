## 1. Prerequisite Reconciliation

- [ ] 1.1 Re-read the applied block-13 coordinator and blocks 25, 27, and finalized 28 source/tests; map the exact active-handle, session-publication, bridge-abandonment, shared 10-second `TimeProvider` Stop task, tree-kill fact, and process/stream-finality member names without adding a parallel seam.
- [ ] 1.2 Define the apply-time host budget allocation from the remaining portion of block 28's fixed 10-second deadline plus an explicit process-tree-kill/exit/drain/disposal reserve and hosted-service registration order, then validate it against `HostOptions.ShutdownTimeout` before worker admission opens.

## 2. Host Lifecycle and Admission Fence

- [ ] 2.1 Extend the exact singleton coordinator/lifecycle owner with an idempotent `BeginShutdown` transition that closes admission and captures the matching active record atomically before any cancellation callback or process I/O.
- [ ] 2.2 Register one `IHostApplicationLifetime.ApplicationStopping` callback that invokes the nonblocking transition and factory-alias the same singleton as the hosted owner whose `StopAsync` awaits the shared cleanup task.
- [ ] 2.3 Make callback registration, partial `StartAsync` failure, repeated host stop, repeated lifetime notification, and asynchronous disposal converge on the same permanently-stopping state and memoized task.

## 3. Shared Worker Cleanup Composition

- [ ] 3.1 Attach pending and concurrently starting work to the captured active record so cancellation-before-launch prevents dispatch where possible and a successful OS start racing shutdown still publishes an owned session into the same cleanup task.
- [ ] 3.2 Join or start block 28's exact cancellation task once for pre-ready and running sessions; preserve its first-Stop timestamp, one 10-second fake-clock deadline, execute-flush cancel eligibility, and at-most-one tree-kill attempt while concurrent Dashboard Stop and host shutdown join it.
- [ ] 3.3 Keep host stop-token cancellation outside block 28's owned task and wait semantics: do not reset/shorten grace, duplicate escalation, cancel pumps, detach ownership, or report clean `StopAsync` completion before process exit, both stream finalities, and exactly-once disposal.
- [ ] 3.4 For terminal, exited, startup-failed, and cleanup-in-progress records, join the existing completion/disposal path and avoid duplicate cancellation, process operations without a session, or stale-handle cleanup.
- [ ] 3.5 After session finality, invoke block 27's exact accepted-terminal or nonterminal abandonment cleanup and then detach only the matching coordinator record without synthesizing or classifying a terminal outcome reserved for block 30.

## 4. Deterministic Unit and Host Tests

- [ ] 4.1 Add `TaskCompletionSource` gates with asynchronous continuations for shutdown races against idle admission, pending preparation, OS start ownership transfer, pre-ready startup, running execution, accepted terminal, process exit before stream EOF, and final coordinator cleanup; use no sleeps.
- [ ] 4.2 Prove admission closes before cancellation and every racing request is either rejected as stopping or captured by cleanup, with no request identity, pending mutation, launch, or dispatch after the fence.
- [ ] 4.3 Prove lifetime callback and hosted `StopAsync` reference the same singleton/task; overlapping user Stop, repeated stop callbacks, repeated disposal, and stale cleanup produce at most one cancel write, grace wait, process-tree kill, drain lifecycle, and resource disposal.
- [ ] 4.4 With fake `TimeProvider`, prove host-token expiry neither resets nor shortens block 28's original 10-second deadline and cannot cancel pumps or return cleanly; prove invalid remaining-grace/reserve/`HostOptions.ShutdownTimeout` composition fails startup before admission opens.
- [ ] 4.5 Prove startup failure with no session performs no process operation, startup success racing cancellation remains owned, and partial host startup failure runs the same cleanup.
- [ ] 4.6 Prove cleanup waits for process exit plus both redirected-stream finalities, disposes all resources, closes bridge activity, releases only the matching coordinator record, and never creates Completed/Cancelled/Failed or block-30 crash/protocol classification.

## 5. Process-Fixture and Regression Verification

- [ ] 5.1 Extend the block-26 fixture matrix with cooperative, deliberately unresponsive, high-stderr, trailing-output-after-exit, and startup-race cases; use positive gates for ordering and finite deadlines only as failure/reaping watchdogs.
- [ ] 5.2 For cooperative exit and accepted tree-kill cases, assert no surviving process tree, complete stdout/stderr drainage, exactly-once resource disposal, idle-but-stopping coordinator cleanup, and preserved raw observations without terminal invention; for platform kill failure, assert retained exact-session ownership/stopping state and no false disposal or clean-shutdown claim.
- [ ] 5.3 Run focused coordinator/host-lifecycle/session tests, the block-26 process-fixture tests, and `npm run test` with default exclusions.
- [ ] 5.4 Run `openspec validate 29-stop-active-worker-on-web-shutdown --strict`, inspect final `openspec status --change 29-stop-active-worker-on-web-shutdown`, and review the scope diff to confirm block 28 and block 30 were not changed or advanced.

## Audit Reconciliation

Shutdown is clean only after the exact owned worker has exited and both stdout and stderr drains have reached finality, followed by exact-handle cleanup. A rejected or failed tree kill leaves the session unresolved: shutdown must retain ownership/failure evidence and must not report clean completion, release the handle as settled, or treat a terminal frame alone as sufficient.

