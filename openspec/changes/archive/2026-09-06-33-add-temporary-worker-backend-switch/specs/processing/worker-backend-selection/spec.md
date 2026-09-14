## Purpose

Provides a controlled, internal transition between processing backends while preserving one observable processing lifecycle and preventing effects from the backend that was not selected.

## ADDED Requirements

### Requirement: Temporary internal backend selection
The system SHALL support exactly two temporary processing backend values, in-process and child-worker, at the processing coordinator dispatch boundary. In block 33 the selection SHALL default to in-process, an authorized internal composition or test seam SHALL be able to select child-worker explicitly, and the selection SHALL NOT be exposed through AppConfig, persisted settings, environment or command-line deployment modes, public application APIs, or the UI.

#### Scenario: Initial default selection
- **WHEN** the Web composition supplies no explicit temporary backend value in block 33
- **THEN** an admitted processing request selects the in-process backend

#### Scenario: Explicit child selection
- **WHEN** an internal transition composition or focused test explicitly selects child-worker
- **THEN** an admitted processing request selects the child-worker backend without changing its request contract

#### Scenario: Unsupported internal value
- **WHEN** composition is attempted with a backend value other than in-process or child-worker
- **THEN** composition fails deterministically before any processing request is admitted or ProcessingState is mutated

### Requirement: One backend dispatch per admitted run
For each admitted request, the coordinator SHALL invoke exactly one selected backend with the same immutable ProcessingRunRequest and run ID, exact armed event reporter, coordinator-owned cancellation token, active arbitration handle, and ProcessingRunResult semantics established by the coordinator and executor contracts. It SHALL NOT execute both backends, switch backend after admission, fall back to the other backend after any failure, or automatically retry the run.

#### Scenario: Selected backend succeeds
- **WHEN** the selected backend accepts and completes an admitted request
- **THEN** only that backend executes and its terminal result is the result observed through the shared coordinator lifecycle

#### Scenario: Selected child backend cannot start
- **WHEN** child-worker is selected and child startup fails
- **THEN** the same run is finalized as the classified failure without invoking the in-process backend or starting a replacement child

#### Scenario: Duplicate trigger during selected execution
- **WHEN** another manual or scheduled trigger arrives while either selected backend owns the active coordinator handle
- **THEN** the trigger is rejected without selecting, resolving, or invoking another backend

### Requirement: Backend-independent lifecycle and cancellation
The coordinator SHALL retain its existing admission, pending-state, reporter arming, active/stopping ownership, cancellation, shutdown, terminal cleanup, and exact-handle release behavior for both backends. In-process cancellation SHALL remain cooperative through the coordinator token. Child-worker cancellation SHALL translate that same coordinator cancellation intent into the exact-session child cancellation, grace, containment, drainage, and classification path, and SHALL not treat wait cancellation, an exit number, or process termination alone as a terminal result.

#### Scenario: In-process cancellation
- **WHEN** cancellation is requested for an admitted in-process run
- **THEN** the coordinator enters the shared stopping lifecycle and the executor returns its cancellation result through the same reporter and result contract

#### Scenario: Child-session cancellation
- **WHEN** cancellation is requested for an admitted child-worker run
- **THEN** the coordinator remains owner until exact-session cancellation and complete child finality produce one normalized terminal result

#### Scenario: Child terminal and transport finality differ in time
- **WHEN** a valid child terminal is projected before process and stream finality
- **THEN** the terminal remains authoritative while coordinator ownership is released only after the child backend finishes classification and cleanup

### Requirement: Lazy selected-backend activation
The coordinator composition SHALL resolve and instantiate only the backend selected for an admitted run. Selecting in-process SHALL cause no child command, launcher, bridge, classifier-session, or process-start effect. Selecting child-worker SHALL cause no in-process executor or processing geodata service construction or access through the backend dispatch path.

#### Scenario: In-process selected
- **WHEN** an admitted request selects in-process execution
- **THEN** no child backend service is instantiated and no child process effect occurs

#### Scenario: Child-worker selected
- **WHEN** an admitted request selects child-worker execution
- **THEN** no in-process backend or processing geodata dependency is instantiated or accessed by the Web dispatch path

#### Scenario: Busy request rejected
- **WHEN** a request is rejected by process-local arbitration before dispatch
- **THEN** neither backend is resolved or instantiated

### Requirement: Transition has a fixed removal sequence
The temporary selector SHALL be used only for the Phase 5 sequence: block 34 exercises manual child execution through explicit child selection; block 35 exercises eligible scheduled child execution through explicit child selection; block 36 proves an empty scheduled pass resolves neither child nor geodata execution; block 37 changes the internal production default to child-worker while retaining only an explicit temporary in-process fallback; and block 38 removes the production selector, in-process backend registration, and fallback path.

#### Scenario: Transition reaches block 38
- **WHEN** blocks 34 through 37 have passed their required control-plane and process integration coverage
- **THEN** block 38 can delete the temporary production selection rather than preserving it as a deployment mode

## Audit Reconciliation

This change has applied blocks 29, 31, and 32 as prerequisites in addition to its existing prerequisites. The child backend consumes launcher/session/bridge/classifier finalization only; it is never a producer/reporter, never emits lifecycle/progress/log/activity/terminal events, and never reports a second terminal. It returns only the finalized receipt/result of the authoritative child path.

