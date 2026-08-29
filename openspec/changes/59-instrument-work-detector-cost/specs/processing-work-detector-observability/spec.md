## Purpose

Processing work detector observability provides safe, low-cardinality evidence about scheduled detector outcome and elapsed cost without exposing database, asset, request, or connection data.

## ADDED Requirements

### Requirement: Every detector call has one terminal measurement
The system SHALL emit exactly one `EventId(5901, "ProcessingWorkDetectorCompleted")` structured terminal measurement for each scheduled work-detector invocation, whether it returns work, returns no work, is cancelled, or fails. The measurement SHALL classify the outcome as exactly `HasWork`, `NoWork`, `Cancelled`, or `Failed`. An exception SHALL be `Cancelled` if and only if the exact caller cancellation token is requested; every exception while that token is not requested, including database/command timeout and unmatched `OperationCanceledException`, SHALL be `Failed`. Instrumentation SHALL preserve the detector's original result, cancellation, or failure behavior and SHALL add no timeout.

#### Scenario: Detector reports work
- **WHEN** a detector invocation completes with `HasWork = true`
- **THEN** exactly one terminal measurement records outcome `HasWork` and the original result is returned unchanged

#### Scenario: Detector reports no work
- **WHEN** a detector invocation completes with `HasWork = false`
- **THEN** exactly one terminal measurement records outcome `NoWork` and the original result is returned unchanged

#### Scenario: Detector is cancelled
- **WHEN** a detector invocation throws and the exact caller cancellation token is requested
- **THEN** exactly one terminal measurement records outcome `Cancelled` and the cancellation is propagated rather than converted to no work

#### Scenario: Detector fails
- **WHEN** a detector invocation throws while the exact caller cancellation token is not requested
- **THEN** exactly one terminal measurement records outcome `Failed` and the failure is propagated unchanged

#### Scenario: Database command times out
- **WHEN** the detector reports a database or command timeout while the exact caller cancellation token is not requested
- **THEN** exactly one terminal measurement records outcome `Failed`, the original failure propagates, and no timeout behavior is added

### Requirement: Duration and event severity are deterministic
Each terminal measurement SHALL record elapsed monotonic duration in milliseconds. Successful and cancelled calls below 1000 milliseconds SHALL be emitted at Information; failed calls and calls lasting at least 1000 milliseconds SHALL emit that same single terminal measurement at Warning. Detector terminal measurements SHALL NOT be sampled.

#### Scenario: Detector completes below the threshold
- **WHEN** a successful detector call takes less than 1000 milliseconds
- **THEN** its one Information terminal measurement contains the measured duration

#### Scenario: Detector reaches the threshold
- **WHEN** a detector call takes exactly 1000 milliseconds
- **THEN** its one terminal measurement is emitted at Warning without an additional slow-operation event

#### Scenario: Concurrent detector calls finish out of order
- **WHEN** multiple detector calls overlap and complete in a different order than they started
- **THEN** each call has exactly one terminal measurement with its own outcome and elapsed duration

### Requirement: Detector dimensions are bounded and safe
The terminal measurement SHALL identify the scheduled trigger, logical purpose and coverage, and fallback use when a result exists. Its strategy SHALL be exactly the bounded literal `postgres-exists-v1`, and its database-operation family SHALL be exactly the bounded literal `eligibility-existence-probe` rather than query text. It SHALL NOT contain SQL, query plans, coordinates, asset/request/run IDs, cursor or work-set values, counts of eligible assets, database/host/user names, credentials, connection strings, parameter values, arbitrary metadata, exception objects, exception messages, or stack traces.

#### Scenario: Successful scheduled existence detection is recorded
- **WHEN** the finalized existence strategy completes for a scheduled full-eligibility request
- **THEN** the measurement contains `strategy=postgres-exists-v1`, `database_operation=eligibility-existence-probe`, and only its other bounded scheduled context, fallback value, outcome, duration, and successful roundtrip evidence

#### Scenario: Failure contains hostile sensitive text
- **WHEN** a detector fails with an exception whose message contains synthetic SQL, coordinates, identifiers, credentials, and a connection string
- **THEN** none of that text or the exception object appears in the terminal measurement

### Requirement: Runtime database cost claims remain evidence-based
For a successful existence-strategy result, the terminal measurement SHALL record one database roundtrip because the finalized strategy completed its single existence operation. Cancellation and failure measurements SHALL omit the roundtrip field when completion cannot be established. Runtime telemetry SHALL NOT claim rows scanned, query-plan shape, buffer hits, physical reads, or index use; those facts remain unavailable in normal processing and SHALL be handed to change 60's explicit maintainer diagnostic under `postgres-exists-v1`.

#### Scenario: Existence query completes successfully
- **WHEN** the existence-backed detector returns work or no work
- **THEN** its terminal measurement records one database roundtrip without SQL or row-scan claims

#### Scenario: Query is cancelled or fails
- **WHEN** the existence-backed detector is cancelled or fails before successful completion
- **THEN** its terminal measurement does not guess a database roundtrip count or rows-scanned value

### Requirement: Observability does not alter product behavior or state
Detector observability SHALL use structured application logging only. It SHALL NOT add a metrics/exporter dependency, create or alter timeout policy, update processing state or the user-facing log ring, change scheduling or eligibility, resolve a worker backend, launch or suppress a worker, mutate database or configuration state, or add UI behavior.

#### Scenario: Standard scheduled detection is observed
- **WHEN** the Standard scheduler invokes its admitted detector seam
- **THEN** logging observes the call while the existing detector and coordinator remain solely responsible for the work decision and lifecycle

#### Scenario: Non-scheduled paths bypass detection
- **WHEN** manual, Web-only, Run-once, or private-worker paths follow their existing change-57 bypass behavior
- **THEN** this change creates no detector measurement and does not introduce detector activation on those paths
