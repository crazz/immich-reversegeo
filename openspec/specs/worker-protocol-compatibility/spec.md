# Worker Protocol Compatibility Specification

## Purpose

Provides durable, versioned compatibility evidence for worker-to-controller protocol events so canonical bytes remain stable, additive same-version evolution stays readable, and invalid codec or lifecycle input fails closed before process transports depend on it.

## Requirements

### Requirement: Every v1 event type has canonical and round-trip evidence
The compatibility suite SHALL retain one reviewable canonical UTF-8 JSON fixture for each v1 worker-to-controller event type defined by block 15. It SHALL verify complete typed decoding, deterministic repeated serialization, exact canonical bytes including the normative envelope and per-type payload property order, and parse-serialize-parse preservation. It SHALL additionally exercise every trigger token, diagnostic level, and terminal outcome without requiring a golden fixture for every value combination.

#### Scenario: Canonical event corpus is verified
- **WHEN** the suite enumerates ready, run-started, eligibility-determined, progress-changed, activity-started, activity-ended, log-emitted, completed, cancelled, and failed fixtures
- **THEN** every fixture decodes to its complete expected typed value and that value serializes repeatedly to the exact retained bytes

#### Scenario: Defined token variants round trip
- **WHEN** valid manual, scheduled, run-once, trace, information, warning, error, completed, cancelled, and failed values pass through the codec
- **THEN** their typed meaning and canonical defined-field output are preserved

### Requirement: Same-version additions are compatible but unknown semantics fail closed
The suite SHALL prove that unknown properties in a supported v1 envelope or payload are ignored after duplicate detection and are omitted from canonical reserialization. It SHALL separately prove that unsupported or noncanonical protocol identities, versions, directions, categories, types, and category/type combinations are rejected without coercion.

#### Scenario: Additive producer fields are read by a v1 consumer
- **WHEN** valid v1 fixtures include unknown scalar, null, array, or object properties at envelope or payload level
- **THEN** decoding yields the same known typed event and reserialization emits only the canonical v1 fields

#### Scenario: Duplicate additive field is received
- **WHEN** an otherwise unknown property name appears more than once in the same object
- **THEN** decoding rejects the frame rather than treating duplication as compatible evolution

#### Scenario: Unknown protocol semantics are received
- **WHEN** a frame declares an unsupported or case-varied protocol, integer version, direction, category, type, or a mismatched known category/type pair
- **THEN** decoding returns the corresponding safe failure and no generic diagnostic, terminal, or partial event

### Requirement: Required fields and primitive forms have systematic boundary coverage
The suite SHALL mutate valid canonical frames to cover every required envelope field and every type-specific payload field across the v1 vocabulary. Coverage SHALL distinguish missing, duplicate, wrong JSON kind, forbidden null, blank, invalid value, noncanonical representation, and violated payload invariant. GUIDs, timestamps, and JSON Int64 values SHALL include valid boundaries and invalid lexical/domain boundaries.

#### Scenario: Required field is absent, duplicated, or invalid
- **WHEN** one known envelope or payload field is removed, duplicated, assigned the wrong JSON kind, or assigned a value that violates its v1 invariant
- **THEN** decoding returns a stable failure category and no valid or partially populated event

#### Scenario: GUID boundary is exercised
- **WHEN** run or activity identities use canonical non-empty lower-case D format, empty GUID, upper-case, braces, compact form, malformed text, null in an illegal context, or the wrong JSON kind
- **THEN** only forms permitted by the exact event contract are accepted, including null run correlation only for ready

#### Scenario: Timestamp boundary is exercised
- **WHEN** timestamps use supported representable year boundaries and exact seven-digit UTC Z form, the producer clock supplies event timestamps, or candidates use offsets, invalid dates, wrong fractional precision, wrong case, wrong run-started/terminal equality, stream regression, terminal chronology, or a non-string kind
- **THEN** only canonical zero-offset values with required producer/equality semantics and nondecreasing event/terminal chronology are accepted

#### Scenario: Int64 boundary is exercised
- **WHEN** sequence and count fields use zero or Int64 maximum where permitted, or use negatives, overflow, underflow, quotes, fractions, exponents, or inconsistent processed totals
- **THEN** only canonical base-10 integer values satisfying the field's domain invariants are accepted

### Requirement: One-frame UTF-8 and size behavior is adversarially covered
The suite SHALL pass raw bytes to the production single-message codec and use its named 1,048,576-byte JSON-object limit. It SHALL cover exact-limit and one-byte-oversized content, ASCII and multibyte UTF-8, optional single LF or CRLF input delimiters, BOM, invalid or truncated UTF-8, empty input, bare or repeated delimiters, truncated JSON, literal embedded line breaks, trailing data, and multiple frames in one call. Oversized input SHALL be classified before JSON parsing.

#### Scenario: Exact byte limit is accepted for evaluation
- **WHEN** a complete valid frame contains exactly the maximum number of JSON-object bytes, excluding an allowed delimiter
- **THEN** the codec evaluates that one frame using byte count rather than character count

#### Scenario: Oversized input is malformed too
- **WHEN** input is both more than one byte over the maximum and malformed JSON
- **THEN** it returns the size failure rather than a JSON failure and yields no event

#### Scenario: Delimiters and encoded newlines are distinguished
- **WHEN** one frame has no delimiter, one LF, one CRLF, a bare CR, repeated delimiters, a literal line break, or an escaped newline inside a JSON string
- **THEN** only the block-15 single-frame forms are accepted and escaped newline data does not create another frame

#### Scenario: Truncated or multiple input is supplied
- **WHEN** bytes end within a JSON token or multibyte UTF-8 scalar, contain trailing non-frame data, or contain two valid frames
- **THEN** the one-frame codec rejects the entire candidate and returns no first/partial event

### Requirement: Stdout purity is constrained at the codec boundary
Without opening or redirecting a process stream, the suite SHALL prove that a candidate stdout line is accepted only when it is exactly one supported protocol frame. It SHALL reject ordinary log text, prefixes or suffixes around JSON, whitespace-only lines, and syntactically valid JSON with unsupported protocol semantics. It SHALL NOT test console routing, stderr separation, line flushing, concurrent writes, pipe draining, or runtime process classification.

#### Scenario: Non-protocol stdout candidate is evaluated
- **WHEN** the codec receives ordinary text, a log-prefixed object, a JSON object with trailing text, or unsupported semantic JSON
- **THEN** it returns a safe failure and no typed protocol event

### Requirement: Stream lifecycle and rejection atomicity are comprehensively covered
The suite SHALL drive the block-15 public stream validator through valid ready-only, completed, cancelled, failed, progress, diagnostic, and paired-activity sequences. It SHALL cover exact-next sequence, stable run correlation, ready/start/eligibility/terminal cardinality, legal direct pre-eligibility cancellation/failure terminals, rejection of pre-eligibility progress/activity/diagnostic events, completion only after eligibility, activity pairing, terminal finality, and missing-terminal finalization. Every rejected candidate SHALL leave validator state unchanged so the corrected event at the same expected sequence can still be accepted.

#### Scenario: Valid stream shapes complete
- **WHEN** ready is followed by a correctly correlated run-started and either eligibility plus completed, or a permitted direct cancelled/failed terminal, with exact consecutive sequences
- **THEN** incremental validation accepts the events and finalization reports the corresponding complete state

#### Scenario: Sequence, correlation, or cardinality is invalid
- **WHEN** a candidate has a gap, duplicate, regression, changed or illegal null run identity, duplicate ready/start/eligibility/terminal, completed without eligibility, illegal pre-eligibility event, unmatched activity end, unfinished required activity state, or post-terminal event
- **THEN** validation rejects that candidate with the applicable stable category and does not advance accepted state

#### Scenario: Rejected candidate is corrected
- **WHEN** an invalid candidate is followed by a valid replacement using the still-expected sequence and state
- **THEN** the valid replacement is accepted, proving rejection did not mutate sequence, correlation, activity, lifecycle, or terminal state

#### Scenario: Stream ends without an accepted run terminal
- **WHEN** finalization follows ready only or follows run-started without a terminal
- **THEN** ready-only reports no accepted run while the accepted unterminated run reports incomplete, and neither path fabricates a terminal event

### Requirement: Failures are safe, stable, and non-partial
For each block-15 failure family, the suite SHALL assert its stable machine-readable category, absence of a valid or partial event, bounded diagnostic text, and omission of raw input, secret-bearing sentinels, parser exceptions, stack traces, credentials, connection strings, and SQL-like content. The suite SHALL NOT require exact human diagnostic prose unless the protocol specification makes that prose normative.

#### Scenario: Hostile invalid content fails
- **WHEN** invalid content includes a distinctive payload sentinel or secret-like text
- **THEN** the result identifies the failure family with bounded safe text and does not echo the hostile content or expose exception details

### Requirement: Versioned fixtures are retained and deliberately updated
Canonical and original v1 fixtures SHALL be retained as backward-compatibility evidence and SHALL NOT be blanket-regenerated when implementation changes. Same-version additive forward-compatibility fixtures SHALL be retained separately and SHALL canonicalize back to defined v1 bytes. A deliberate canonical byte change SHALL require an explicit reviewed protocol decision and either restoration of v1 behavior or a new versioned fixture directory; any approved fixture correction SHALL be documented rather than silently overwriting evidence.

#### Scenario: Serializer implementation changes without a protocol change
- **WHEN** refactoring changes produced bytes or stops reading an original v1 fixture
- **THEN** compatibility tests fail and fixtures remain unchanged until production behavior is restored or an explicit protocol-version decision is approved

#### Scenario: Additive v1 fixture is introduced
- **WHEN** a forward-compatibility fixture adds an unknown field without changing known v1 semantics
- **THEN** the original canonical fixture remains retained, the additive fixture decodes, and canonical reserialization omits the addition

### Requirement: Compatibility coverage remains transport and command independent
The suite SHALL use pure block-15 event codec and state-machine APIs only. It SHALL NOT start a host or process; access console, stdin, stdout, stderr, or pipes; test launcher/fixture executables, exit codes, stderr tails, crashes, or runtime fault classification; or define/test controller-to-worker requests and commands owned by block 17.

#### Scenario: Suite boundary is reviewed
- **WHEN** block-16 test dependencies and fixtures are inspected
- **THEN** they contain worker-to-controller event codec/state-machine coverage only and do not duplicate blocks 17, 21, 22, 25, 26, or 30
