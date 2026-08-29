## Context

See [proposal.md](proposal.md) and [specs/processing/scheduled-child-worker-execution/spec.md](specs/processing/scheduled-child-worker-execution/spec.md). Block 11 keeps the exact eligibility count, zero gate, skipped snapshot, non-empty processing-config snapshot, batches, geodata, persistence, and terminal result inside `ProcessingRunExecutor`. Blocks 12–13 separate schedule timing from the singleton coordinator and establish accepted order: active handle/CTS publication → `MarkPending()` → exact-request adapter arm → dispatch, with the scheduled caller awaiting terminal cleanup. Block 33 freezes an internal backend selection on the admitted handle and lazily resolves only that backend.

The child request protocol carries only run identity and trigger. The worker acquires the PostgreSQL advisory lock after `run-started` and before its authoritative count or heavy work. Consequently a Web pre-launch check is advisory: it cannot reserve a work set, replace the worker count, or prove the later advisory lock will be available.

## Goals / Non-Goals

**Goals:**
- Avoid child startup for a scheduled occurrence whose initial eligibility check finds no work.
- Keep local admission, pending visibility, cancellation, state identity, and scheduled awaiting coherent across predispatch and child paths.
- Reuse the existing child backend and terminal/finality contracts without constructing an unselected backend or geodata graph.
- Leave a narrow replacement point for blocks 57–58 while making the initial implementation behavior explicit.

**Non-Goals:**
- No eligibility predicate, executor pipeline, worker protocol, advisory-lock, schedule parsing/waiting, Dashboard/manual-run, or deployment-mode redesign.
- No stable work-set transaction, reservation, watermark, existence-query optimization, index, detector telemetry, or new public configuration.
- No fallback to in-process execution, replacement worker, automatic retry, catch-up, replay, or request resubmission.
- No edits to block 34 or ownership of block 36's dedicated empty-path regression.

## Decisions

### 1. Add a scheduled-only boolean predispatch gate with a count-backed adapter

Introduce a narrow internal fakeable contract at the coordinator predispatch boundary, named `IScheduledRunWorkGate.HasWorkAsync(CancellationToken)` in this plan. The production adapter calls the existing exact eligibility-count repository operation once and returns `count > 0`. It exposes no count to callers and resolves no executor, skipped store, AppConfig, batch, protocol, resolver, cache, airport, or geodata service.

This is intentionally not the general `IProcessingWorkDetector` promised by block 57. During block 57, replace/alias this temporary scheduled gate with the general boolean detector while preserving the call site and outcomes; block 58 then changes only its implementation to a bounded existence query. Alternative: pass the detector's numeric count to the worker. Rejected because block 11 makes the executor's independently observed count authoritative and the protocol intentionally carries no eligibility or work set. Alternative: introduce the final Phase 8 abstraction now. Rejected because it would absorb numbered-block 57's ownership and broaden this migration step.

The initial implementation performs two exact counts for an eligible occurrence: one in Web to avoid an empty launch and one in the worker under the advisory lock. This is accepted transition cost, not consistency. The observations race independently. A positive Web result followed by worker zero launches one child that completes via block 11's normal zero path. A Web zero followed by newly eligible work completes locally and leaves that work for the next ordinary trigger. No result causes fallback or retry. Blocks 57–58 own removal of the pre-launch exact count, not the worker's authoritative count.

### 2. Insert detection after complete local admission setup and before any backend activation

Preserve block 13 order exactly:

1. process-local admission creates the scheduled request and publishes the matching active handle and coordinator CTS;
2. block 33's immutable backend value is frozen on that handle;
3. `MarkPending()` runs immediately;
4. the exact-request state adapter is armed;
5. the scheduled gate runs once with the coordinator-owned token;
6. only a work result creates the per-run scope, resolves the selected child backend, and dispatches it once.

The gate belongs to the admitted execution operation, not cron calculation or `ProcessingBackgroundService`. A local busy rejection retains the exact scheduled-contention control-plane line and stops before request ID creation, pending mutation, detection, scope creation, backend selection/resolution, or launch. Admission remains held during detection and every local finalization, so manual or scheduled retriggers cannot race into the pending/cleanup gap.

Alternative: detect before admission. Rejected because concurrent due/manual triggers could duplicate a costly query, cancellation would lack the accepted handle, and `MarkPending()` would no longer immediately follow lock acquisition. Alternative: put detection inside the child backend. Rejected because process startup would already have occurred. Alternative: let the scheduler branch directly to the launcher. Rejected because it duplicates coordinator ownership and creates new scheduler semantics.

### 3. Close predispatch paths through an identity-checked local state finalizer

Extend the existing adapter/coordinator abandonment boundary with a narrow identity-checked predispatch finalizer; do not create a worker event, `ProcessingRunResult`, or second reporter session.

- **No work:** apply the same adapter operations as accepted eligibility zero followed by completed zero accounting. This resets total/counters/LastError, sets start/completion timestamps, appends the exact existing zero line and then `Run complete. Processed=0 Skipped=0 Errors=0`, clears activities, and returns idle.
- **Active cancellation:** apply the established pre-eligibility cancellation projection: append `Run cancelled.`, transition inactive with completion time, append the existing summary from the pending snapshot, and do not add or replace an error.
- **Unexpected failure:** render bounded safe detector detail through the established pre-eligibility failed projection: add one legacy fatal UI error, transition inactive with completion time, append the existing summary, and preserve the adapter's pre-eligibility snapshot semantics.

Each operation accepts only the exact armed request, is idempotent for repeated cleanup, rejects stale identities, and is followed by matching-handle detachment/disposal. Projection or callback faults use block 13's existing abandonment cleanup so admission cannot strand. The scheduled start operation awaits this local closure and handle release before reporting accepted-after-terminal.

Alternative: fabricate child protocol terminal events. Rejected because no child/session exists. Alternative: silently clear `IsRunning`. Rejected because it would omit timestamps/logs, leave stale state unexplained, and violate blocks 1 and 9. Alternative: call the in-process executor for no-work finality. Rejected because it repeats the gate, resolves the forbidden graph, and defeats isolation.

### 4. Eligible work uses the existing selected child backend unchanged

After a work decision, create the block-33 run scope and resolve only the frozen selected backend. In the Phase 5 composition established by prerequisites, `ChildWorker` is selected explicitly; this change adds no trigger-specific selector and does not edit block 34. The backend receives the same scheduled request, exact armed adapter/reporter, and coordinator token, then reuses command building, ready/execute sequencing, event bridge, cancellation owner, classifier/finalizer, and scope cleanup.

The scheduled caller remains awaiting until authoritative terminal handling and all process/stdout/stderr finality and exact-session cleanup settle. A worker authoritative count of zero is a normal completed child run. PostgreSQL advisory contention is the existing typed Busy fact: one failed worker terminal, exit 3, zero domain work. It is not the local `RejectedAlreadyRunning` schedule outcome and does not cause a retry. Detector success followed by child start, protocol, projection, crash, cancellation, or cleanup failure stays on that same run and backend.

### 5. Preserve three separate configuration snapshots

Do not combine or transmit configuration surfaces:

- **Schedule plan:** block 12 copies Enabled/Cron before calculating and waiting. That occurrence stays pinned; saves do not wake it.
- **Backend/launch:** block 33's internal immutable selection is frozen on admission, and block 24's existing command descriptor captures its established executable, working-directory, argument, and inherited-environment launch facts. No AppConfig/UI/CLI setting is added.
- **Processing:** the detector reads only database eligibility and does not read AppConfig. The request remains run ID plus Scheduled trigger. Inside the child, after advisory-lock acquisition and the authoritative nonzero exact count, block 11 reads one processing-config snapshot for all batches. A save before that snapshot may affect the run; a save afterward waits for a later run. The Web does not attempt to synchronize a processing snapshot with the child.

This preserves existing consistency boundaries rather than claiming scheduler, detector, and worker see one atomic configuration version.

### 6. Keep block ownership explicit

Block 35 adds functional eligible/empty/busy/cancel/failure coverage at the scheduler/coordinator gate with fakes. Block 36 retains the dedicated regression proving exactly one detector call, zero backend/launcher/geodata construction or access, zero protocol events, and defined idle state. Block 57 replaces the temporary gate with its general boolean detector without changing behavior; block 58 replaces count-backed detection with an existence query. Neither future block may remove the worker executor's authoritative count.

## Risks / Trade-offs

- [Eligible schedules execute two exact count queries] → Document this as the temporary correctness-preserving implementation; blocks 57–58 optimize only the pre-launch query.
- [Eligibility changes between detector and worker] → Treat detection as advisory and test both directions; never pass a work set or retry.
- [Predispatch finalization could conflict with worker terminal ownership] → Restrict it to states before backend resolution, require exact identity/idempotence, and emit no worker event or result.
- [A selected child graph is accidentally resolved for no-work/failure] → Use fail-on-resolution backend, launcher, protocol, executor, and geodata fakes; block 36 adds the focused regression.
- [Detector exceptions leak database details] → Feed only bounded safe detail to the existing fatal presentation; keep raw exceptions in controlled logger diagnostics.
- [Prerequisite APIs differ when applied] → Re-read completed blocks 11–13 and 25–33, preserve these ownership/order contracts, and stop rather than add a parallel coordinator or launcher path.

## Migration Plan

1. Re-read applied prerequisite APIs and tests without modifying block 34.
2. Add the scheduled-only gate contract, count-backed adapter, and DI registration that does not resolve geodata or either backend.
3. Insert the gate after admission/pending/arm and add identity-checked local no-work/cancel/failure finalization.
4. Route work through the existing selected child backend and preserve scheduled awaiting through final cleanup.
5. Add focused block-35 tests; leave the dedicated no-launch/geodata/protocol regression to block 36.
6. Run focused worker suites, the normal test command, strict OpenSpec validation, and a scope diff. Rollback reverts this gate/finalizer wiring only; it adds no persistent data or runtime fallback.

## Audit Reconciliation

Scope is scheduled accepted execution only and consumes the established detector/local-finalizer contracts and prerequisites; it neither changes manual routing nor makes child-worker the default. The default remains in-process until block 37. Its detector-zero local path emits no worker producer event or worker result, while a canonical advisory Busy remains a child terminal distinct from local admission rejection.

