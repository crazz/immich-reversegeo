## 1. Reconcile prerequisite contracts

- [x] 1.1 Re-read the applied block-11 executor and block-13 coordinator APIs and preserve the exact `ProcessingRunRequest`, run ID, `IProcessingEventReporter`, `CancellationToken`, `ProcessingRunResult`, singleton coordinator, admission, pending, and handle-cleanup contracts.
- [x] 1.2 Re-read the applied block-25 launcher/session, block-27 bridge, block-28 cancellation owner, and block-30 classifier/finalizer APIs; adapt their actual names rather than introducing duplicate launch, projection, cancellation, or classification owners.
- [x] 1.3 Confirm block 33 consumes the external Phase 4 prerequisites without editing block-32 protocol, scenario, spec, or AppHost behavior, changing protocol/result semantics, or moving admission and state ownership out of the coordinator. The necessary `ProcessCoordinatorFixture.cs` constructor migration consumes the block-33 coordinator API only.

## 2. Add the internal temporary selection

- [x] 2.1 Add internal `ProcessingBackendKind` values `InProcess` and `ChildWorker` plus an immutable internal `TemporaryProcessingBackendSelection` singleton; default the block-33 Web registration to `InProcess`.
- [x] 2.2 Add an internal composition/test overload that accepts the enum directly, validate it exhaustively at registration, and reject undefined casts with `ArgumentOutOfRangeException` before host start or run admission.
- [x] 2.3 Verify no selector binding or representation is added to AppConfig, settings JSON, IConfiguration/environment/command-line deployment modes, public service APIs, endpoints, Dashboard/Settings UI, or the worker request/protocol.

## 3. Adapt and lazily resolve one backend

- [x] 3.1 Add the internal `IProcessingRunBackend.ExecuteAsync(ProcessingRunRequest, IProcessingEventReporter, CancellationToken)` contract returning `Task<ProcessingRunResult>`.
- [x] 3.2 Add a keyed scoped in-process adapter that forwards the exact arguments once to the existing singleton executor without changing cancellation, persistence, reporting, or result semantics.
- [x] 3.3 Add a keyed scoped child adapter that composes the existing launcher, exact-run bridge, cancellation/control owner, classifier, and finalization gate; return the result matching the authoritative committed terminal without reporting it twice.
- [x] 3.4 Change only the singleton coordinator dispatch seam to freeze the selected enum on the admitted handle, create one run scope, resolve one keyed backend, and dispatch once after the existing publish/MarkPending/reporter-arm sequence.
- [x] 3.5 Keep the run scope and selected backend owned until terminal/finality cleanup; settle run-owned child objects, asynchronously detach the scheduled cancellation registration, dispose the CTS and scope, and only then release the exact matching coordinator handle.
- [x] 3.6 Preserve singleton identity for the coordinator, ProcessingState/reporter adapter, ProcessingBackgroundService concrete/hosted aliases, selection value, and existing singleton executor collaborators; do not inject both backends or `IEnumerable<IProcessingRunBackend>` into a singleton.

## 4. Normalize execution, cancellation, and failure

- [x] 4.1 Pass the coordinator CTS directly to in-process execution. For an admitted scheduled child handle, register only that exact CTS before publication; its callback captures the handle and reuses `ClaimStop`/attached termination. Manual Stop and shutdown retain their existing ownership, and the child adapter adds no token observer or cancellation registration.
- [x] 4.2 Keep child coordinator ownership through cancel command/grace/containment, process exit, stdout/stderr finality, classification, terminal/activity cleanup, and session disposal; preserve a previously committed bridge terminal as authoritative.
- [x] 4.3 Map typed child startup and abnormal completion through the existing classifier/finalizer into the shared result/state lifecycle; do not classify raw exit, EOF, stderr, or kill evidence in the coordinator.
- [x] 4.4 Ensure every admitted run stays on its frozen backend after resolution, start, protocol, projection, executor, cancellation, or cleanup failure: no other-backend fallback, parallel backend, replacement child, automatic retry, stdout replay, projection retry, or request resubmission.

## 5. Add focused selection and DI tests

- [x] 5.1 Test the internal-selection matrix: omitted/default value selects in-process; explicit child value selects child-worker; every undefined enum cast fails composition before host start, admission, run-ID creation, pending state, reporter arming, or backend resolution.
- [x] 5.2 For each backend, test that one accepted manual-shaped request reaches exactly one adapter with the same request object/run ID, exact reporter instance, coordinator token, and matching success result, while the coordinator follows the same pending/active/idle lifecycle.
- [x] 5.3 For each backend, test duplicate manual and scheduled-shaped triggers while active; assert rejection creates no new ID/CTS/pending/reporting/scope and resolves neither backend again.
- [x] 5.4 Test lazy isolation with constructor-counting and fail-on-resolution fakes: in-process selection constructs no child adapter/command/launcher/bridge/classifier session, child selection constructs no in-process adapter/executor/geodata dependency, and busy rejection constructs neither.
- [x] 5.5 Test DI lifetime and cleanup: all singleton aliases resolve to the same instances, one selected scoped adapter exists per admitted run, a later run gets a new scope, and no scope is disposed before terminal/finality cleanup or retained after exact-handle release.

## 6. Add lifecycle and no-fallback tests

- [x] 6.1 Run the backend parity matrix for Completed, Cancelled, and Failed results; assert one terminal state mutation, matching result semantics, closed activities, and return to idle for both adapters.
- [x] 6.2 Test in-process cooperative cancellation and child exact-session cancellation separately; for child, cover cancel-before-ready/accepted behavior available from prerequisites, grace/containment, complete drainage, and ownership retention until normalized finality.
- [x] 6.3 Test a valid child terminal arriving before process/stream finality; assert it remains authoritative, no second terminal is reported, and coordinator/scope ownership is released only after final evidence and cleanup settle.
- [x] 6.4 Test child resolution/start failure, protocol/projection failure, crash/missing terminal, and cancellation containment failure with deterministic prerequisite fakes; assert one classified result and zero in-process calls, replacement launches, retries, or premature handle release.
- [x] 6.5 Test an in-process executor failure through its existing contract; assert zero child resolution/launch/fallback and identity-checked coordinator cleanup without inventing new executor classification semantics.

## 7. Protect the bounded transition and verify

- [x] 7.1 Add removal-oriented assertions/comments at the internal registration seam documenting the sequence: block 34 manual explicit child, block 35 eligible scheduled explicit child, block 36 empty path resolves neither graph, block 37 internal default becomes child with explicit in-process fallback, and block 38 deletes selector/keyed production fallback.
- [x] 7.2 Run focused coordinator/backend/composition tests and the existing launcher/bridge/cancellation/classifier suites, then run `npm run test` and the relevant prerequisite process-fixture integration tests. The necessary `ProcessCoordinatorFixture.cs` constructor migration is limited to consuming the block-33 API; it changes no block-32 protocol, scenario, spec, or AppHost behavior.
- [x] 7.3 Run `openspec validate 33-add-temporary-worker-backend-switch --strict` and `openspec status --change 33-add-temporary-worker-backend-switch`; audit the block-33 scope and confirm no public configuration or UI surface was introduced.

## Audit Reconciliation

This change has applied blocks 29, 31, and 32 as prerequisites in addition to its existing prerequisites. The child backend consumes launcher/session/bridge/classifier finalization only; it is never a producer/reporter, never emits lifecycle/progress/log/activity/terminal events, and never reports a second terminal. It returns only the finalized receipt/result of the authoritative child path.

## Validation

- Focused Change-33 matrix: 27/27 passed (`_out/change33-final-focused-tests.log`).
- Control-plane checkpoint: 457/457 passed (`_out/change33-control-plane-checkpoint-tests.log`).
- Default suite: 1509/1509 passed (`_out/change33-final-default-tests-fixed.log`).
- PostgreSQL process matrix: 9/9 passed (`_out/change33-postgres-process-matrix.log`).
- `openspec validate 33-add-temporary-worker-backend-switch --strict` passed; status reports planning complete.
