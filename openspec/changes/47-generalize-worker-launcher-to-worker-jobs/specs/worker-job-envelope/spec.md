## Purpose

Defines a backward-compatible, strongly typed child-worker job contract that reuses one process lifecycle and one correlation identity across processing and future isolated capabilities.

## ADDED Requirements

### Requirement: Versioned worker-job compatibility
The system SHALL preserve the complete v1 processing protocol and the exact sole private worker argument `--internal-worker` unchanged. It SHALL select v2 before ready only through the child-process environment entry named exactly `IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION`, where absence selects legacy v1 and the only accepted present value is exactly `2`. It MUST NOT silently reinterpret, negotiate, upgrade, downgrade, or select a protocol from an ambient user value.

#### Scenario: Existing v1 processing launch
- **WHEN** the controller launches the exact sole argument `--internal-worker`, explicitly removes the reserved entry from the child environment, and sends a v1 processing request
- **THEN** the worker uses the existing v1 ready, request, event, terminal, framing, ordering, and exit behavior without semantic or byte-golden changes

#### Scenario: Explicit v2 job launch
- **WHEN** the controller launches the exact sole argument `--internal-worker`, sets `IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION=2` in that child descriptor, and sends a valid v2 job request
- **THEN** the worker validates the entry before ready, emits v2 ready, and validates only the v2 vocabulary for that session

#### Scenario: Parent has an ambient v2 selector during a v1 launch
- **WHEN** the controller process inherited any value for the reserved entry but its command descriptor selects v1
- **THEN** the command builder removes the entry from the child environment and the worker starts in v1

#### Scenario: Parent has an ambient invalid selector during a v2 launch
- **WHEN** the controller process inherited an empty, malformed, or unsupported reserved-entry value but its command descriptor selects v2
- **THEN** the command builder replaces it with exact `2` for that child without mutating the parent environment

#### Scenario: Reserved entry has an unsupported value
- **WHEN** the private worker starts with the reserved entry present as any value other than exact `2`
- **THEN** it fails with the established invalid-input outcome before host construction, dependency initialization, ready, stdin processing, or work

#### Scenario: Environment entry appears without worker role
- **WHEN** a normal Web invocation inherits the reserved entry without the exact sole private worker argument
- **THEN** the entry does not select worker role or become application configuration

#### Scenario: Selected and encoded versions differ
- **WHEN** an envelope version does not match the version selected by absence or exact child value `2`
- **THEN** the worker rejects input before accepting or executing a job and does not silently downgrade

#### Scenario: Public configuration surfaces are inspected
- **WHEN** deployment environment documentation, AppConfig, configuration binding, Docker examples, UI, or status models are inspected
- **THEN** the reserved child-only entry is not exposed as a supported user setting

### Requirement: One worker job identity
The system SHALL use one nonempty canonical GUID identity from admission through request, session, events, cancellation, terminal result, classification, and cleanup. A processing job's job identity MUST equal its existing processing run identity, and no launcher, worker, adapter, or handler MAY mint a second correlation or attempt identity.

#### Scenario: Processing identity crosses v1 and v2 adapters
- **WHEN** an admitted processing request is represented by either protocol version
- **THEN** v1 `runId`, v2 `jobId`, session identity, cancellation target, every job frame, and final cleanup all contain the same GUID value

#### Scenario: Identity mismatch
- **WHEN** a request, event, result, cancellation, or terminal frame carries an identity other than the active job identity
- **THEN** validation fails without mutating another job or releasing another job's handle

### Requirement: Closed typed job variants
The v2 system SHALL select request, event, and result payloads by a case-sensitive closed job-kind discriminator and SHALL use concrete capability-specific DTOs. It MUST NOT accept a generic object, dictionary, arbitrary JSON element, string-keyed options bag, reflection-selected runtime type, or unknown-property store as a job payload or result.

#### Scenario: Processing job accepted
- **WHEN** a valid `ProcessAssets` request is submitted and its handler is registered
- **THEN** the worker dispatches the typed processing request exactly once and accepts only processing-specific events and result data for that job

#### Scenario: Kind and payload disagree
- **WHEN** a payload or result type does not match its envelope job kind
- **THEN** the frame fails validation before partial values are delivered or work begins

#### Scenario: Unknown or unregistered kind
- **WHEN** a request names an unknown kind or a reserved kind with no registered typed handler
- **THEN** the request is rejected before acceptance and before heavy service initialization, with no job terminal frame

#### Scenario: Future capability is added
- **WHEN** a later change enables `CoordinateLookup` or `CacheMutation`
- **THEN** it adds a concrete request DTO, result DTO, allowed event set, validation, codec goldens, descriptor, and typed handler before the kind is advertised as supported

### Requirement: Common and capability-specific event model
V2 SHALL provide process-scoped ready plus common job-started, bounded log, scoped activity, and terminal output for every supported kind. Every non-ready frame SHALL carry the active job identity and kind. Capability progress/diagnostic events SHALL be concrete variants allowed only for their declared kind.

#### Scenario: Worker becomes ready
- **WHEN** a v2 worker finishes required startup
- **THEN** it emits and flushes exactly one process-scoped ready frame with null job identity and the actually registered supported job kinds before reading stdin

#### Scenario: Common log and activity events
- **WHEN** an accepted handler reports a log or scoped activity
- **THEN** the worker emits a bounded common typed event with the active identity and kind while preserving sequence and activity lifecycle rules

#### Scenario: Processing progress is bridged
- **WHEN** a ProcessAssets handler reports eligibility, counts, activity, or log information
- **THEN** the processing adapter produces the same validated ProcessingState transitions and visible count semantics as the v1 processing bridge

#### Scenario: Event kind is invalid for the job
- **WHEN** a handler or stream emits a capability event not allowed for the active kind
- **THEN** validation fails and the event is not delivered to observers

### Requirement: One worker terminal after acceptance
After accepting one execute request, the worker host SHALL own emission of exactly one terminal frame for handler completion, cooperative cancellation, or handled failure. A completed terminal SHALL contain exactly one typed result matching the job kind; a failed terminal SHALL contain exactly one bounded structured safe error; a cancelled terminal SHALL not contain a success result. Handlers MUST NOT emit terminal frames directly.

#### Scenario: Job completes
- **WHEN** a typed handler returns successfully
- **THEN** observers receive one completed terminal with the same identity and kind and the matching typed result after all preceding events

#### Scenario: Job is cooperatively cancelled
- **WHEN** cancellation reaches the active handler and cleanup succeeds
- **THEN** observers receive one cancelled terminal and the process uses the established cooperative-cancellation outcome

#### Scenario: Handler fails safely
- **WHEN** an accepted handler reports a handled domain failure
- **THEN** observers receive one failed terminal with a stable error code, category, and bounded safe message and no exception object, stack trace, raw input, stderr, or secret

#### Scenario: Failure occurs before acceptance or outside terminal transport
- **WHEN** invocation, startup, framing, protocol, process, stdout transport, sink, forced-kill, or shutdown evidence prevents a valid worker terminal
- **THEN** the controller classifier finalizes the outcome exactly once without fabricating a worker terminal frame

### Requirement: Reusable launcher session and cancellation lifecycle
The generalized launcher SHALL preserve existing command construction, process ownership, readiness, execute-flush, stdout/stderr draining, timeout, stop escalation, wait-only cancellation, disposal, and finality behavior. The session SHALL expose the one job identity and job kind while hiding the platform process.

#### Scenario: Session starts
- **WHEN** the child process is successfully created for a typed job
- **THEN** the launcher returns a session only after owning process, stream-pump, and exit-observation lifecycles, without waiting for ready

#### Scenario: Cancellation targets active job
- **WHEN** stop is requested for the active cancellable job after execute flush
- **THEN** at most one cancel frame with the same identity is written and repeated stop callers join the same deadline/escalation operation

#### Scenario: Caller stops waiting
- **WHEN** cancellation is requested only on a session wait
- **THEN** the wait ends without abandoning, killing, cancelling, or duplicating ownership of the child lifecycle

#### Scenario: Job kind does not alter finality
- **WHEN** any supported job exits or is killed
- **THEN** completion still waits for process exit, stdout EOF, stderr EOF/drain, protocol finalization, bridge cleanup, and exact handle release

### Requirement: Typed DI handler registry
The worker host SHALL resolve accepted jobs through a DI-composed registry with exactly one descriptor and typed handler per supported kind. Registry construction SHALL reject duplicate kinds and incompatible request/result declarations deterministically. Only registered kinds SHALL be advertised in ready.

#### Scenario: Duplicate handler registration
- **WHEN** composition contains two handlers for one kind
- **THEN** worker startup fails deterministically before ready or job work

#### Scenario: Valid processing registration
- **WHEN** composition registers the ProcessAssets descriptor and matching typed handler
- **THEN** the registry resolves it without reflection or string-to-runtime-type activation and the host retains sole terminal ownership

#### Scenario: Reserved future kind lacks handler
- **WHEN** `CoordinateLookup` or `CacheMutation` has not yet supplied its complete typed handler registration
- **THEN** ready does not advertise that kind and input cannot initialize its heavy dependencies

### Requirement: Arbitration metadata without arbitration policy
Each supported job descriptor SHALL expose immutable typed metadata sufficient for a later coordinator to identify its kind, capability family, heavy/geodata resource class, cancellability, and request origin. This change MUST NOT queue jobs, assign priority, own a global active slot, or treat local busy admission as a worker process outcome.

#### Scenario: Processing metadata is inspected
- **WHEN** a coordinator inspects the ProcessAssets descriptor and request
- **THEN** it can identify the exclusive heavy-worker resource class, cancellability, and processing origin without decoding an untyped payload

#### Scenario: Local admission is busy
- **WHEN** a later controller-side arbitrator rejects a second job as busy
- **THEN** no worker is launched and the rejection is not reported as worker exit code 3

### Requirement: Exit and classifier compatibility
The generalized host SHALL retain the established managed exit meanings and precedence for every job kind: completion/no work, invalid invocation/request/input, global advisory-lock busy, handler/domain failure, startup/configuration/host failure, output protocol/transport failure, and cooperative cancellation. Exit code 3 MUST remain exclusive to the global advisory lock. Raw abrupt termination SHALL remain unmapped evidence.

#### Scenario: Invalid job request
- **WHEN** kind, version, identity, payload, or input sequence validation fails before acceptance
- **THEN** the process uses the established invalid-input outcome and emits no synthetic job terminal

#### Scenario: Typed handler domain failure
- **WHEN** an accepted handler fails in its domain execution path and terminal transport succeeds
- **THEN** the process uses the established domain-failure outcome and the committed failed terminal remains authoritative

#### Scenario: Local arbitration rejection
- **WHEN** a controller rejects a job before launching a worker
- **THEN** no process exit is fabricated and advisory-lock-busy exit code 3 is not used

### Requirement: Processing behavior parity and reversible migration
The system SHALL retain v1 processing support while v2 ProcessAssets parity is established. Switching production processing to v2 MUST NOT change processing trigger, request snapshot, scheduling/manual exclusion, advisory locking, progress/count projection, activity/log presentation, cancellation grace, terminal state, or failure classification.

#### Scenario: V1 and v2 processing parity
- **WHEN** the same processing request and deterministic executor event sequence run through v1 and v2 adapters
- **THEN** observers receive equivalent ProcessingState lifecycle, counts, logs, activities, terminal status, and classified outcome with the same identity

#### Scenario: V2 rollout is rolled back
- **WHEN** production command selection is returned to the retained v1 processing path
- **THEN** the unchanged v1 codec and invocation resume without data migration or a second run identity
