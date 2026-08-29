## Purpose

Defines the versioned controller-to-worker execute request and cancellation command contract for exactly one processing run, including readiness, correlation, framing, compatibility, sequencing, EOF, and validation boundaries.

## ADDED Requirements

### Requirement: Controller input uses the shared v1 envelope
Every controller-to-worker message SHALL be one JSON object with required properties `protocol`, `version`, `direction`, `category`, `type`, `sequence`, `timestampUtc`, `runId`, and `payload` in that exact serialization order and in the block-15 canonical forms. The controller producer SHALL obtain `timestampUtc` from its injected `TimeProvider`; it is the production time of that input frame, not a request execution timestamp, and accepted controller timestamps SHALL be nondecreasing in their input stream. `protocol` SHALL equal `immich-reversegeo.worker`, `version` SHALL be integer 1, `direction` SHALL equal `controller-to-worker`, `runId` SHALL be a non-empty lower-case canonical GUID, and `payload` SHALL be an object. The only supported v1 input category/type pairs SHALL be `request/execute` and `control/cancel`.

#### Scenario: Execute envelope is recognized
- **WHEN** a canonical v1 controller-to-worker envelope declares `request/execute` with a valid run ID and payload
- **THEN** it is eligible for execute-payload and input-lifecycle validation

#### Scenario: Reserved or unknown command is received
- **WHEN** an input declares ping, shutdown, another request/control type, the worker-to-controller direction, or any other unsupported semantic discriminator
- **THEN** it is rejected rather than treated as a reserved, generic, diagnostic, execute, or cancel message

### Requirement: Execute maps exactly to the immutable processing request
The `request/execute` payload SHALL contain required `trigger` with exactly one canonical token `manual`, `scheduled`, or `run-once`. The accepted typed value SHALL be the block-7 `ProcessingRunRequest` whose `RunId` equals envelope `runId` and whose `Trigger` equals that token's block-7 value. This exact immutable identity and trigger SHALL be preserved for executor invocation, results, run-scoped events, terminal correlation, and any controller/UI “job ID” alias. The protocol SHALL NOT define a `jobId`, another processing mode, or mutable request fields.

#### Scenario: Manual request is reconstructed
- **WHEN** execute carries run ID `01234567-89ab-cdef-0123-456789abcdef` and trigger `manual`
- **THEN** the typed request has that exact non-empty run ID and the block-7 Manual trigger without generating or translating identity

#### Scenario: Run-once trigger is delivered
- **WHEN** execute carries trigger `run-once`
- **THEN** it maps to block-7 RunOnce and invokes the same eligible-assets processing contract rather than a distinct job type or work-set mode

#### Scenario: Mutable execution data is absent
- **WHEN** a controller snapshots an accepted processing request for serialization
- **THEN** only its run ID and trigger become defined request facts, while settings, cron text, eligibility, skipped IDs, asset IDs, credentials, connection strings, and work-set data remain outside the request and are not later reread to mutate it

### Requirement: Ready precedes exactly one execute request
The worker SHALL emit and flush the block-15 process-scoped `ready` event before it attempts to consume the initial execute frame, and the controller SHALL wait for a valid ready event before sending execute. Validation of execute after readiness SHALL be the only action that creates the typed processing request. Exactly one execute request SHALL be accepted per worker process; execute SHALL be the first accepted controller-input message with input sequence 1, and another execute SHALL be invalid before, during, or after that run.

#### Scenario: Request follows readiness
- **WHEN** the controller observes valid ready and sends a valid execute at input sequence 1
- **THEN** exactly one processing request is accepted for that worker process

#### Scenario: Cancel or second execute appears where the request belongs
- **WHEN** cancel is the first input message or another execute follows an accepted execute
- **THEN** the message is rejected and no additional processing request is created

#### Scenario: Bytes arrive before readiness
- **WHEN** controller input is present before the worker has emitted and flushed ready
- **THEN** those bytes alone do not create a run and the worker does not validate an execute until the ready-before-consume boundary is satisfied

### Requirement: Input sequencing is independent and transactional
Controller-input sequence numbers SHALL start at 1 and increment by exactly one for every accepted execute or cancel on stdin. This sequence SHALL be independent from worker stdout sequence values. A message with a duplicate, gap, regression, noncanonical value, or overflow SHALL be rejected. Parsing or lifecycle rejection SHALL NOT consume a sequence number or otherwise mutate the last accepted request/cancel state.

#### Scenario: Consecutive cancel follows execute
- **WHEN** execute sequence 1 and correlated cancel sequence 2 are valid
- **THEN** both are accepted regardless of the current stdout event sequence

#### Scenario: Invalid message does not advance input state
- **WHEN** the next expected sequence is 2 and a malformed or sequence-3 input is rejected
- **THEN** a later valid sequence-2 message remains eligible for validation

### Requirement: Cancel is correlated and effect-idempotent
The `control/cancel` message SHALL carry the accepted execute request's exact `runId` and a payload with no defined v1 properties; canonical serialization SHALL emit `{}`. Cancel SHALL NOT carry or create a second command/job identity, reason, exception, cancellation token, or replacement request. A correctly correlated cancel accepted before executor invocation SHALL latch cooperative cancellation; one accepted during execution SHALL request cancellation of that same run; one consumed after terminal SHALL be a harmless no-op. Multiple correctly sequenced cancels for the same run SHALL be valid and SHALL have the same idempotent cancellation effect. A replay using a consumed sequence, a cancel before execute, or a cancel with empty/different run correlation SHALL be invalid and SHALL have no cancellation effect.

#### Scenario: Cancel wins before execution starts
- **WHEN** a valid cancel follows execute after request acceptance but before executor invocation
- **THEN** execution receives cancellation already requested and retains the original immutable request and normal run-started/terminal protocol lifecycle

#### Scenario: Cancel arrives during execution
- **WHEN** a correctly correlated cancel is accepted while the executor is active
- **THEN** cooperative cancellation is requested for that exact run without replacing its request

#### Scenario: Cancel repeats or arrives after terminal
- **WHEN** one or more later correctly sequenced correlated cancels are consumed after cancellation was already requested or after execution reached terminal
- **THEN** they do not repeat side effects, create another run, or require an acknowledgement

#### Scenario: Cancel correlation is wrong
- **WHEN** cancel omits, empties, reformats, or changes the accepted execute run ID
- **THEN** it is rejected and does not affect the active request or cancellation state

### Requirement: Controller input shares v1 framing and compatibility constraints
Controller input SHALL use strict UTF-8 without BOM and one compact JSON object on one non-empty line. The encoded object SHALL be at most 1,048,576 bytes excluding its delimiter. Emitters SHALL use LF; pure single-message parsing MAY remove one trailing LF or CRLF and SHALL reject a bare CR, extra frame data, literal embedded line breaks, empty input, invalid UTF-8, BOM, or oversized content before JSON deserialization. Property names and tokens SHALL be case-sensitive, GUID/Int64/timestamp values SHALL use block-15 canonical forms, and duplicate properties SHALL be invalid.

For supported v1 messages, unknown properties at envelope and payload object levels SHALL be ignored after duplicate-name detection, including unknown cancel-payload properties. Missing/wrong/invalid known fields and unknown protocol, version, direction, category, or type SHALL fail closed. Canonical serialization SHALL emit only defined fields.

#### Scenario: Input reaches the shared byte limit
- **WHEN** a complete controller input frame is no more than 1,048,576 encoded bytes and otherwise valid
- **THEN** it is evaluated under the same framing rules as a v1 worker event

#### Scenario: Input is oversized or incompatibly encoded
- **WHEN** a frame is oversized, has a BOM or invalid UTF-8, or violates line framing
- **THEN** it is rejected before yielding a JSON message or typed request/control value

#### Scenario: Same-version additive property is received
- **WHEN** a valid v1 execute or cancel includes an unknown nonduplicate object property
- **THEN** the unknown property is ignored and the known typed semantics remain unchanged

#### Scenario: Known or semantic input is invalid
- **WHEN** input has malformed JSON, a duplicate property, a missing or wrong known field, noncanonical primitive, unsupported version, or unknown semantic discriminator
- **THEN** it is rejected without normalization, guessing, or conversion into another message type

### Requirement: EOF and half-close do not imply cancellation
EOF before a complete accepted execute frame SHALL yield no request and SHALL NOT start processing. EOF after bytes from an incomplete frame SHALL be an invalid framing condition. Clean EOF or controller half-close after execute acceptance SHALL mean that no later control message can arrive; it SHALL NOT request cancellation, mutate the request, or prevent execution and stdout terminal reporting. EOF after cancel or terminal SHALL add no semantic effect.

#### Scenario: Stdin closes before request
- **WHEN** stdin reaches clean EOF without one complete valid execute frame
- **THEN** no processing request exists and no work starts

#### Scenario: Stdin closes during a frame
- **WHEN** EOF follows only a prefix of a controller input JSON frame
- **THEN** the input is classified as invalid framing and yields no partial typed value

#### Scenario: Controller half-closes after delivery
- **WHEN** a valid execute has been accepted and the controller closes its stdin write side without cancel
- **THEN** execution continues normally while the worker may still emit stdout events, but future graceful cancellation through stdin is unavailable

### Requirement: Validation failures are safe and acknowledgements are absent
Input parsing and sequence/lifecycle validation SHALL return either one fully validated immutable typed message or one structured failure with a stable machine-readable category and safe bounded diagnostic. Failures SHALL distinguish the applicable block-15 size/encoding/framing, malformed JSON, envelope, unsupported protocol/version/type, payload, sequence, correlation, and lifecycle classes. They SHALL NOT return partial requests/commands, echo raw input or arbitrary payload text, expose parser exceptions/stacks/secrets, start work from invalid initial input, or convert invalid control into cancellation.

Protocol v1 SHALL add no execute-accepted or cancel-accepted worker event. Existing run-started SHALL be the first positive execution evidence for an accepted request, and existing terminal events SHALL report execution outcome when available. The input contract SHALL expose validation failure to the consuming host only; block 22 SHALL own stdin reading/loop and runtime response, and block 23 SHALL own process exit outcomes.

#### Scenario: Initial execute is invalid
- **WHEN** empty, malformed, unsupported, oversized, incompatible, or invariant-breaking initial input is validated
- **THEN** the host receives one safe structured failure, no typed request is returned, and processing does not start

#### Scenario: Control is invalid during a run
- **WHEN** malformed, unsupported, out-of-sequence, or incorrectly correlated control input is validated after execute
- **THEN** the host receives one safe structured failure, validator state is unchanged, and the input does not cancel or mutate the run

#### Scenario: Valid execute reaches executor entry
- **WHEN** the accepted immutable request reaches execution and run-started is emitted
- **THEN** run-started is execution evidence rather than a separate request acknowledgement

### Requirement: This change defines contracts without transport or job generalization
This change SHALL define immutable controller-input envelopes/payloads, explicit block-7 mapping, pure one-message codec behavior, and pure input sequence/lifecycle validation only. It SHALL NOT read stdin, implement a command loop, launch or stop processes, emit or flush stdout, map validation/EOF/execution to exit codes, generalize processing into job kinds, or change executor-owned snapshots and processing behavior.

#### Scenario: Block 17 is applied alone
- **WHEN** the controller-input contracts and deterministic tests are introduced
- **THEN** current in-process execution, worker hosting, console streams, process lifecycle, configuration snapshots, and user-visible behavior remain unchanged

#### Scenario: Later blocks consume the contract
- **WHEN** blocks 22, 23, 24, 25, or 28 implement transport and cancellation responsibilities
- **THEN** they preserve this schema and boundaries, while block 47 remains the first change that may generalize worker jobs
