## Context

See `proposal.md` for motivation and `specs/processing/manual-child-worker-execution/spec.md` for behavior. In the current checkout, `Dashboard.razor` still injects `ProcessingBackgroundService`, loads dashboard statistics before calling `TriggerRunAsync()`, calls `CancelRun()` for Stop, and observes `ProcessingState`. The current service still owns its semaphore, manual CTS, in-process pass, and direct state mutations. None of the prerequisite coordinator, backend selector, launcher, event bridge, cancellation owner, or classifier types are present in source yet.

This design therefore consumes, rather than recreates, the finalized contracts from blocks 13, 25, 27, 28, 30, and 33. Their required ownership is stable: the coordinator owns local admission, run identity, cancellation, pending/reporting preparation, dispatch, and exact-handle cleanup; the internal backend selector chooses one lazily resolved backend; the launcher owns child process and stream mechanics; the bridge validates and projects typed events; the cancellation owner controls correlated Stop/grace/tree-kill/drain; and the classifier/finalizer reconciles complete evidence exactly once. Apply must re-read the actual prerequisite source names and signatures after those blocks land.

## Goals / Non-Goals

**Goals:**
- Prove the existing Dashboard manual action through explicit internal `ChildWorker` selection without giving the UI backend knowledge.
- Preserve manual admission, prompt return, immediate pending, early Stop safety, ProcessingState/log compatibility, and safe retriggering.
- Carry one run identity through child startup, protocol, projection, cancellation, classification, cleanup, and coordinator release.
- Cover normal, empty, busy, startup, timeout, protocol, crash, cancellation, forced-kill, and cleanup outcomes with deterministic tests.
- Demonstrate that child selection resolves no in-process executor/geodata graph and never falls back, retries, or duplicates work.

**Non-Goals:**
- No scheduled trigger routing, scheduled eligibility detector, cron behavior, or empty-schedule gate; those remain later work.
- No production-default change and no settings, environment, CLI, endpoint, Dashboard, or public mode selector.
- No new coordinator, request, protocol, launcher, bridge, cancellation, classifier, state, or terminal vocabulary parallel to prerequisites.
- No worker pipeline, advisory-lock, geometry, persistence, geodata, deployment-role, or public documentation redesign.
- No broad Razor component test framework merely to test a coordinator boundary already observable through state and fakes.

## Decisions

### 1. Keep Dashboard on one coordinator-facing manual surface

Use the finalized block-13 Dashboard-facing manual start and active-run cancellation contract. If the prerequisite implementation still leaves `Dashboard.razor` injecting `ProcessingBackgroundService`, replace only that control dependency with the exact coordinator-facing interface or factory alias already established by block 13; do not inject a launcher, backend selector, child session, bridge, or classifier. `RunNow()` keeps its current page-local pending guard and pre-trigger statistics refresh unless finalized block-13 tests prove a required compatibility adjustment. The processing admission decision remains authoritative and manual contention remains silent.

The accepted call returns once dispatch ownership exists, preserving prompt UI behavior. Stop calls the coordinator's active-run cancellation operation and is intentionally backend-neutral. Alternative: branch in Dashboard between `TriggerRunAsync` and child launch. Rejected because it duplicates admission/cancellation/state ownership and creates a second execution path. Alternative: await child terminal from the component. Rejected because it changes the established manual caller contract and couples circuit lifetime to worker lifetime.

### 2. Reuse the coordinator's accepted-run order without manual special cases

For an accepted manual request, preserve the block-13 order exactly:

1. win atomic local admission;
2. create and publish one immutable manual request, non-empty run ID, active handle, and live coordinator CTS;
3. call `ProcessingState.MarkPending()`;
4. arm the exact singleton reporter/state adapter for that request;
5. freeze `ChildWorker` selection on the handle, create one run scope, and lazily resolve only that keyed backend;
6. establish one guarded backend dispatch before returning Accepted.

Publishing the active cancellation owner before `MarkPending()` closes the early-Stop race while retaining immediate visible pending state. Rejected AlreadyRunning or Stopping calls create no identity, CTS, pending state, arm, scope, backend resolution, process, or run event. Duplicate manual contention adds no log. Alternative: allocate the run ID in Dashboard or after process start. Rejected because cancellation and correlation would have no authoritative identity during setup.

### 3. Exercise child selection through internal composition, not a runtime mode

Block 33's `TemporaryProcessingBackendSelection` (or finalized equivalent) remains internal and defaults production Web composition to `InProcess` in this numbered step. Add or use an authorized internal registration overload/test host builder that explicitly selects `ChildWorker` for block-34 manual-path tests and transition verification. It must use the same production registrations except for that internal value; no string parsing or configuration binding is introduced. Scheduled behavior is not asserted or changed by this setup.

The coordinator resolves one keyed scoped backend only after admission. Constructor-counting and fail-on-resolution fakes prove that child selection does not instantiate the in-process adapter, executor, processing resolver, or geodata dependencies, and busy rejection resolves neither backend. Alternative: change the production default now. Rejected because block 37 owns that transition. Alternative: add a per-trigger backend field. Rejected because selection belongs to composition and would alter the shared request/protocol contract.

### 4. Compose one child session around the exact request and reporter

The child backend forwards the coordinator's exact `ProcessingRunRequest`, run ID, armed reporter, and cancellation token to the finalized block-25/27/28/30 composition. It resolves the command, launches one process, starts stdout/stderr/exit observation immediately, waits for validated readiness, writes and flushes one execute request, and accepts typed events only for that run. Ready is transport-only; run-started claims correlation; eligibility invokes the existing state-start/reset projection; absolute progress, activities, and diagnostics flow through the existing state adapter; the terminal projection/finalization receipt owns the sole Dashboard terminal mutation.

Manual zero-work still launches the child and lets the worker perform authoritative eligibility; only scheduled work later receives a pre-launch detector. Existing no-work and summary semantics are projected from worker events rather than reproduced in Dashboard or coordinator code. The backend returns the normalized result corresponding to the committed finalization receipt; returning it never reports a second terminal.

Alternative: bypass the bridge and translate events in the manual trigger. Rejected because it duplicates validation/correlation and risks state mutation after finality. Alternative: run a Web-side manual count before launch. Rejected because it would diverge from the worker's authoritative pass and trespass on scheduled detector work.

### 5. Route Stop through the existing exact-session cancellation owner

The coordinator token is the sole cancellation intent. For a child backend it is translated to at most one correlated cancel command only after execute write/flush makes cancellation legal. Cancellation before that point is latched and handled by the same session owner rather than lost. Stop does not cancel only the launch wait and never releases the coordinator while the process may still execute or emit output.

The finalized single grace deadline, whole-tree containment, exit/stdout/stderr drainage, and disposal path applies unchanged. A committed Cancelled terminal is authoritative. Without one, exact-session Stop plus orderly cancellation evidence or accepted forced kill can synthesize Cancelled; forced kill adds one bounded warning. Raw exit 130, EOF, or kill alone does not prove cancellation, and kill rejection remains a failed/unsettled cleanup condition.

### 6. Preserve classifier precedence and one finalization gate for every outcome

Use block 30's immutable evidence snapshot and pure classifier only after a typed no-process failure or complete process/stdout/stderr finality. The existing linearizable finalization gate is shared by normal terminal projection and abnormal classification. Cover these classes without manual-specific mappings:

- valid Completed with work and valid Completed with zero eligibility;
- valid Failed, including reserved busy code 3, with existing failed/fatal UI semantics and no retry;
- command resolution and OS-start failure;
- readiness timeout, pre-ready EOF/exit, ready callback rejection, and execute write/flush failure;
- malformed, oversized, unknown/incompatible, sequence/correlation/lifecycle/activity, sink, and projection faults;
- post-ready crash, missing terminal, raw mapped-looking exit, unmapped exit, and output/disposal anomaly;
- cooperative Cancelled, forced termination after exact Stop, and kill failure.

A committed valid terminal remains authoritative; later contradictions add at most one bounded safe anomaly. Without a committed terminal, the classifier synthesizes one Failed or authorized Cancelled projection. Diagnostics use typed categories and safe lifecycle phases; arbitrary stderr, request payloads, arguments, secrets, and stack traces do not enter `LastError` or UI logs.

Finalization order remains: freeze evidence, classify, win or observe the terminal receipt, close activities and callback acceptance, append compatible terminal diagnostic/summary once, settle launcher/cancellation/process/stream/run-scope cleanup, then release only the matching coordinator handle. No generic `finally` may release admission before state and child finality. Late or stale cleanup cannot detach a replacement run.

### 7. Verify orchestration with deterministic unit tests and reuse fixture modes

Add focused manual orchestration tests at the Dashboard-facing coordinator boundary using gates, completion sources, constructor-counting fakes, and a recording `ProcessingState`/adapter. They assert request/run/token identity, accepted ordering, prompt return, early Stop, exact selection, duplicate rejection, event projection, terminal receipt, cleanup order, and retrigger. They do not load PostgreSQL, SQLite, Overture, GADM, real cron delays, or wall-clock sleeps.

Reuse the finalized block-26 fixture and block-30 matrix instead of creating another worker executable or protocol dialect. Add only a thin manual-coordinator integration layer that selects `ChildWorker`, invokes the Dashboard-facing API, and observes state/ownership around existing fixture modes: success, no-work, busy/failed terminal, pre-ready crash, post-ready crash, malformed, oversized, unknown/incompatible, invalid sequence, ready timeout where covered by a deterministic seam, terminal/exit mismatch, stderr flood/redaction, cooperative cancellation, and unresponsive forced termination. Every real-process test must positively handshake, drain both streams, reap the process tree in cleanup, and avoid sleeps/polling.

A component test is required only if coordinator-boundary coverage cannot prove that Run and Stop use the backend-neutral interface and existing `OnChanged` refresh behavior. Prefer a narrow compile-time/binding test over adding a broad UI harness.

## Risks / Trade-offs

- [Prerequisite source APIs are absent or differ from planning names] → Apply prerequisites first and bind to their exact finalized interfaces, lifetimes, receipts, and fixture modes; do not recreate guessed types in block 34.
- [A global temporary selector could accidentally alter scheduled runs] → Keep the production default unchanged and scope explicit `ChildWorker` selection to internal manual transition/test composition; make no scheduler calls or assertions here.
- [Dashboard's pre-trigger statistics load obscures admission timing] → Keep it page-local and test coordinator admission separately; do not treat it as worker eligibility or let it create run state.
- [Terminal projection precedes transport cleanup] → Retain coordinator ownership until complete session evidence, process/stream cleanup, run-scope disposal, and matching release finish.
- [Failure repair double-mutates ProcessingState] → Use the prerequisite finalization receipt and abnormal-finality surface; never replay a terminal or append a second summary.
- [Child failure appears to justify fallback] → Assert zero in-process resolutions/effects for every child failure class and keep failure on the original run.
- [Fixture tests leak processes or become timing-sensitive] → Use existing positive handshakes, fake time/seams, explicit EOF/exit gates, unconditional tree reaping, and bounded waits only at the test harness safety boundary.

## Migration Plan

1. Confirm blocks 13, 25, 27, 28, 30, and 33 are applied; re-read the actual coordinator manual API, selection registration, keyed backend, launcher/session, bridge receipt, cancellation owner, classifier/finalizer, DI lifetimes, and fixture modes.
2. Add the internal child-selected manual test/transition composition and fail-on-resolution fakes without changing the production default or any public configuration surface.
3. Bind the Dashboard Run and Stop actions to the finalized backend-neutral coordinator surface if block 13 has not already completed that routing; leave rendering and `ProcessingState.OnChanged` behavior unchanged.
4. Complete only missing child-backend orchestration needed to connect the finalized launcher, bridge, cancellation, and finalizer contracts; remove no prerequisite ownership and add no alternate path.
5. Add deterministic manual admission/lifecycle tests, then the thin block-26 fixture integration matrix; verify no fallback, no duplicate dispatch, no stale cleanup, and retrigger after every terminal class.
6. Run focused tests, the default suite, process-fixture coverage, strict OpenSpec validation, status review, and a scope diff proving no scheduled/public-mode changes.
7. Roll back by reverting this numbered change's manual child-selected composition/tests/routing only. Do not add runtime fallback; the block-33 production default remains the rollback path until its planned removal sequence.

## Audit Reconciliation

Block 26 is a prerequisite for deterministic real-worker fixture coverage. The manual request uses one exact `Guid` identity whose canonical wire representation is preserved unchanged through child launch, events, bridge, cancellation, and finality. It consumes the internal exact 10-second `TimeProvider` cancellation policy without adding a public setting. UI `Processed` is projected from `UpdatedCount`, never aggregate `ProcessedCount`.

