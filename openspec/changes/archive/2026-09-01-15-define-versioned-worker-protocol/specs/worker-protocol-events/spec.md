## Purpose

Defines the versioned worker-to-controller event envelope, canonical JSON/NDJSON representation, compatibility policy, validation behavior, and ordered lifecycle that later worker and launcher transports must preserve.

## ADDED Requirements

### Requirement: Protocol v1 has a fixed envelope identity and shape
Every v1 event SHALL be one JSON object with these required envelope properties, in this exact serialization order: `protocol`, `version`, `direction`, `category`, `type`, `sequence`, `timestampUtc`, `runId`, and `payload`. A v1 parser MAY additionally receive additive unknown envelope properties as defined below; the v1 serializer SHALL emit exactly and only the listed envelope properties in that order. `protocol` SHALL equal `immich-reversegeo.worker`; `version` SHALL be the JSON integer `1`; `direction` SHALL equal `worker-to-controller`; `payload` SHALL be an object; and category/type combinations SHALL be closed and validated. Property and discriminator matching SHALL be case-sensitive.

#### Scenario: Valid v1 envelope is recognized
- **WHEN** a message supplies the fixed protocol identity, supported version and direction, a defined category/type pair, and valid required values
- **THEN** it is eligible for typed payload and stream-lifecycle validation

#### Scenario: Envelope identity is incompatible
- **WHEN** protocol, version, direction, category, or type is missing, has the wrong JSON type, differs only by case, or is not supported
- **THEN** the message is rejected and does not yield a valid event

### Requirement: Run identity is the sole job correlation identity
For every accepted processing job, envelope `runId` SHALL be exactly the non-empty block-7 `ProcessingRunRequest.RunId`. “Job ID” SHALL be an alias for that same value and SHALL NOT introduce a second identifier. The process-scoped `ready` message SHALL have `runId: null` because it precedes request acceptance; every run-scoped message SHALL carry the same non-empty run ID. Run IDs SHALL use lower-case canonical hyphenated GUID text.

#### Scenario: Accepted job retains block-7 identity
- **WHEN** a worker reports events for an accepted processing request
- **THEN** every run-scoped envelope carries the request's exact run ID and no independent job ID

#### Scenario: Ready precedes run correlation
- **WHEN** a worker announces readiness before reading an accepted request
- **THEN** the ready envelope contains `runId: null` and creates no processing run

#### Scenario: Correlation changes within a run
- **WHEN** a later run-scoped message omits, empties, reformats, or changes the established run ID
- **THEN** stream validation rejects the message

### Requirement: Event vocabulary maps block-8 facts without changing their meaning
V1 SHALL define these category/type pairs: `lifecycle/ready` with `{}`; `lifecycle/run-started` with `trigger`, `startedAtUtc`; `lifecycle/eligibility-determined` with `eligibleCount`; `progress/progress-changed` with `processedCount`, `updatedCount`, `skippedCount`, `failedCount`; `activity/activity-started` with `activityId`, `label`; `activity/activity-ended` with `activityId`; `diagnostic/log-emitted` with `level`, `message`; and `terminal/completed`, `terminal/cancelled`, or `terminal/failed` with `trigger`, `startedAtUtc`, `endedAtUtc`, `processedCount`, `updatedCount`, `skippedCount`, `failedCount`, `failureMessage`. The listed payload properties are required and SHALL be serialized in exactly the listed order; a parser MAY ignore additive unknown payload properties only as defined below, while serialization emits exactly and only the listed payload properties. Payloads SHALL preserve block-7 and block-8 invariants: trigger vocabulary; execution start; non-negative eligibility; coherent non-negative Int64 accounting where processed equals updated plus skipped plus failed; non-empty opaque activity ID and start label; matching activity end ID; Trace/Information/Warning/Error log level with non-blank message; and terminal result timing, counts, outcome, and safe failure-message rules. No payload SHALL carry an exception object, stack trace, cancellation token, delegate, credentials, connection string, SQL, or arbitrary structured state.

#### Scenario: Transport-neutral event is encoded
- **WHEN** a defined block-8 event is mapped to the wire
- **THEN** its wire type and typed payload preserve the source request, accounting, activity, log, and lifecycle facts

#### Scenario: Run finish is encoded
- **WHEN** block 8 finishes with a validated Completed, Cancelled, or Failed block-7 result
- **THEN** the codec emits respectively completed, cancelled, or failed with the same run ID, execution timestamps, counters, and permitted failure message

#### Scenario: Payload violates source invariants
- **WHEN** a payload contains a negative or inconsistent count, invalid trigger/log level, blank required text, invalid activity identity, invalid terminal timing, or outcome/failure-detail mismatch
- **THEN** the message is rejected rather than normalized

### Requirement: JSON names and primitive forms are canonical
Envelope and payload property names SHALL be case-sensitive camel case. Discriminator tokens SHALL be lower-case kebab case. GUID values SHALL be lower-case `D` format. Sequence and count values SHALL be base-10 JSON Int64 integers without quotes, fractions, or exponents. Every timestamp SHALL be a JSON string in exact `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'` form and denote zero-offset UTC. The worker event producer SHALL obtain `timestampUtc` from its injected `TimeProvider` at event production; `run-started.timestampUtc` SHALL equal its payload `startedAtUtc`, and a terminal `timestampUtc` SHALL equal its payload `endedAtUtc`. Within an accepted stream timestamps SHALL be nondecreasing, and terminal `endedAtUtc` SHALL be greater than or equal to `startedAtUtc`. Serialization SHALL be compact and deterministic for the same immutable value.

#### Scenario: Known event is serialized repeatedly
- **WHEN** the same valid event is serialized more than once
- **THEN** each encoded UTF-8 JSON byte sequence is identical

#### Scenario: Noncanonical primitive is received
- **WHEN** a GUID, integer, token, property name, or timestamp is semantically similar but not in its required canonical form
- **THEN** parsing rejects it without silently normalizing the input

### Requirement: NDJSON framing and encoding are bounded
A protocol message SHALL be strict UTF-8 without a byte-order mark and SHALL contain one compact JSON object on one non-empty line. The encoded JSON object SHALL be at most 1,048,576 bytes excluding its line delimiter. Emitters SHALL use one LF delimiter; single-message parsing MAY accept and remove one trailing LF or CRLF but SHALL reject a bare CR, additional data, empty lines, literal embedded line breaks, invalid UTF-8, BOM, or an oversized message before JSON deserialization. JSON-escaped newline characters inside string values remain data and SHALL NOT create frames.

#### Scenario: Maximum-size valid line is parsed
- **WHEN** a complete valid message is no more than 1,048,576 encoded bytes and has an allowed delimiter
- **THEN** the single-message codec evaluates that one JSON object

#### Scenario: Oversized or invalidly encoded line is received
- **WHEN** encoded content exceeds the limit, contains invalid UTF-8 or BOM, or violates the one-line delimiter rules
- **THEN** it is rejected before yielding a JSON event

### Requirement: Compatibility is additive within a supported version and fail-closed otherwise
For a supported protocol identifier and version, unknown object properties SHALL be ignored at the envelope and nested payload object levels so additive producers remain compatible. Required known properties and all known invariants SHALL still be validated. Duplicate JSON property names SHALL be rejected. Unknown direction, category, type, protocol identifier, or version SHALL be rejected; an unknown type SHALL NOT be coerced into a log or failure event. Serialization SHALL emit only the defined v1 fields.

#### Scenario: Same-version additive field is received
- **WHEN** a valid v1 envelope or payload includes an otherwise unknown property
- **THEN** parsing ignores that property and retains the known typed event

#### Scenario: Duplicate or unknown semantic discriminator is received
- **WHEN** a message duplicates a property or declares an unknown direction, category, type, protocol identifier, or version
- **THEN** it is rejected without guessing producer intent

### Requirement: Sequence and lifecycle validation are deterministic
The first accepted message in one worker stdout stream SHALL be `ready` with sequence 1. Every later accepted message SHALL increment sequence by exactly one across that stream, including the transition from process-scoped ready to run-scoped events. A stream SHALL accept ready exactly once and only first. An accepted run SHALL contain exactly one run-started event, zero or one eligibility-determined event, only lifecycle-legal later events, and exactly one terminal event before the stream is complete. Incremental validation SHALL accept at most one terminal; stream finalization SHALL report an accepted run without a terminal as incomplete without fabricating one. A completed run requires eligibility. Between run-started and eligibility-determined, no progress, activity, or diagnostic event is legal; cancellation or failure before counting MAY transition directly from run-started to terminal. A terminal event SHALL be final. Sequence gaps, regressions, duplicates, overflow, duplicate ready/start/eligibility/terminal, any pre-eligibility progress/activity/log event, invalid activity pairing, timestamp regression, and post-terminal messages SHALL be rejected.

#### Scenario: Empty run completes
- **WHEN** the stream contains ready, run-started, eligibility-determined with zero, and completed with consecutive sequences
- **THEN** stream validation accepts exactly one ready and one final terminal event

#### Scenario: Count fails before eligibility
- **WHEN** run-started is followed directly by a matching failed terminal with the next sequence
- **THEN** stream validation accepts the block-8 pre-count failure lifecycle

#### Scenario: Order or cardinality is invalid
- **WHEN** sequence is not exactly next, ready is not first or repeats, a lifecycle event repeats illegally, activity ends without a matching open activity, or any message follows terminal
- **THEN** stream validation rejects that message and does not advance its accepted state

### Requirement: Parsing and validation failures are safe and non-partial
The codec SHALL return either one fully validated typed event or one structured failure with a stable machine-readable failure category and safe bounded diagnostic text. Failure categories SHALL distinguish encoding/framing/size, malformed JSON, envelope, unsupported protocol/version/type, payload, sequence, correlation, and lifecycle errors. It SHALL NOT return a partially populated valid event, raw parser exception, stack trace, secret-bearing input echo, or unbounded input text.

#### Scenario: Invalid message is parsed
- **WHEN** any framing, JSON, envelope, payload, compatibility, sequence, correlation, or lifecycle rule fails
- **THEN** the caller receives one safe structured failure and no valid event

### Requirement: Stream ownership is specified without implementing transport
When stdout transport is introduced, stdout SHALL contain only protocol NDJSON frames and ordinary worker logs SHALL be written to stderr; non-protocol text on stdout is a protocol violation. This change SHALL define immutable contracts, pure single-message codec behavior, and pure stream validation only. It SHALL NOT define controller requests or commands, perform stdout emission/flushing/concurrent write serialization, read stdin, map process exit codes, launch a process, drain pipes, retain stderr tails, or classify runtime worker crashes.

#### Scenario: Block 15 is applied alone
- **WHEN** the protocol foundation and deterministic tests are introduced
- **THEN** existing in-process execution, Web state, hosting, process I/O, and user-visible behavior remain unchanged

#### Scenario: Later transport adopts the contract
- **WHEN** blocks 17, 21, 22, 23, 25, or 30 implement their respective responsibilities
- **THEN** they consume these identifiers, framing, compatibility, correlation, order, and purity rules without moving those transport responsibilities into block 15
