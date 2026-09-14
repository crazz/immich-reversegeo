## Purpose

Keeps coordinate Lookup responsive and fully diagnostic while all geodata work runs in an isolated, correlated worker job rather than the Web control plane.

## ADDED Requirements

### Requirement: Responsive local validation
The WebUI SHALL parse and validate coordinate input before requesting worker admission, and SHALL reject non-finite values, latitude outside -90 through 90, or longitude outside -180 through 180 without launching work or discarding the last completed result.

#### Scenario: Coordinate is outside the valid range
- **WHEN** a user submits an out-of-range latitude or longitude
- **THEN** the page identifies the invalid field and accepted range, remains interactive, and does not request admission or launch a worker

#### Scenario: Coordinate is valid
- **WHEN** a user submits valid coordinates and lookup options
- **THEN** the page captures one immutable request snapshot before seeking worker admission

### Requirement: Isolated lookup execution
For each admitted Lookup, the WebUI SHALL launch exactly one typed v2 coordinate-lookup worker job and SHALL NOT resolve geodata, ensure/query geodata caches, or perform live place lookup inside the Web process.

#### Scenario: Lookup is admitted in Standard mode
- **WHEN** a valid Lookup is submitted in Standard mode while the worker resource is available
- **THEN** the WebUI runs the Lookup through the isolated worker and does not use an in-Web resolver fallback

#### Scenario: Lookup is admitted in Web-only mode
- **WHEN** a valid Lookup is submitted in Web-only mode while the worker resource is available
- **THEN** the WebUI provides the same worker-backed Lookup behavior and result semantics as Standard mode

#### Scenario: Worker launch is unavailable
- **WHEN** the Web host cannot launch the coordinate-lookup worker
- **THEN** the page displays a safe actionable failure, restores its controls, and does not attempt in-process resolution

### Requirement: Explicit lookup lifecycle presentation
The WebUI SHALL distinguish admission, worker startup, active discrete progress/activity, cancellation requested, completion, cancellation, busy rejection, and failure; SHALL present the coordinate-lookup worker's closed source/final-selection steps without inventing percentages; and SHALL treat the terminal or controller-classified outcome rather than progress or log text as authoritative.

#### Scenario: Worker starts and reports activity
- **WHEN** an admitted worker starts and emits correlated progress or scoped activity
- **THEN** the page shows a bounded current step or activity and remains in an active state

#### Scenario: Worker completes successfully
- **WHEN** the worker returns a correlated completed terminal with a typed lookup result
- **THEN** the page renders typed disabled/skipped/ready/no-match/unavailable/failed source states, cache readiness, release/version, best matches and bounded candidates, trace/profile summary, final per-field source attribution, and GADM attribution/license metadata using the existing Lookup result sections

#### Scenario: Source fails within a completed lookup
- **WHEN** a requested source failure is represented as a safe diagnostic in an otherwise completed result
- **THEN** the page identifies that source failure, retains independently resolved fields, and does not relabel the whole job as failed

#### Scenario: Worker fails before a valid terminal
- **WHEN** startup, crash, protocol, transport, forced-stop, or missing-terminal handling produces a controller-classified failure
- **THEN** the page shows the bounded safe classified outcome and does not fabricate or display a successful result

### Requirement: Deterministic control enablement
The WebUI SHALL prevent overlapping submissions from one page operation by disabling coordinate, paste, option, and Lookup controls while admission or worker work is active, while exposing one cancellation action for an admitted cancellable job.

#### Scenario: Lookup becomes active
- **WHEN** a valid attempt enters admission
- **THEN** mutable form controls and the Lookup action are disabled until the attempt is rejected or its admitted session reaches final cleanup

#### Scenario: Cancellation is requested
- **WHEN** the user selects Cancel for an active Lookup
- **THEN** the page changes to a cancelling state, disables repeated cancellation, and keeps other controls disabled until authoritative completion

#### Scenario: Attempt reaches final cleanup
- **WHEN** the attempt is busy-rejected, fails to launch, or its admitted session finishes and drains
- **THEN** the page restores the appropriate controls and permits a later valid Lookup

### Requirement: Fail-fast admission and reusable coordination boundary
The WebUI SHALL fail fast when the exclusive heavy-worker resource is owned, SHALL not queue or launch the rejected Lookup, and SHALL present a friendly active-job label when safely available without depending on a particular coordinator implementation.

#### Scenario: Another heavy job owns the resource
- **WHEN** a valid Lookup requests admission while the worker resource is busy
- **THEN** no worker is started, no in-Web geodata service is initialized, and the page tells the user which friendly job category is active or safely says another background job is active

#### Scenario: Busy owner later releases
- **WHEN** the active owner completes, fails, crashes, is cancelled, or is disposed and releases the resource
- **THEN** a later valid Lookup can be admitted without restarting the Web host

### Requirement: Correlated events and stale-operation safety
The WebUI SHALL apply worker events and outcomes only to the active coordinate-lookup identity and page operation, and SHALL ignore stale UI callbacks after replacement or disposal.

#### Scenario: Event identity or kind does not match
- **WHEN** an event does not match the active protocol version, coordinate-lookup kind, or job identity
- **THEN** it is not displayed as lookup progress or a result and the session follows protocol-failure classification

#### Scenario: Late callback arrives after a newer operation
- **WHEN** a callback from a completed, cancelled, or disposed attempt arrives after another operation owns the page state
- **THEN** the callback does not change the newer operation's status, controls, error, diagnostics, or result

### Requirement: Cancellation and circuit disposal
The WebUI SHALL target cancellation to the exact active worker job and SHALL stop and asynchronously dispose an owned active session when the user navigates away or the circuit is disposed.

#### Scenario: User cancels an active lookup
- **WHEN** the user requests cancellation
- **THEN** the request reaches the active worker cancellation contract and the page ultimately displays the authoritative completed, cancelled, or classified failure outcome from the cancellation race

#### Scenario: Circuit is disposed during lookup
- **WHEN** the page or circuit is disposed while a Lookup owns a worker session
- **THEN** the WebUI suppresses later rendering, joins the bounded stop/disposal path, and releases admission exactly once without leaving an intentionally running orphan

### Requirement: Lookup remains separate from processing and asset mutation
A worker-backed Lookup SHALL remain a read-only preview, SHALL NOT write Immich asset metadata, and SHALL NOT add lookup lifecycle, activity, logs, counts, or terminal outcomes to processing-run state.

#### Scenario: Lookup completes
- **WHEN** a Lookup succeeds, partially resolves, fails, or is cancelled
- **THEN** no asset or asset metadata row is changed and no processing-run state transition is emitted for that Lookup

### Requirement: GADM restriction and source errors remain visible
The Lookup page SHALL identify GADM as experimental and limited to academic and other non-commercial use at the option/result decision point, SHALL use the worker result's stable dataset/version and official license URL metadata when available, and SHALL present safe GADM availability/download/query errors separately from the licensing restriction.

#### Scenario: User enables GADM
- **WHEN** the GADM option is selected
- **THEN** the page keeps the non-commercial-use notice visible while the attempt is configured or active

#### Scenario: GADM source is unavailable
- **WHEN** a completed lookup reports a safe GADM source error
- **THEN** the GADM result area says that GADM data is unavailable, preserves usable Overture results, and does not state or imply that the technical error was caused by the license restriction
