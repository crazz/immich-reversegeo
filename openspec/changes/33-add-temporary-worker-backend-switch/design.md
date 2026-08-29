## Context

Blocks 11 and 13 establish `ProcessingRunExecutor.ExecuteAsync(ProcessingRunRequest, IProcessingEventReporter, CancellationToken) -> Task<ProcessingRunResult>` behind one singleton coordinator that owns admission, the exact active handle and CTS, `MarkPending`, reporter arming, cancellation, shutdown, and identity-checked cleanup. Blocks 25, 27, 28, and 30 establish a child session whose typed events are projected through a run-scoped bridge, whose exact-session cancellation is controlled through one grace/containment owner, and whose complete launch/process/stream/control evidence is classified and finalized once. See `proposal.md` for motivation and `specs/processing/worker-backend-selection/spec.md` for the required transition behavior.

The selector must not weaken those ownership lines. In particular, the executor does not launch processes, the launcher does not classify results, the bridge does not synthesize failures, and the classifier does not retry. The coordinator remains the only process-local admission and cancellation entry point.

## Goals / Non-Goals

**Goals:**
- Put one removable selection seam immediately before coordinator dispatch.
- Preserve the request, run identity, reporter, result, state, arbitration, cancellation, and cleanup contracts across both implementations.
- Ensure DI does not eagerly build the unselected backend or its dependency graph.
- Make the initial default, invalid-value behavior, test seam, lifetimes, coverage matrix, and blocks 34–38 removal sequence explicit.

**Non-Goals:**
- No user-selectable deployment mode, AppConfig/settings field, environment variable, command-line option, UI, or public API.
- No scheduler eligibility redesign, Dashboard rewrite, worker protocol change, result-classification change, retry/fallback policy, geodata change, or block-32 work.
- No permanent strategy framework beyond what block 38 needs to remove.

## Decisions

### 1. Use one internal enum value registered by internal composition code

Define an internal two-value enum, named `ProcessingBackendKind` in this plan, with only `InProcess` and `ChildWorker`. Store one value in an internal immutable `TemporaryProcessingBackendSelection` singleton. The Web control-plane registration method accepts the value through an internal overload/defaulted parameter; block 33 calls the default `InProcess`, while focused tests and blocks 34–35 can register `ChildWorker` explicitly. The test assembly may use the repository's existing internal-visibility convention rather than promoting this to a public option.

The registration method validates the enum with an exhaustive switch and throws `ArgumentOutOfRangeException` for any undefined cast before building or starting the host. The dispatcher uses another exhaustive switch as a defensive invariant. There is no string parser and no binding from `IConfiguration`, `AppConfig`, settings JSON, environment variables, command-line arguments, endpoints, or Razor components.

Alternative: add an options/configuration key. Rejected because it would accidentally create the Phase 6 deployment-mode surface and require compatibility, validation, documentation, and persistence for a mechanism deleted in block 38. Alternative: put a backend field on `ProcessingRunRequest`. Rejected because it would change the shared request/protocol contract and let callers rather than composition select execution.

### 2. Adapt both paths to one internal backend contract without changing owned contracts

Add a narrow internal `IProcessingRunBackend` with the same dispatch shape as the executor:

`Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)`.

The in-process adapter forwards those exact arguments once to the block-11 singleton executor. The child adapter uses the same request and run ID to create one block-25 session, binds one block-27 bridge to the exact reporter/state adapter already armed by the coordinator, and lets block 30 classify/finalize complete session evidence. It returns the `ProcessingRunResult` corresponding to the already-authoritative committed terminal; returning the value does not report a second terminal.

The coordinator retains its existing admitted order: publish the exact handle/request/CTS, mark pending, arm the exact reporter, select and resolve one backend, and dispatch once. Rejected triggers do not select or resolve a backend. The coordinator never invents a backend-specific result, and backend completion cannot release a nonmatching handle.

Alternative: let `ProcessingBackgroundService`, Dashboard, or scheduler branch between executor and launcher. Rejected because it duplicates arbitration/state behavior and lets manual and scheduled paths diverge. Alternative: make the launcher itself implement the backend contract. Rejected because launcher completion intentionally contains raw evidence and does not own bridge projection, classification, or normalized results.

### 3. Resolve one keyed scoped adapter lazily per admitted run

Register `IProcessingRunBackend` twice as internal keyed scoped services keyed by `ProcessingBackendKind`. The singleton coordinator injects only the selection singleton and `IServiceScopeFactory`; it MUST NOT inject both adapters, `IEnumerable<IProcessingRunBackend>`, the executor, launcher, or geodata services. After admission and immediately before dispatch it creates one run scope, resolves only the selected key, and keeps that scope on the exact active handle until terminal cleanup.

Lifetimes are:
- coordinator, `ProcessingState`, the exact block-9 reporter/state adapter, scheduler/host aliases, selection value, and stateless resolver policy: singleton;
- block-11 executor and its already-established stateless collaborator aliases: unchanged singleton registrations;
- in-process and child backend adapters: keyed scoped, one selected adapter per admitted run;
- bridge, child session evidence/finalization state, and other run-owned child objects: scoped or explicitly created per run and disposed by that run;
- launcher/codec/command-builder/classifier services that are stateless: retain their prerequisite lifetimes, but no child adapter or session is resolved when in-process is selected.

The coordinator disposes the run scope only after backend terminal/finality cleanup and before releasing the matching handle. On child selection, the in-process adapter and executor/geodata graph are not resolved through this path. On in-process selection, the child adapter, command builder, launcher, bridge, classifier session, and process are not resolved. This is stronger than merely avoiding `Process.Start`; constructor side effects from the unselected graph are absent.

Alternative: inject two singleton implementations and choose one. Rejected because Microsoft DI would construct both graphs when the coordinator is built. Alternative: resolve transient/disposable adapters from the root provider. Rejected because run-owned objects would have unclear disposal and could be retained for the host lifetime.

### 4. Normalize cancellation and completion at the backend boundary

The coordinator owns one CTS and one active/stopping transition for either backend. The in-process adapter passes the token directly to the executor, preserving cooperative cancellation and prior committed persistence effects. The child adapter observes that token as exact-session cancellation intent and delegates to the block-28/30 control owner: send at most one cancel command when legal, apply the one grace deadline and whole-tree containment policy, continue exit/stdout/stderr drainage, freeze complete evidence, and return only after classification/finalization and cleanup settle.

Cancelling a wait is not cancelling the run. Raw exit 130, EOF, or a kill does not independently authorize `Cancelled`; the exact-session control intent and classifier precedence remain authoritative. A valid terminal committed by the bridge stays authoritative even if transport finality follows later. The coordinator may expose the already-planned manual-dispatch and scheduled-await shapes, but both observe the same active handle and normalized terminal result.

Alternative: cancel the launcher wait and release the coordinator immediately. Rejected because the child may still own the advisory lock, emit events, or mutate persistence. Alternative: map exit codes in the coordinator. Rejected because block 30 owns evidence classification and raw numeric exits are not domain authority.

### 5. Never fall back, race, or retry

Selection is frozen onto the exact admitted handle before backend resolution. A resolution, child start, protocol, projection, cancellation, crash, or executor failure stays with that backend and that run. Child start failure is classified/finalized as Failed; it never invokes the in-process executor. No path starts two backends, launches a replacement child, replays stdout, retries projection, or resubmits the request.

Alternative: use in-process as a resilience fallback while child integration matures. Rejected because a failed child may have accepted or committed work, making fallback duplicate execution and violating the one-run contract.

## Risks / Trade-offs

- [Applied prerequisite APIs or names differ from the planning names] → Reconcile against the completed block-11/13/25/27/28/30 types during apply, preserving the ownership and lifetime decisions above rather than adding parallel abstractions.
- [Keyed DI accidentally resolves both graphs] → Add constructor-counting/fail-on-resolution fakes and composition tests; prohibit enumerable injection and root-provider resolution.
- [Child terminal projection and returned result double-finalize state] → Require the bridge/classifier finalization gate to remain the sole terminal writer; the backend return value only mirrors its receipt.
- [Scope disposal races late callbacks] → Close callback acceptance and child/session cleanup before disposing the run scope, then release only the matching coordinator handle.
- [The temporary seam becomes permanent] → Keep every selector type and registration internal, name it temporary, and make block 38 deletion a tested migration step.

## Migration Plan

1. **Block 33:** add the internal enum/selection singleton, keyed lazy adapters, and coordinator dispatch seam. Production defaults to `InProcess`; tests explicitly exercise both values and invalid-value rejection.
2. **Block 34:** exercise manual requests with explicit `ChildWorker` selection through the same Dashboard-facing coordinator API; do not add a caller-visible switch.
3. **Block 35:** exercise eligible scheduled requests with explicit `ChildWorker` selection through the same scheduler/coordinator API and pre-launch detector.
4. **Block 36:** lock down the empty scheduled path: one detector call, no backend resolution, no launcher/session/protocol events, and no in-process/geodata construction.
5. **Block 37:** change only the internal production default to `ChildWorker`; retain explicit `InProcess` solely for short-lived transition and parity tests.
6. **Block 38:** remove `ProcessingBackendKind`, `TemporaryProcessingBackendSelection`, keyed production selection, and the production in-process adapter/registration. Register the child backend directly at the coordinator seam; keep control-plane fakes outside production DI.

Rollback before block 38 reverts the current numbered step and its internal registration value. It does not add fallback-on-failure, public configuration, or automatic retry. After block 38, rollback is a source revert, not a runtime mode.

## Audit Reconciliation

This change has applied blocks 29, 31, and 32 as prerequisites in addition to its existing prerequisites. The child backend consumes launcher/session/bridge/classifier finalization only; it is never a producer/reporter, never emits lifecycle/progress/log/activity/terminal events, and never reports a second terminal. It returns only the finalized receipt/result of the authoritative child path.

