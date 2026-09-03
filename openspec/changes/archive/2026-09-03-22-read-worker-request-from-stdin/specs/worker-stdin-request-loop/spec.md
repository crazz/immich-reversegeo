## Purpose

Provides a bounded, byte-correct standard-input transport that acquires one worker execute request, observes correlated cancellation controls beside execution, and reports input finality safely without owning stdout events or process exit codes.

## ADDED Requirements

### Requirement: Standard input has one owner behind readiness
The internal worker SHALL give exactly one byte-oriented input component exclusive managed ownership of a dedicated standard-input stream. The component SHALL NOT use `Console.In`, `TextReader.ReadLine`, or another text-line API that can allocate an entire untrusted line before enforcing the byte limit. It SHALL begin its first read only after the block-21 `lifecycle/ready` frame has been successfully written and flushed. No other worker component SHALL read, replace, or dispose standard input while that component is active.

#### Scenario: Input is already buffered at startup
- **WHEN** controller bytes are present before worker readiness
- **THEN** the worker emits and flushes ready before the input owner performs its first read or validates those bytes

#### Scenario: Worker input ownership is reviewed
- **WHEN** the internal-worker composition and lifecycle paths are inspected
- **THEN** only the bounded input component receives the dedicated standard-input stream and no path uses a competing console text reader

### Requirement: Frames are decoded incrementally with a hard byte bound
The input owner SHALL incrementally read bytes, preserve strict UTF-8 decoder state across arbitrary stream chunk boundaries, and split frames only on the literal LF byte. It SHALL accept either LF or one CR immediately before LF as the delimiter, remove that delimiter before invoking the block-17 one-frame codec, and reject a bare CR, an empty frame, invalid or truncated UTF-8, a byte-order mark, or any other framing violation. The JSON object SHALL contain at most 1,048,576 bytes excluding LF or CRLF. The reader SHALL detect overflow without retaining an unbounded prefix and SHALL retain no more than the protocol limit, one possible delimiter CR, and fixed bounded read/decoder state before returning a safe oversized-frame failure.

#### Scenario: UTF-8 and delimiter cross read boundaries
- **WHEN** a multibyte UTF-8 scalar, the CR/LF pair, or both are split across arbitrary input chunks
- **THEN** the resulting complete frame is decoded and validated exactly as the same bytes delivered in one chunk

#### Scenario: Frame is exactly at the object limit
- **WHEN** a valid JSON object contains exactly 1,048,576 encoded bytes and is followed by LF or CRLF
- **THEN** the frame remains eligible for block-17 parsing without an oversized allocation

#### Scenario: Frame exceeds the object limit
- **WHEN** more than 1,048,576 object bytes arrive before LF, allowing only one pending CR that could belong to CRLF
- **THEN** the reader reports message-too-large without buffering the remainder or invoking JSON deserialization

#### Scenario: Empty or invalidly encoded line arrives
- **WHEN** the input contains a bare LF/CRLF frame, BOM, invalid UTF-8, truncated UTF-8, or bare CR framing
- **THEN** it yields one safe input failure and no typed execute or cancel value

### Requirement: Exactly one execute lease is published
After readiness, the input owner SHALL apply the block-17 codec and transactional controller-input validator to complete frames. The first accepted input SHALL be `request/execute` with independent input sequence 1. Acceptance SHALL publish exactly one block-20 request lease containing the exact immutable processing request and one cooperative cancellation signal. The initial acquisition boundary SHALL settle once as accepted execute, clean pre-request EOF, or safe pre-request failure; it SHALL never publish a second lease or reconstruct a different identity or trigger.

#### Scenario: Valid execute is accepted
- **WHEN** a valid execute frame with input sequence 1 follows readiness
- **THEN** one lease exposes that exact run ID and trigger and the worker host can invoke the one-shot executor once

#### Scenario: Duplicate execute arrives
- **WHEN** any second execute frame arrives before, during, or after the accepted run
- **THEN** it is rejected as invalid lifecycle, no second lease or executor invocation is created, and accepted input state is not replaced

#### Scenario: Input sequence is independent
- **WHEN** execute sequence 1 and later controls use exact-next controller-input sequences while stdout has unrelated sequence values
- **THEN** input validation depends only on the accepted stdin sequence and commits state only for a fully valid frame

### Requirement: Correlated cancel is observed throughout the accepted-run race window
Immediately after execute acceptance, the input owner SHALL continue a control pump independently of executor progress. A correctly sequenced `control/cancel` with the accepted request's exact run ID SHALL request the lease's single cooperative cancellation signal at most once in effect. If accepted before executor invocation, cancellation SHALL already be requested when execution receives the linked token; if accepted during execution, it SHALL request that same token; if its validation is ordered after terminal, it SHALL be a harmless no-op. Repeated correctly sequenced correlated cancels SHALL remain valid and effect-idempotent. A cancel-first, replayed/gapped sequence, or wrong/empty run correlation SHALL have no cancellation effect.

#### Scenario: Cancel is buffered behind execute
- **WHEN** execute and a valid next-sequence correlated cancel are available before the host invokes the executor
- **THEN** the lease is published once and its cancellation signal can be latched before executor entry

#### Scenario: Cancel races active execution
- **WHEN** a valid correlated cancel commits while the executor is running
- **THEN** the exact accepted run's cooperative token is requested without replacing its request or emitting an acknowledgement

#### Scenario: Cancel races terminal finality
- **WHEN** cancel validation and terminal notification contend
- **THEN** one deterministic atomic ordering classifies the cancel as pre-terminal cancellation effect or post-terminal no-op, never both

#### Scenario: Cancel repeats
- **WHEN** multiple correlated cancels carry consecutive unconsumed input sequences
- **THEN** each valid frame advances input sequence while the shared cancellation effect occurs no more than once

### Requirement: EOF preserves request and cancellation semantics
Clean EOF with no buffered bytes before execute acceptance SHALL settle initial acquisition as pre-request EOF, create no processing request, invoke no executor, and produce no run terminal. EOF with any unterminated frame bytes SHALL be invalid framing, both before and after execute. Clean EOF after execute acceptance SHALL stop control availability only; it SHALL NOT cancel or mutate the request, prevent executor invocation or completion, or suppress the accepted run's normal stdout terminal reporting.

#### Scenario: Stdin closes before request
- **WHEN** the first read reaches EOF with no frame bytes buffered
- **THEN** the host receives clean pre-request EOF and no run starts

#### Scenario: Stdin closes during initial frame
- **WHEN** EOF follows a non-empty prefix before any execute delimiter
- **THEN** the host receives a partial-frame input failure and no request or terminal is created

#### Scenario: Controller half-closes after execute
- **WHEN** clean EOF occurs on a frame boundary after execute acceptance
- **THEN** the control pump completes normally and execution continues without implicit cancellation

#### Scenario: Stdin closes during a control frame
- **WHEN** EOF follows a non-empty unterminated control prefix after execute acceptance
- **THEN** the worker records a post-acceptance input failure without treating the prefix or EOF as cancellation

### Requirement: Invalid frames and reader faults fail closed without changing the run
Malformed JSON; empty, duplicate-execute, unknown, unsupported-version, wrong-direction, incompatible-type, invalid-payload, out-of-sequence, or incorrectly correlated frames SHALL stop further input consumption with one safe structured input failure. A non-cancellation exception from the input stream SHALL similarly become one safe reader failure. Before execute acceptance, either failure SHALL settle initial acquisition as pre-request failure and start no run. After execute acceptance, it SHALL be attached to accepted-run host finality, SHALL NOT itself request cooperative cancellation, mutate the request, synthesize a terminal, or suppress the executor/reporter's one normal terminal attempt. Block 23 SHALL decide the process exit consequence when execution and an input failure both exist.

#### Scenario: Initial frame is malformed or incompatible
- **WHEN** the first complete frame cannot yield a valid sequence-1 execute
- **THEN** no lease is published, no work starts, and one safe pre-request failure is handed to the host

#### Scenario: Invalid control follows execute
- **WHEN** a malformed, unknown, duplicate-execute, incompatible, out-of-sequence, or wrongly correlated frame follows the accepted execute
- **THEN** the input pump records one post-acceptance failure, stops reading, and neither cancels nor changes the accepted run

#### Scenario: Stream read throws
- **WHEN** the owned stream faults before or after request acceptance
- **THEN** the fault is classified at the corresponding pre-request or accepted-run boundary without exposing the raw exception

### Requirement: Input finality and diagnostics do not add stdout messages
The input component SHALL emit no execute acknowledgement, cancel acknowledgement, protocol error, log event, or synthetic terminal on stdout. `run-started` remains the first positive execute evidence, and the executor/reporter plus block-21 emitter remain the sole owners of accepted-run events and the one terminal attempt. The component SHALL hand a stable machine-readable outcome and bounded safe diagnostic to block-20 host coordination and MAY log the same safe category best-effort to stderr. It SHALL NOT echo raw frame bytes, arbitrary payload text, parser/stream exception messages, stack traces, credentials, or secret-like sentinels. Block 23 remains the sole owner of exit-code selection.

#### Scenario: Input validation fails
- **WHEN** a frame or reader failure is reported
- **THEN** stdout receives no acknowledgement or diagnostic frame and host coordination receives only the safe structured outcome

#### Scenario: Accepted execution finishes
- **WHEN** the executor/reporter completes or cancels the accepted run after any valid control activity
- **THEN** exactly its block-21 terminal attempt is used and the input component does not duplicate or replace it

### Requirement: The command pump has structured lifetime and disposal
The accepted request lease SHALL expose completion/finality for its background control pump. The pump SHALL run only from readiness until pre-request finality, clean post-request EOF, fatal input failure, terminal/host shutdown, or lease disposal. Terminal notification and disposal SHALL stop accepting new control work without waiting indefinitely for future stdin bytes, unblock any pending read through the owned stream's cancellation/disposal path, await the pump's completion exactly once, and dispose decoder/buffer/cancellation resources. Expected shutdown cancellation SHALL not replace an earlier EOF/failure outcome or be reported as a reader fault. The worker SHALL not wait after terminal for a future cancel merely to observe post-terminal behavior.

#### Scenario: Terminal occurs while read is pending
- **WHEN** execution reaches terminal while the control pump is blocked waiting for more input
- **THEN** lease finalization unblocks and awaits the pump before its resources are disposed and host shutdown completes

#### Scenario: Pump already completed
- **WHEN** clean EOF or an input failure completed the pump before execution terminal
- **THEN** lease finalization observes the recorded outcome and disposes resources without restarting or double-awaiting the reader

#### Scenario: Shutdown cancellation unblocks input
- **WHEN** host shutdown or lease disposal cancels a pending read
- **THEN** the pump settles as expected disposal unless a prior primary input outcome was already recorded
