# Processing Event Reporting Specification

## Purpose

Defines a transport-neutral, UI-independent reporting session for observing one processing run with precise lifecycle, accounting, diagnostic, concurrency, cancellation, failure, and correlated-activity semantics.

## Requirements

### Requirement: Reporter sessions expose a closed transport-neutral event vocabulary
The system SHALL expose an asynchronous processing-event reporter that opens one run-scoped session for an immutable processing request and zero-offset UTC execution-start timestamp. A session SHALL emit immutable run started, eligibility determined, progress changed, activity started, activity ended, log emitted, and run finished events. Every event SHALL identify the originating request, and run finished SHALL carry the matching validated block-7 result. Event contracts MUST NOT contain mutable WebUI state, exception objects, stack traces, cancellation tokens, delegates, serializer annotations, protocol versions, envelope sequence/timestamp fields, framing, or process-exit data.

#### Scenario: Session exposes every defined event kind
- **WHEN** a consumer receives any event from a run-scoped session
- **THEN** it can identify the request and exactly one defined event kind without inspecting WebUI state or transport metadata

#### Scenario: Terminal result retains session identity
- **WHEN** the session emits run finished
- **THEN** its immutable result carries the exact request used to open the session and one completed, cancelled, or failed block-7 outcome

### Requirement: Execution start and eligibility determination are separate ordered facts
Opening a session SHALL emit run started exactly once at execution entry before eligibility counting, with the request and execution-start timestamp but no fabricated eligibility total. If counting succeeds, the session SHALL next emit eligibility determined exactly once with a non-negative total before any progress, log, activity-start, or activity-end event, or normal completion. If counting is cancelled or fails, the session SHALL permit run finished after run started without an eligibility event. A rejected pre-admission invocation SHALL create neither a request nor a session.

#### Scenario: Eligibility count succeeds
- **WHEN** counting returns a non-negative total
- **THEN** run started precedes eligibility determined carrying that total

#### Scenario: Count is cancelled by the active token
- **WHEN** active-run cancellation terminates eligibility counting
- **THEN** run started is followed by a cancelled run finished with no eligibility or progress event

#### Scenario: Count fails fatally
- **WHEN** an unexpected error terminates eligibility counting
- **THEN** run started is followed by a failed run finished with no eligibility or progress event

#### Scenario: Duplicate invocation is rejected
- **WHEN** admission rejects an invocation because another run owns the run lock
- **THEN** no request, session, or processing event is created for the rejected invocation

### Requirement: The run session enforces lifecycle singularity and finality
A non-broken session SHALL accept at most one eligibility event and exactly one run-finished event. It SHALL reject progress before eligibility, events after finish, mismatched requests, invalid terminal result timing, and a second terminal attempt. Before accepting run finished, the session SHALL close any session-owned activity still open; run finished SHALL then be the final accepted event. Exactly-one-terminal observation is guaranteed only while the supplied reporter continues accepting valid events.

#### Scenario: Empty run completes
- **WHEN** eligibility is zero and the run completes normally
- **THEN** the accepted order is run started, eligibility determined with zero, and completed zero-count run finished

#### Scenario: Finish is requested with an open activity
- **WHEN** terminal closure begins while a session-owned activity remains open
- **THEN** the session emits its activity-ended event before run finished and later scope disposal is a local no-op

#### Scenario: Event is attempted after finish
- **WHEN** a caller attempts any report after run finished was accepted
- **THEN** the session rejects it without emitting another event

### Requirement: Progress snapshots preserve block-7 accounting
A progress-changed event SHALL carry one coherent immutable snapshot with non-negative `ProcessedCount`, `UpdatedCount`, `SkippedCount`, and `FailedCount`, where processed equals updated plus skipped plus failed. Updated SHALL count only successful Immich writes; skipped SHALL count actively evaluated no-write dispositions; failed SHALL count handled per-asset exceptions; and processed SHALL count those three terminal per-asset dispositions. Session snapshots SHALL be monotonic and SHALL be emitted only after a new terminal per-asset disposition. Previously suppressed assets SHALL not contribute.

#### Scenario: Successful write changes updated accounting
- **WHEN** an Immich location write returns successfully
- **THEN** the session commits one updated disposition and emits a snapshot incrementing processed and updated once

#### Scenario: Deliberate no-write changes skipped accounting
- **WHEN** an actively evaluated asset reaches a current deliberate no-write disposition
- **THEN** the session commits one skipped disposition and emits a snapshot incrementing processed and skipped once

#### Scenario: Handled asset exception changes failed accounting
- **WHEN** an asset exception is handled and execution continues
- **THEN** the session commits one failed disposition and emits a snapshot incrementing processed and failed once

### Requirement: Irreversible dispositions survive cancellation and publication delay
Once a successful write, deliberate skip, or handled per-asset failure reaches its terminal disposition, the session SHALL commit its accounting and publish the resulting snapshot through a non-cancelled path. Cancellation arriving after that disposition, while waiting for the session serialization gate, or while the reporter applies backpressure SHALL not erase the disposition or lower terminal counts. An asset interrupted before a terminal disposition SHALL remain uncounted. Publication cancellation before an event's linearization point SHALL emit no event; cleanup and committed-disposition publication MUST NOT use the already-cancelled run token.

#### Scenario: Cancellation follows successful write
- **WHEN** a location write succeeds and active cancellation is requested before its snapshot is accepted
- **THEN** the updated disposition is still published and retained in the cancelled terminal result

#### Scenario: Cancellation interrupts work before disposition
- **WHEN** active cancellation interrupts an asset before write, deliberate skip, or handled failure is committed
- **THEN** no progress count is added for that asset

#### Scenario: Ordinary publication wait is cancelled before acceptance
- **WHEN** a cancellable non-cleanup report is cancelled before the reporter's linearization point
- **THEN** no event is emitted and the caller observes cancellation

### Requirement: Fatal run failure remains distinct from handled asset failure
An unexpected pass-level failure SHALL be represented only by a failed run result with a non-blank diagnostic message and SHALL NOT increment progress `FailedCount`. A run reaching normal completion SHALL have a completed result even after handled per-asset failures. An unrelated cancellation-like exception SHALL be fatal; only cancellation attributable to the active run token SHALL produce cancelled. Reporter payloads SHALL retain neither originating exception nor stack trace.

#### Scenario: Run completes after handled asset failures
- **WHEN** one or more per-asset failures are counted and the pass reaches normal completion
- **THEN** run finished is completed and retains those assets in `FailedCount`

#### Scenario: Fatal pass error terminates the run
- **WHEN** an unexpected pass-level error terminates processing
- **THEN** run finished is failed with message-only detail and no extra per-asset failed increment

#### Scenario: Unrelated cancellation-like exception occurs
- **WHEN** a cancellation-like exception occurs while the active run token is not requested
- **THEN** run finished is failed rather than cancelled

### Requirement: Activity identity and terminal closure are disposal-safe
Each activity start SHALL carry a non-empty opaque identifier and non-blank display label; its end SHALL carry the same request and identifier. Equal labels SHALL use distinct identifiers. An asynchronous scope helper SHALL return only after start is accepted, report at most one end on first disposal, and make repeated disposal a no-op. Exception or cancellation unwind SHALL use a non-cancelled end path. Session terminal closure SHALL end all still-open session activities before finish and mark their scopes closed so later disposal emits nothing.

#### Scenario: Equal labels overlap
- **WHEN** two concurrent activities use the same label
- **THEN** they have distinct identifiers and ending either one does not end the other

#### Scenario: Scope unwinds during cancellation
- **WHEN** active cancellation leaves a started activity scope before terminal closure
- **THEN** the matching activity-ended event is accepted once through a non-cancelled path

#### Scenario: Terminal closure precedes scope disposal
- **WHEN** finish closes an outstanding activity and its scope is disposed later
- **THEN** no event follows run finished

### Requirement: Log events preserve existing UI-log boundaries with typed severity
Each log-emitted event SHALL carry exactly one defined severity of Trace, Information, Warning, or Error and a non-blank plain message without timestamp or severity prefix. Only messages currently sent to `ProcessingState.AppendLog` or produced by `ProcessingState.IncrementError` SHALL become processing log events during compatibility routing; `ILogger`-only messages SHALL remain outside this event stream. Current pre-write resolved-location detail SHALL be Trace and SHALL remain before the write attempt; it is resolution detail, not proof of write success. Existing UI warning/error call sites SHALL map to Warning/Error. Lifecycle and summary lines MAY be derived by block 9 from lifecycle events. Payloads MUST NOT contain exception objects, stacks, arbitrary structured state, or a claim of future wire safety.

#### Scenario: Resolution detail precedes a failing write
- **WHEN** current processing resolves a location, emits its UI detail, and the subsequent write fails
- **THEN** Trace resolution detail precedes the Error diagnostic without being classified as write success

#### Scenario: No-city branch remains logger-only
- **WHEN** current processing reaches a no-city branch that logs only through `ILogger` and increments skipped state
- **THEN** compatibility routing emits the skipped progress disposition but no new UI processing log event

#### Scenario: Exception reaches a diagnostic boundary
- **WHEN** a handled exception is described in a log or failed result
- **THEN** only intended message text crosses the event boundary and no exception object or stack trace is retained

### Requirement: Acceptance order is linearizable, isolated, and backpressured
A run session SHALL be safe for concurrent calls from parallel asset work and SHALL establish one linearizable accepted order per session. An event's linearization point is when the reporter has synchronously consumed it or copied its immutable value into reporter-owned bounded capacity; successful completion means that acceptance occurred. Ordering between different sessions is unspecified and SHALL not affect either session's validation. Producers SHALL await every operation and MUST NOT use fire-and-forget reporting. Block 8 SHALL neither drop nor coalesce accepted events nor require an unbounded queue. A queued reporter implementation SHALL bound its own capacity and apply asynchronous backpressure; the no-op reporter requires no queue.

#### Scenario: Concurrent assets report outcomes
- **WHEN** parallel callers report through one session
- **THEN** the consumer observes one linearizable order with coherent monotonic snapshots

#### Scenario: Reporter applies bounded backpressure
- **WHEN** reporter-owned bounded capacity is unavailable
- **THEN** the operation asynchronously waits before its linearization point instead of dropping, coalescing, running fire-and-forget, or growing an unbounded queue

#### Scenario: Two sessions report concurrently
- **WHEN** two valid sessions use the same thread-safe reporter
- **THEN** each retains its own lifecycle, counters, activities, and terminal validation regardless of cross-session interleaving

### Requirement: Reporter faults break the session without recursive reporting
If the underlying reporter throws or returns cancellation before an event is accepted, the session SHALL propagate that failure and enter a broken state. A broken session SHALL reject further ordinary reports. It SHALL perform only local activity-state cleanup and SHALL NOT recursively attempt activity-end, log, or failed-terminal reporting through the reporter already known to be broken. A fault while accepting run finished means the validated result exists at the producer but no terminal observation is guaranteed. Reporter failure is infrastructure failure and SHALL NOT be counted as a per-asset failed disposition.

#### Scenario: Reporter fails during an ordinary event
- **WHEN** the reporter faults while accepting eligibility, progress, activity, or log data
- **THEN** the caller observes the fault, the session becomes broken, and no recursive terminal report is attempted through that reporter

#### Scenario: Reporter fails while ending an activity
- **WHEN** activity-end acceptance faults
- **THEN** the session becomes broken, performs local idempotent scope cleanup, and emits no recursive cleanup events

#### Scenario: Reporter fails while accepting terminal result
- **WHEN** run-finished acceptance faults
- **THEN** the producer retains its validated result but the event contract makes no exactly-once observation claim

### Requirement: No-op and recording reporters support staged adoption
The contract SHALL include a stateless thread-safe no-op reporter that accepts every valid event without side effects and test support for a thread-safe recording reporter that preserves each session's linearized immutable payload order. Test support SHALL permit deterministic acceptance gates and injected faults before the linearization point. Neither reporter SHALL require `ProcessingState`, Blazor, a logging provider, database, geodata, or worker protocol.

#### Scenario: No observer is configured
- **WHEN** a caller uses the no-op reporter
- **THEN** valid session reports complete without changing processing behavior or UI state

#### Scenario: Contract test controls acceptance
- **WHEN** a test gates or faults recording-reporter acceptance
- **THEN** it can assert awaiting, cancellation-before-acceptance, ordering, broken-session behavior, and immutable recorded payloads without timing sleeps

### Requirement: Block-8 introduction preserves runtime behavior and later-block boundaries
Introducing the reporter/session contract SHALL NOT rewire `ProcessingBackgroundService`, mutate or adapt `ProcessingState`, change Blazor subscriptions, alter scheduling/admission/`MarkPending()`/run-lock ownership, move resolver/cache progress, extract execution, or define worker serialization. Existing WebUI processed SHALL continue to mean successful writes; block 9 SHALL map `UpdatedCount` to it and MAY separately project fatal terminal failure into the legacy UI error count without changing domain `FailedCount`.

#### Scenario: Contract is introduced before its adapter
- **WHEN** block 8 is applied by itself
- **THEN** the active service, state, UI, logs, processing outputs, and user-visible behavior remain unchanged

#### Scenario: Later adapter receives eligibility
- **WHEN** block 9 receives run started before counting and later eligibility determined
- **THEN** it may preserve current UI timing by deferring `ProcessingState.StartRun(total)` until eligibility is known
