## Purpose

Provides a lossless, ordered worker-to-controller NDJSON output stream whose protocol frames remain parseable under concurrent processing, cancellation, and transport faults without mixing ordinary logs into stdout.

## ADDED Requirements

### Requirement: Readiness and processing sessions map exactly to protocol frames
The worker emitter SHALL emit the block-15 `lifecycle/ready` frame once as the first process-scoped frame with `runId: null` and an empty payload. For one accepted block-8 processing session, it SHALL map `RunStarted`, `EligibilityDetermined`, `ProgressChanged`, `ActivityStarted`, `ActivityEnded`, and `LogEmitted` respectively to `lifecycle/run-started`, `lifecycle/eligibility-determined`, `progress/progress-changed`, `activity/activity-started`, `activity/activity-ended`, and `diagnostic/log-emitted`. It SHALL map `RunFinished` to exactly one `terminal/completed`, `terminal/cancelled`, or `terminal/failed` frame according to the validated result. Every run-scoped frame SHALL carry exactly the session request's non-empty `ProcessingRunRequest.RunId`; no second job identity SHALL be generated.

#### Scenario: Worker becomes ready before accepting a run
- **WHEN** worker output initialization completes
- **THEN** one ready frame with null run correlation is flushed before the emitter accepts any run-session event

#### Scenario: Processing events are reported
- **WHEN** a valid accepted processing session reports each block-8 event kind
- **THEN** each event produces exactly its corresponding block-15 category, type, timestamp, correlation, and typed payload without omission or reinterpretation

#### Scenario: Processing finishes before eligibility is known
- **WHEN** a session reports run started and then a permitted pre-count cancelled or failed result
- **THEN** the emitter writes the matching terminal directly after run-started without fabricating eligibility or progress

### Requirement: Sequence allocation and ordering are stream-wide
The emitter SHALL allocate sequence numbers only within its single serialized acceptance/write order. Sequence SHALL start at 1 for ready and increment by exactly one across all later run-scoped frames; it SHALL NOT restart at run start or maintain a separate per-job counter. Concurrent reporters SHALL observe a linearizable FIFO acceptance order, and each accepted source event SHALL produce at most one sequence value and at most one frame.

#### Scenario: Concurrent asset tasks report events
- **WHEN** multiple asset or resolver tasks report valid events concurrently
- **THEN** their accepted frames have unique consecutive stream sequences and their bytes never interleave

#### Scenario: Run starts after readiness
- **WHEN** ready has been successfully flushed and the accepted session reports run started
- **THEN** run-started uses sequence 2 and the exact request run ID

### Requirement: Emission uses canonical atomic NDJSON lines
The emitter SHALL reuse the block-15 canonical codec and named message-size limit. Each successful frame SHALL be built as one contiguous strict UTF-8 buffer without BOM containing one compact JSON object followed by exactly one LF byte, and SHALL be submitted through the sole stdout writer without `TextWriter` newline or encoding transformation. The emitter SHALL flush after every frame and SHALL complete the corresponding report operation only after that frame's write and flush succeed. Successful concurrent writes SHALL never form partial or mixed lines.

#### Scenario: A frame is written successfully
- **WHEN** the codec produces a valid frame within the block-15 byte limit
- **THEN** stdout receives exactly the canonical JSON bytes plus one LF, no BOM or CR, and the report completes after flush

#### Scenario: Multibyte and escaped content is emitted
- **WHEN** a permitted log or activity value contains multibyte UTF-8 or escaped newline data
- **THEN** byte counting follows UTF-8, the escape remains inside one JSON object, and exactly one physical LF terminates the frame

### Requirement: Concurrent emission has bounded lossless backpressure
The emitter SHALL use a bounded queue feeding exactly one writer. When capacity is exhausted, producers SHALL asynchronously wait for space; this change SHALL NOT drop, overwrite, sample, batch, or coalesce any accepted event. Cancellation by the active report token before queue acceptance SHALL remove that candidate without allocating a sequence. Once a candidate is accepted, later caller cancellation SHALL NOT retract it; its operation SHALL complete only with successful write/flush or the shared transport failure.

#### Scenario: Slow stdout saturates the queue
- **WHEN** the writer is blocked and the configured test capacity is full
- **THEN** additional producers remain asynchronously backpressured and no event is dropped or coalesced

#### Scenario: Waiting producer is cancelled before acceptance
- **WHEN** the active report cancellation token is cancelled while its candidate is still waiting for queue capacity
- **THEN** that candidate emits no frame and consumes no sequence

#### Scenario: Cancellation occurs after acceptance
- **WHEN** the report token is cancelled after the candidate enters the emitter's accepted order
- **THEN** the candidate remains committed and completes according to the eventual write and flush outcome

### Requirement: Stdout has one exclusive managed owner
In worker role, the emitter SHALL be the only application component given the stdout protocol stream. It SHALL use a dedicated byte stream, not `Console.Out`, `Console.SetOut`, `Console.Write`, or `Console.WriteLine`. Ordinary worker `ILogger` providers SHALL write to stderr, and reporter diagnostics SHALL NOT be mirrored through ordinary logging. No startup banner, framework log, exception text, or other non-protocol application text SHALL be written to stdout.

#### Scenario: Worker records an ordinary log
- **WHEN** an `ILogger` records worker startup, execution, or transport diagnostic text
- **THEN** the text is written only to stderr and stdout remains a sequence of protocol frames

#### Scenario: Source ownership is reviewed
- **WHEN** worker execution and composition paths are checked
- **THEN** they contain no direct `Console.Out` or `Console.Write*` use and only the emitter receives the managed stdout stream

### Requirement: Transport faults break emission safely
A domain-to-protocol mapping, validation, serialization, write, or flush failure SHALL atomically transition the emitter to a broken state, stop accepting new events, fail queued and future report operations with one stable transport-level failure that does not include raw payload, secret, exception, or stack details, and perform no retry that could duplicate an uncertain frame. A broken-pipe or mid-write failure MAY leave one trailing incomplete physical line at the operating-system boundary; the emitter SHALL write nothing after it and SHALL NOT fabricate a protocol log, activity end, failed terminal, or replacement sequence. A best-effort safe transport diagnostic MAY be sent through stderr `ILogger`, but logger failure SHALL NOT replace or recurse into the original failure. Process exit classification is outside this change.

#### Scenario: Serialization fails before a write
- **WHEN** mapping, validation, size checking, or serialization fails for an accepted event
- **THEN** no bytes for that event are written, the emitter becomes broken, and all affected callers receive a safe non-payload-bearing transport failure

#### Scenario: Stdout breaks during write or flush
- **WHEN** the stream throws, closes, or reports a broken pipe while writing or flushing an accepted frame
- **THEN** the emitter performs no retry or further stdout write and fails pending/future reports consistently

### Requirement: Terminal closure is ordered and final
The emitter SHALL accept a terminal candidate only after all previously accepted frames, including activity-ended frames produced by block-8 session cleanup. Accepting the terminal candidate SHALL close the intake to later run events. The terminal frame SHALL be the last frame, and successful emitter completion SHALL require its successful write and flush. Late event or activity-scope disposal attempts SHALL produce no stdout bytes. Emitter disposal, cancellation, or transport failure SHALL NOT invent a terminal or activity closure; block 23 owns exit behavior for missing terminal outcomes.

#### Scenario: Session finishes with active activities
- **WHEN** block-8 session finalization closes its accepted activity scopes and then reports RunFinished
- **THEN** each activity-ended frame precedes the terminal and the terminal is the final flushed line

#### Scenario: Event arrives after terminal acceptance
- **WHEN** any producer reports after the terminal candidate has closed intake
- **THEN** the report is rejected without allocating sequence or writing stdout

#### Scenario: Emitter is disposed before terminal
- **WHEN** shutdown or failure ends emission before a terminal has been accepted and flushed
- **THEN** no synthetic terminal is written and the missing-terminal outcome remains for blocks 23/25/30 to classify

### Requirement: Protocol output does not amplify sensitive data
The emitter SHALL serialize only the fields defined by the block-15 typed payload contract. It SHALL NOT add `ILogger` scopes, structured state, exception objects, stack traces, credentials, connection strings, SQL, environment values, raw input, or arbitrary objects to a frame. Log-event messages and terminal failure details SHALL already satisfy the safe block-8/block-15 producer contract; invalid, blank, oversized, or otherwise unsafe payloads SHALL fail before stdout write rather than being echoed in protocol or stderr failure diagnostics. This transport SHALL NOT claim heuristic discovery of secrets inside otherwise valid free text.

#### Scenario: A transport failure involves secret-like payload text
- **WHEN** an invalid event contains a distinctive credential-like sentinel and mapping or serialization fails
- **THEN** neither the thrown transport failure nor the stderr diagnostic echoes that sentinel or raw event payload

#### Scenario: Typed log event is valid
- **WHEN** a pre-sanitized block-8 log event contains only a valid level and non-blank safe message
- **THEN** the emitter writes exactly the block-15 diagnostic payload without attaching logger metadata or exception details
