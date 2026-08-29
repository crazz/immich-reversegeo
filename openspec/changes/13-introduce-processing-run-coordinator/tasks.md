## 1. Prerequisite re-read and characterization

- [ ] 1.1 Apply blocks 7–11 and concurrent block 12 first, then re-read their finalized source, tests, and DI for the request/trigger models, reporter adapter arm and cleanup operations, executor signature, scheduler execution-start contract, and hosted-service shape; stop rather than create duplicate seams if any prerequisite is absent.
- [ ] 1.2 Add or retain deterministic characterization for silent manual contention, the exact scheduled skipped-pass message, active-handle versus pending timing, prompt manual dispatch return, current scheduled-cancel mismatch, terminal-before-release ordering, and concrete/hosted singleton identity.
- [ ] 1.3 Record the exact applied block-12 interface and replace its temporary adapter without editing cron calculation, due-time waiting, startup initialization, or other block-12 ownership.

## 2. Coordinator contract and singleton composition

- [ ] 2.1 Define one internal/common admission result with Accepted, AlreadyRunning, and Stopping plus matching completion identity, a prompt Dashboard-facing `Manual` surface, and an implementation of block 12's exact `Scheduled` asynchronous RejectedAlreadyRunning/AcceptedAfterTerminal contract; expose no `RunOnce` or mutable execution internals and create no request on rejection.
- [ ] 2.2 Add one singleton coordinator with a short admission gate, open/stopping state, and one optional active handle containing the request, coordinator-owned CTS, owned execution/completion tracking, and cleanup identity.
- [ ] 2.3 Factory-alias the concrete coordinator, Dashboard-facing contract, finalized block-12 execution-start contract, and coordinator host-lifecycle registration to the exact same instance; retain concrete `ProcessingBackgroundService` and its hosted alias as a separate exact scheduler instance, assert both identity groups with `ReferenceEquals`, and verify scheduler → start contract → coordinator with no dependency cycle.

## 3. Admission, projection, and dispatch

- [ ] 3.1 Implement the atomic idle-to-active reservation so a fresh non-empty trigger-specific request and live CTS are published before pending notification; AlreadyRunning and Stopping perform no identity, state, arm, CTS, or executor work.
- [ ] 3.2 Preserve accepted sequencing as reservation/CTS publication → `MarkPending()` → exact-request reporter-adapter arm → one guarded in-process `ProcessingRunExecutor` dispatch, returning Accepted only after dispatch ownership is established.
- [ ] 3.3 Consume the finalized reporter adapter's identity API or add a narrow control-plane abandonment operation on that exact singleton so setup/reporter faults can release matching pending/arm ownership without fabricating a domain result or mutating `ProcessingState` from the executor.
- [ ] 3.4 Observe every owned execution task, leave Completed/Cancelled/Failed terminal reporting solely to the executor/session, log infrastructure faults once, and use unconditional exact-handle cleanup without duplicate terminal events.

## 4. Cancellation, retrigger, and shutdown

- [ ] 4.1 Route cancellation through the coordinator-owned active handle; make immediate post-pending cancellation reliable, make idle cancellation a no-op, and intentionally allow the existing Dashboard Cancel command to cancel manual or scheduled runs.
- [ ] 4.2 Detach only the matching active request, dispose its CTS once after execution/reporting stop using it, and prove completed, cancelled, failed, setup-faulted, and reporter-faulted runs all permit a later request with a new run ID.
- [ ] 4.3 Close admission atomically at application stopping, cancel the active local run, and await its owned completion within the host shutdown token; make repeated stop/cancel/cleanup calls idempotent and prevent stale cleanup from clearing a newer handle.
- [ ] 4.4 Preserve the boundary that this shutdown path is cooperative and in-process only; do not add worker grace/kill/drain behavior, protocol/process launch, or PostgreSQL advisory locking.

## 5. Scheduler and Dashboard migration

- [ ] 5.1 Route the applied block-12 scheduled execution-start call through the coordinator, return RejectedAlreadyRunning immediately or AcceptedAfterTerminal only after matching cleanup, propagate its stopping token into accepted cancellation/await, and retain the exact contention log while leaving cron/due-time calculation and waiting unchanged.
- [ ] 5.2 Change `Dashboard.razor` to inject the narrow coordinator rather than `ProcessingBackgroundService`, keep Run Now prompt/nonblocking, make its admission result honest during terminal cleanup or shutdown, and route Cancel to the active coordinator handle.
- [ ] 5.3 Remove `_runLock`, `_runCts`, manual fire-and-forget dispatch, request/arm duplication, and terminal release ownership from `ProcessingBackgroundService` only after both call sites use the coordinator; retain startup initialization and block-12 scheduling responsibilities.

## 6. Deterministic verification and scope

- [ ] 6.1 Use per-test coordinator instances, fixed request-ID/time inputs where the finalized seams allow, `TaskCompletionSource` gates with asynchronous continuations, and fake executor/projection/lifetime collaborators; use no sleeps, cron waits, live database, geodata, Blazor circuit, or child process.
- [ ] 6.2 Prove manual-during-scheduled and scheduled-during-manual exclusion in both directions, trigger metadata and unique accepted IDs, no ID/arm/dispatch on rejection, silent manual contention, the exact scheduled skipped log, and no Web-coordinator contract path for `RunOnce`.
- [ ] 6.3 Prove active CTS publication precedes pending observation, immediate Cancel reaches the accepted token, scheduled Cancel is effective, idle Cancel is harmless, and duplicate requests never replace the active request or CTS.
- [ ] 6.4 Prove one executor dispatch and no duplicate terminal report; Completed, Cancelled, Failed, synchronous setup fault, reporter fault, and projection callback fault each release local admission and are fully observed.
- [ ] 6.5 Prove retrigger after cleanup receives a new ID, stale/late cleanup cannot clear a newer run, and the projection-idle/cleanup gate yields an explicit AlreadyRunning decision rather than false acceptance.
- [ ] 6.6 Prove shutdown-versus-admission linearization, rejection after stopping, active cooperative cancellation/drain, idempotent repeated stop, and exact DI reference identity across concrete, Dashboard, block-12 start, and hosted aliases while the scheduler remains its own single hosted instance.
- [ ] 6.7 Run focused coordinator/adapter/scheduler/Dashboard-boundary/DI tests, `npm run test`, `openspec validate 13-introduce-processing-run-coordinator --strict`, and a diff proving no block 12, executor-pipeline, protocol/process, cron, or cross-process-lock scope was edited.
