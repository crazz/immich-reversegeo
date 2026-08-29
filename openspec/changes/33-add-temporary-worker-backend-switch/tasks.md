## 1. Reconcile prerequisite contracts

- [ ] 1.1 Re-read the applied block-11 executor and block-13 coordinator APIs and preserve the exact `ProcessingRunRequest`, run ID, `IProcessingEventReporter`, `CancellationToken`, `ProcessingRunResult`, singleton coordinator, admission, pending, and handle-cleanup contracts.
- [ ] 1.2 Re-read the applied block-25 launcher/session, block-27 bridge, block-28 cancellation owner, and block-30 classifier/finalizer APIs; adapt their actual names rather than introducing duplicate launch, projection, cancellation, or classification owners.
- [ ] 1.3 Confirm block 33 consumes the external Phase 4 prerequisites without editing block 32, changing protocol/result semantics, or moving admission and state ownership out of the coordinator.

## 2. Add the internal temporary selection

- [ ] 2.1 Add internal `ProcessingBackendKind` values `InProcess` and `ChildWorker` plus an immutable internal `TemporaryProcessingBackendSelection` singleton; default the block-33 Web registration to `InProcess`.
- [ ] 2.2 Add an internal composition/test overload that accepts the enum directly, validate it exhaustively at registration, and reject undefined casts with `ArgumentOutOfRangeException` before host start or run admission.
- [ ] 2.3 Verify no selector binding or representation is added to AppConfig, settings JSON, IConfiguration/environment/command-line deployment modes, public service APIs, endpoints, Dashboard/Settings UI, or the worker request/protocol.

## 3. Adapt and lazily resolve one backend

- [ ] 3.1 Add the internal `IProcessingRunBackend.ExecuteAsync(ProcessingRunRequest, IProcessingEventReporter, CancellationToken)` contract returning `Task<ProcessingRunResult>`.
- [ ] 3.2 Add a keyed scoped in-process adapter that forwards the exact arguments once to the existing singleton executor without changing cancellation, persistence, reporting, or result semantics.
- [ ] 3.3 Add a keyed scoped child adapter that composes the existing launcher, exact-run bridge, cancellation/control owner, classifier, and finalization gate; return the result matching the authoritative committed terminal without reporting it twice.
- [ ] 3.4 Change only the singleton coordinator dispatch seam to freeze the selected enum on the admitted handle, create one run scope, resolve one keyed backend, and dispatch once after the existing publish/MarkPending/reporter-arm sequence.
- [ ] 3.5 Keep the run scope and selected backend owned until terminal/finality cleanup, close callbacks and dispose run-owned child objects, dispose the scope, and only then release the exact matching coordinator handle.
- [ ] 3.6 Preserve singleton identity for the coordinator, ProcessingState/reporter adapter, ProcessingBackgroundService concrete/hosted aliases, selection value, and existing singleton executor collaborators; do not inject both backends or `IEnumerable<IProcessingRunBackend>` into a singleton.

## 4. Normalize execution, cancellation, and failure

- [ ] 4.1 Pass the coordinator CTS directly to in-process execution and translate that same token into exact-session child cancellation intent without using wait cancellation as run cancellation.
- [ ] 4.2 Keep child coordinator ownership through cancel command/grace/containment, process exit, stdout/stderr finality, classification, terminal/activity cleanup, and session disposal; preserve a previously committed bridge terminal as authoritative.
- [ ] 4.3 Map typed child startup and abnormal completion through the existing classifier/finalizer into the shared result/state lifecycle; do not classify raw exit, EOF, stderr, or kill evidence in the coordinator.
- [ ] 4.4 Ensure every admitted run stays on its frozen backend after resolution, start, protocol, projection, executor, cancellation, or cleanup failure: no other-backend fallback, parallel backend, replacement child, automatic retry, stdout replay, projection retry, or request resubmission.

## 5. Add focused selection and DI tests

- [ ] 5.1 Test the internal-selection matrix: omitted/default value selects in-process; explicit child value selects child-worker; every undefined enum cast fails composition before host start, admission, run-ID creation, pending state, reporter arming, or backend resolution.
- [ ] 5.2 For each backend, test that one accepted manual-shaped request reaches exactly one adapter with the same request object/run ID, exact reporter instance, coordinator token, and matching success result, while the coordinator follows the same pending/active/idle lifecycle.
- [ ] 5.3 For each backend, test duplicate manual and scheduled-shaped triggers while active; assert rejection creates no new ID/CTS/pending/reporting/scope and resolves neither backend again.
- [ ] 5.4 Test lazy isolation with constructor-counting and fail-on-resolution fakes: in-process selection constructs no child adapter/command/launcher/bridge/classifier session, child selection constructs no in-process adapter/executor/geodata dependency, and busy rejection constructs neither.
- [ ] 5.5 Test DI lifetime and cleanup: all singleton aliases resolve to the same instances, one selected scoped adapter exists per admitted run, a later run gets a new scope, and no scope is disposed before terminal/finality cleanup or retained after exact-handle release.

## 6. Add lifecycle and no-fallback tests

- [ ] 6.1 Run the backend parity matrix for Completed, Cancelled, and Failed results; assert one terminal state mutation, matching result semantics, closed activities, and return to idle for both adapters.
- [ ] 6.2 Test in-process cooperative cancellation and child exact-session cancellation separately; for child, cover cancel-before-ready/accepted behavior available from prerequisites, grace/containment, complete drainage, and ownership retention until normalized finality.
- [ ] 6.3 Test a valid child terminal arriving before process/stream finality; assert it remains authoritative, no second terminal is reported, and coordinator/scope ownership is released only after final evidence and cleanup settle.
- [ ] 6.4 Test child resolution/start failure, protocol/projection failure, crash/missing terminal, and cancellation containment failure with deterministic prerequisite fakes; assert one classified result and zero in-process calls, replacement launches, retries, or premature handle release.
- [ ] 6.5 Test an in-process executor failure through its existing contract; assert zero child resolution/launch/fallback and identity-checked coordinator cleanup without inventing new executor classification semantics.

## 7. Protect the bounded transition and verify

- [ ] 7.1 Add removal-oriented assertions/comments at the internal registration seam documenting the sequence: block 34 manual explicit child, block 35 eligible scheduled explicit child, block 36 empty path resolves neither graph, block 37 internal default becomes child with explicit in-process fallback, and block 38 deletes selector/keyed production fallback.
- [ ] 7.2 Run focused coordinator/backend/composition tests and the existing launcher/bridge/cancellation/classifier suites, then run `npm run test` and the relevant prerequisite process-fixture integration tests without modifying block 32.
- [ ] 7.3 Run `openspec validate 33-add-temporary-worker-backend-switch --strict` and `openspec status --change 33-add-temporary-worker-backend-switch`; review scope to confirm only numbered block 33 in MASTERPLAN.md plus change-33 planning/implementation files changed and no public configuration or UI surface was introduced.

## Audit Reconciliation

This change has applied blocks 29, 31, and 32 as prerequisites in addition to its existing prerequisites. The child backend consumes launcher/session/bridge/classifier finalization only; it is never a producer/reporter, never emits lifecycle/progress/log/activity/terminal events, and never reports a second terminal. It returns only the finalized receipt/result of the authoritative child path.

