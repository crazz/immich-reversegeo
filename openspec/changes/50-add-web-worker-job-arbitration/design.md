## Context

See `proposal.md` for motivation and `specs/worker-job-arbitration/spec.md` for behavior. Finalized block 47 supplies one lower-case GUID-D `JobId`, closed worker kinds, generic session/finality rules, and immutable descriptor metadata; block 48 declares `CoordinateLookup` cancellable and in the same exclusive-heavy-geodata resource class as `ProcessAssets`. Finalized block 49 already routes Lookup through a closed `Admitted(handle)` / `Busy(metadata)` / `Unavailable(reason)` seam backed temporarily by a lookup-only atomic gate. Therefore block 50 follows and replaces that gate; it is not block 49's prerequisite.

`ProcessingBackgroundService._runLock` and `ProcessingState` remain processing-specific. The PostgreSQL advisory lock is acquired only inside `ProcessAssets` workers and is the only existing cross-process exclusion. This change must preserve the launcher rule that completion includes process exit, stdout/stderr EOF and drain, protocol finalization, classifier/bridge cleanup, and handle release.

## Goals / Non-Goals

**Goals:**

- Make one process-local Web coordinator the only heavy-worker admission authority for processing and Lookup, with a descriptor-compatible path for block 51's `CacheMutation`.
- Make admission, active identity/lifecycle, cancellation ownership, release, and shutdown races linearizable and independently testable.
- Preserve processing-specific and Lookup-specific projections while sharing only generic arbitration state.
- Remove block 49's temporary gate without changing its page/controller contract.

**Non-Goals:**

- Implement the concrete `CacheMutation` request/handler/operations owned by block 51, cache deletion/reset semantics owned by blocks 52/54, or a local no-work detector owned by later processing work.
- Add a durable or in-memory wait queue, retry loop, reservation handoff, preemption, weighted priority, fairness guarantee, starvation prevention, or automatic cancellation of an active job for a new request.
- Replace the processing-only PostgreSQL advisory lock or claim cross-container exclusion for Lookup/cache jobs.
- Merge Lookup/cache status into `ProcessingState`, expose raw worker errors/PIDs publicly, or change v1/v2 wire contracts.

## Decisions

### 1. Use descriptor resource class for one exclusive heavy slot

The coordinator consumes the immutable typed descriptor plus typed request-origin facts established by block 47. Descriptors retain exact `WorkerJobKind` (`ProcessAssets`, `CoordinateLookup`, future `CacheMutation`), a stable friendly category for safe busy/status copy (Processing, Lookup, Cache maintenance), cancellability, heavy/geodata flags, and the admission resource class. Every descriptor marked heavy/geodata-bearing uses the one `ExclusiveHeavyGeodata` slot. No two such jobs can be admitted concurrently in one Web process, even when kinds differ.

The category is presentation/policy metadata, not another job discriminator. Cache operation/source details stay in block 51's typed payload and are not arbitration keys. Unknown resource classes, inconsistent heavy metadata, duplicate descriptor registrations, or unregistered kinds fail startup validation rather than becoming runtime guesses.

Alternative: switch directly on job kind. Rejected because it duplicates descriptor facts and would require coordinator edits for every typed operation. Alternative: per-kind locks. Rejected because processing, Lookup, and cache mutation contend for the same process/geodata resources.

### 2. Return one closed, fail-fast admission result

The shared boundary returns exactly one of:

- `Admitted(handle)`: the caller now owns the reservation and may launch exactly one session with the already-selected `JobId` and descriptor.
- `Busy(active snapshot)`: another exclusive heavy owner exists; no worker starts and the snapshot contains only safe exact kind/category/origin/lifecycle facts needed by the caller.
- `Unavailable(safe reason)`: the Web root cannot accept work before launch, including an active shutdown fence or an unavailable launch facility; no worker starts. Descriptor/DI corruption is a startup failure, not a recoverable unavailable result.

Acquisition is one atomic compare-and-set under a short synchronization boundary; the coordinator never holds that boundary across process launch or async work. Policy is first successful atomic admission wins. There is no waiting, queue, preemption, priority promotion, retry, or ownership transfer, and therefore no fairness or starvation guarantee. Scheduled work is lower impact only because it performs eligible lightweight preflight first and fails fast on contention; once admitted it has the same non-preemptible ownership as every other job.

Alternative: give manual processing priority over Lookup or cancel scheduled work when a user clicks. Rejected because without a queue the ordering is race-dependent, and preemption complicates cache publication and finality. Alternative: retain one semaphore per caller. Rejected because it cannot produce a coherent active snapshot or cross-kind exclusion.

### 3. Preserve one exact identity and use handle capability for ownership

A caller creates/selects the canonical `JobId` once before admission and passes it with kind, category, resource class, cancellability, and origin. For `ProcessAssets`, it is exactly `ProcessingRunRequest.RunId`; for Lookup it is block 49's exact operation job ID. Admission does not mint a reservation ID, attempt ID, run ID, or public generation. The in-memory admitted handle object itself is the unforgeable release capability; active-state mutation additionally checks the exact `JobId` and kind.

The immutable active snapshot contains exact `JobId`, exact kind, safe category, origin (manual, scheduled, Lookup, or later cache UI), cancellability, lifecycle, admission/start timestamps, and nullable child PID. Lifecycle advances monotonically through equivalents of Admitted, Starting, Running, Stopping, and Finalizing; PID is absent before successful process creation and is diagnostic only. Worker events cannot select or release a different owner.

Alternative: identify the active owner only by kind or PID. Rejected because repeated jobs and pre-start failures become ambiguous. Alternative: add a lease GUID. Rejected because block 47 requires one correlation identity end to end.

### 4. Make the admitted caller/session own cancellation and final release

The coordinator arbitrates; it does not become a second process/session manager. The admitted caller binds exactly one launched session (or one startup failure finalizer) to its handle. Only that owner may invoke the normal cancel path, using the existing exact-JobId cooperative cancel, shared grace deadline, and process-tree escalation. A busy caller receives no handle and cannot cancel the active job. Generic UI status is read-only and is not a global cancel endpoint.

Cancellation, a worker terminal, crash, protocol/transport failure, launch failure, or disposal does not release immediately on first evidence. The owner/classifier finalizes the outcome, awaits the established exit/EOF/drain/bridge cleanup boundary when a process exists, then disposes/releases the handle exactly once in `finally`. Pre-process launch failure follows the same classifier-before-release rule without inventing PID, terminal, or exit. Stale, duplicate, wrong-kind, wrong-identity, and non-owner release calls are harmless and cannot clear another job.

Alternative: release on terminal receipt. Rejected because the child and stream pumps may still be active. Alternative: let the coordinator infer process death. Rejected because it would duplicate launcher/classifier ownership.

### 5. Reconcile processing triggers and no-work timing

Manual ProcessAssets, Lookup, and future CacheMutation all attempt the same slot and obey first-admitted wins: manual processing during Lookup is Busy; Lookup during processing is Busy; duplicate manual processing is Busy; and an admitted scheduled job is not preempted by later interactive work.

Manual processing retains its established bypass of local no-work detection and attempts admission immediately after request snapshot/identity validation. For scheduled processing, any lightweight local eligibility/no-work detector available in the landed scheduler runs before a `JobId` is created, before `ProcessingState.MarkPending()`, and before the heavy reservation. A no-work result launches nothing and never makes the coordinator busy. A positive result then creates the exact run identity and atomically attempts admission; another job may win that race, in which case the schedule records/coalesces the existing skipped/pending trigger semantics but does not queue inside the coordinator or launch a worker.

After successful ProcessAssets admission, call `ProcessingState.MarkPending()` immediately before asynchronous launch so the historical lock-held/IsRunning-false window remains closed. Replace `_runLock` as a heavy admission authority; retain only any narrow scheduler-trigger serialization that is proven necessary and cannot admit a worker independently. The worker still acquires the PostgreSQL advisory lock; advisory busy remains exit 3 and finalizes/releases the local slot.

Block 50 fixes this ordering contract but does not introduce or redefine the later detector's query/caching semantics.

Alternative: reserve before no-work detection. Rejected because a lightweight negative check would unnecessarily block Lookup/cache work. Alternative: detect positive and reserve a future place. Rejected because that is a queue/reservation handoff.

### 6. Keep arbitration status separate from capability projections

Expose an immutable read-only coordinator snapshot/event stream for generic active-job diagnostics. It answers idle versus active with bounded friendly category/origin/cancellability/lifecycle/timing facts and omits exact JobId and PID; it does not contain processing counts/logs/activities, Lookup diagnostics/results, cache metadata, raw error text, protocol frames, or cancellation controls. `ProcessingState` remains the sole processing projection and receives only ProcessAssets lifecycle/events. Lookup keeps block 49's page-scoped state. Later cache UI consumes its own result state.

Block 44's existing Dashboard/NavMenu card remains ProcessAssets-focused and MUST NOT map the generic snapshot or non-ProcessAssets lifecycle into that card. Lookup and later cache pages own their capability state. Exact JobId and PID remain controller-internal ownership/correlation facts and are never generic UI fields. Projection observers cannot acquire, release, or cancel admission.

Alternative: generalize `ProcessingState` into all-job state. Rejected because it conflates processing counts/schedule behavior with unrelated diagnostic and mutation work.

### 7. Fence shutdown before stopping the current owner

Coordinator shutdown is linearizable with admission: atomically enter a permanent not-accepting fence first, so every later request returns `Unavailable`; then, if an owner exists, invoke the one owner-bound stop operation and await its bounded session finality/release. The fence never reopens during host teardown, and shutdown never releases the slot early, fabricates a terminal, or performs a second kill. If admission and shutdown race, exactly one wins: either admission returns Admitted and shutdown owns stopping that exact handle, or admission returns Unavailable and no process starts.

Normal user/circuit cancellation remains caller-owned. Shutdown is the sole coordinator-initiated exception and delegates to the owner-bound session stop callback rather than targeting a PID directly. Repeated shutdown calls join one task.

Alternative: stop first and set the fence later. Rejected because a new job could enter during teardown.

### 8. Keep cross-process scope explicit

The coordinator is a singleton per Standard or Web-only Web process. It uses no database row, filesystem lock, or distributed lease. Only ProcessAssets continues to acquire the existing PostgreSQL advisory run lock in the worker; CoordinateLookup and future CacheMutation do not acquire it, and local busy never maps to worker exit 3.

Consequently, multiple Web containers can each admit one heavy Lookup/cache job concurrently, and one container's Lookup/cache can overlap another container's ProcessAssets. Deployments requiring strict heavy-job exclusion must run a single interactive Web control plane until a later distributed resource lease is designed. The coordinator must not imply otherwise in status or docs.

Alternative: reuse the processing advisory lock for every job. Rejected because its finalized key/exit semantics and database dependency are processing-specific, and broadening it belongs to a separate distributed-coordination change.

### 9. Replace block 49's temporary gate behind the existing seam

Implement the shared coordinator adapter against block 49's existing closed admission/launch interface and descriptor metadata. Delete the temporary lookup-only gate implementation, DI registration, and tests that assert lookup-only ownership; do not retain nested gates. Rerun block 49's busy/unavailable/release/cancel/crash/disposal/reuse suite unchanged at the page boundary, adding cross-kind owner metadata assertions. `Lookup.razor` and its operation-generation logic do not change.

Add ProcessAssets adaptation only after the coordinator contract is race-tested. Block 51 later registers/uses the same resource class for concrete CacheMutation work; block 50 reserves compatibility and tests fake future descriptors but does not implement those operations.

Alternative: keep the temporary Lookup gate in front of the coordinator. Rejected because dual ownership can deadlock, return misleading Busy, and release in the wrong order.

## Risks / Trade-offs

- [Fail-fast first-wins can starve a repeatedly colliding caller] → State the lack of fairness explicitly, keep busy results actionable, and require a later queued policy change if fairness becomes necessary.
- [Release occurs before all child resources are gone] → Couple release to classifier plus process/stream/bridge finality and test terminal-versus-EOF/drain races.
- [A stale handle clears a newer owner] → Use handle capability plus exact identity/kind checks and idempotent release.
- [Scheduled detector-to-admission race loses eligible work] → Record/coalesce existing scheduler semantics and retry only on a later scheduler tick; never create an implicit coordinator queue.
- [Shutdown and admission race launches an orphan] → Fence atomically before stop and bind the admitted owner stop operation before returning launch ownership.
- [Multiple Web containers defeat local exclusion] → Keep the caveat explicit and retain the processing advisory lock; do not market local arbitration as distributed safety.
- [Block 49 temporary types differ when applied] → Adapt to the exact landed seam and descriptor shapes, deleting rather than wrapping the temporary implementation; stop if doing so would require parallel identities or wire DTOs.

## Migration Plan

1. Re-read applied blocks 47–49 and bind to their exact descriptor, JobId, admission-result, lookup handle, session finality, and cancellation contracts; stop rather than create parallel DTOs or edit those finalized changes.
2. Add the coordinator contract/state machine and deterministic race tests, including startup validation and shutdown fencing, without routing production callers.
3. Replace block 49's temporary lookup-only implementation/registration with the coordinator adapter, delete its gate-specific tests, and rerun the unchanged Lookup controller/page contract suite.
4. Adapt manual and scheduled ProcessAssets triggers, preserve detector-before-reservation and immediate post-admission `MarkPending()`, and remove `_runLock` as an independent admission path.
5. Add composition, cross-kind, cancellation/finality, shutdown, advisory-lock, and multiple-Web-container boundary documentation/tests. Leave only a typed fake/contract seam for block 51; do not implement it.
6. Roll back by restoring block 49's temporary gate and the prior processing-local admission together; never leave Lookup ungated or run temporary and shared gates simultaneously. No data, protocol, or database migration is required.
