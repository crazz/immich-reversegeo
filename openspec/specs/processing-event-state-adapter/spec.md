# Processing Event State Adapter Specification

## Purpose

Preserves the characterized WebUI processing-state behavior while the admitted in-process processing pass reports through the transport-neutral run-event session.

## Requirements

### Requirement: Admitted runs preserve pending and start timing
The system SHALL mark an accepted manual or scheduled run pending immediately after it acquires admission, SHALL create exactly one request and event session for that accepted run, and SHALL expose a new run start timestamp, supplied total, zero counters, and cleared prior error only when eligibility is determined. A rejected invocation MUST create no request or event session. A run cancelled or failed before eligibility SHALL become terminal without fabricating a total or a new start timestamp.

#### Scenario: Accepted run waits on eligibility
- **WHEN** an accepted run is pending and eligibility counting has not completed
- **THEN** the WebUI reports running/pending immediately while retaining the prior start timestamp, total, counters, and error

#### Scenario: Eligibility starts the visible run
- **WHEN** eligibility counting succeeds with a non-negative total
- **THEN** the WebUI records a new UTC start time, exposes that total, resets processed/skipped/error counters, clears the prior error, and retains the prior completion timestamp and recent logs

#### Scenario: Eligibility never completes
- **WHEN** the active run is cancelled or fails during eligibility counting
- **THEN** the WebUI records terminal completion without a new start timestamp or fabricated eligibility total

#### Scenario: Duplicate admission is rejected
- **WHEN** an invocation is rejected because another run owns admission
- **THEN** no request, event session, start projection, or duplicate processing pass is created

### Requirement: Event progress preserves legacy WebUI meanings
For every applicable progress snapshot, the WebUI processed counter SHALL equal `UpdatedCount`, the skipped counter SHALL equal `SkippedCount`, and the ordinary error counter SHALL equal `FailedCount`. Aggregate `ProcessedCount` MUST NOT replace the legacy processed display. Previously suppressed assets and assets cancelled before a terminal disposition SHALL not change these counters. A fatal run failure SHALL add one legacy UI error without changing the domain result's per-asset `FailedCount`.

#### Scenario: Updated and aggregate processed differ
- **WHEN** a progress snapshot reports one update, two skips, and one handled failure
- **THEN** the WebUI exposes processed one, skipped two, and errors one rather than aggregate processed four

#### Scenario: Previously suppressed asset is encountered
- **WHEN** an asset is excluded by the existing previously-unresolvable set before active evaluation
- **THEN** it contributes to none of the run progress counters

#### Scenario: Fatal failure follows handled failures
- **WHEN** a run with two handled per-asset failures later ends in a fatal pass failure
- **THEN** the terminal result retains `FailedCount` two while the legacy WebUI error counter is three

### Requirement: Skipped, handled-error, and fatal diagnostics remain distinct
The system SHALL retain the existing UI-log boundary for each disposition. Existing skipped branches with a warning SHALL append one `[WARN]` line, the existing no-city logger-only branch SHALL add no UI log, and every handled error event SHALL append exactly one `[ERROR]` line and expose its plain message as `LastError` without double-counting its failed disposition. Fatal completion SHALL expose and append exactly `Fatal: <failure detail>` as the newest error. Cancellation SHALL append the cancellation line and MUST NOT add an ordinary or fatal error.

#### Scenario: Warning-producing skip is reported
- **WHEN** an actively evaluated asset reaches an existing no-country or no-admin-match skipped branch
- **THEN** the skipped counter advances and one timestamped `[WARN]` line retains the existing message text

#### Scenario: Logger-only no-city skip is reported
- **WHEN** an actively evaluated asset resolves country/state but no writable city
- **THEN** the skipped counter advances with no new WebUI processing log line

#### Scenario: Handled asset error is reported
- **WHEN** an asset exception is handled and its error diagnostic and failed disposition are accepted
- **THEN** the WebUI error counter advances once, `LastError` is that asset message, and exactly one matching `[ERROR]` line is appended

#### Scenario: Fatal pass failure is reported
- **WHEN** a non-cancellation pass failure ends the run
- **THEN** one additional legacy UI error is visible and `LastError` plus the newest error line contain `Fatal: <failure detail>`

#### Scenario: Active cancellation ends the run
- **WHEN** cancellation attributable to the active run token ends execution
- **THEN** the WebUI appends `Run cancelled.` without adding an error or replacing `LastError`

### Requirement: Lifecycle and summary logs retain current order and text
After eligibility, the system SHALL derive the existing zero or nonzero run-start line. Block 8 accepts required `ActivityEnded` events before `RunFinished`, and the adapter SHALL project those ends first. On matching `RunFinished`, it SHALL append any cancellation or fatal line using `FailureMessage`, perform only idempotent defensive activity cleanup, make the state inactive and record a new UTC completion time, and then append `Run complete. Processed=<visible-processed> Skipped=<visible-skipped> Errors=<visible-errors>`. After eligibility those visible counters SHALL be the event-mapped counts; before eligibility they SHALL retain the pending snapshot, with a fatal outcome adding its one legacy UI error. Final totals, counters, latest error, and recent logs SHALL remain observable after completion.

#### Scenario: Empty run completes
- **WHEN** eligibility is zero and execution completes normally
- **THEN** the nothing-to-process line precedes terminal completion and the zero-valued summary

#### Scenario: Non-empty run completes
- **WHEN** a non-empty run reaches completed outcome
- **THEN** the non-empty start line precedes run diagnostics and terminal completion precedes the final summary notification

#### Scenario: Cancelled run completes
- **WHEN** an eligible run ends cancelled
- **THEN** the cancellation line precedes completion and the final summary reflects every irreversible disposition accepted before cancellation

#### Scenario: Failed run completes
- **WHEN** an eligible run ends failed
- **THEN** the fatal error line precedes completion and the final summary includes the additional legacy fatal error

### Requirement: Recent logs retain severity, cap, and accepted order
The system SHALL timestamp projected messages at state projection time, SHALL prefix Warning with `[WARN]` and Error with `[ERROR]`, SHALL add no severity prefix for current Trace or Information UI lines, and SHALL retain exactly the newest 100 entries in accepted insertion order. It MUST NOT duplicate timestamp or severity prefixes supplied by the adapter.

#### Scenario: More than one hundred events are projected
- **WHEN** more than 100 uniquely identifiable lifecycle or diagnostic lines are projected
- **THEN** the log snapshot contains exactly the newest 100 once each in accepted order

#### Scenario: Resolution detail precedes a write failure
- **WHEN** Trace resolution detail is accepted before the subsequent write fails
- **THEN** the unprefixed detail line precedes exactly one prefixed error line and does not advance the processed counter

### Requirement: Correlated activities preserve duplicate labels and terminal finality
The system SHALL correlate activity scopes by the combination of run identity and non-empty activity identity, not by label alone. Equal labels with different identities SHALL remain independently active; ending one SHALL not end the other. Duplicate or unknown ends SHALL be no-ops. Terminal projection SHALL clear every tracked scope for that run and later ends or disposal MUST NOT restore or decrement activity in a later run.

#### Scenario: Equal labels use distinct identities
- **WHEN** two activity starts in one run carry the same label and different identities
- **THEN** ending either identity leaves the label visible until the other identity ends

#### Scenario: Distinct current activity ends
- **WHEN** activity A starts, activity B starts, and B ends while A remains
- **THEN** A becomes the sole visible current activity

#### Scenario: Duplicate and unknown ends arrive
- **WHEN** an already-ended or unknown activity identity is ended
- **THEN** current activity and all valid tracked scopes remain unchanged

#### Scenario: Terminal precedes a late end
- **WHEN** terminal projection clears an open activity and its end arrives after terminal or after a new run is armed
- **THEN** no activity is revived or removed from the later run

### Requirement: Run correlation prevents stale and cross-run state mutation
The WebUI projection SHALL be armed with exactly one admitted request and SHALL mutate state only for events carrying that request identity in legal per-session order. An unarmed, mismatched, stale, post-terminal, duplicate-terminal, or prior-run event SHALL cause no observable state mutation or change notification. Terminal projection SHALL release the armed run only after its completion and summary mutations are finished.

#### Scenario: Event carries another run identity
- **WHEN** an event for an unarmed or different request arrives while a run is pending or active
- **THEN** it changes no counters, logs, timestamps, error, activity, or running state

#### Scenario: Old event arrives during a later run
- **WHEN** a late progress, activity, log, or terminal event from a completed request arrives after another request is armed
- **THEN** the later run's state remains unchanged

#### Scenario: Duplicate terminal arrives
- **WHEN** a second terminal event for the completed request is presented
- **THEN** no second completion timestamp, summary, error, or notification is produced

### Requirement: State projection retains observer semantics
Every accepted event that produces one or more observable state mutations SHALL complete those mutations synchronously before event acceptance returns and SHALL raise `OnChanged` at least once for each projected observable mutation. Exact callback multiplicity is not contractual. Ignored correlation-invalid events SHALL raise no change notification.

#### Scenario: Observer receives a projected mutation
- **WHEN** an observer subscribes before an accepted eligibility, progress, diagnostic, activity, or terminal mutation
- **THEN** it receives at least one notification after the corresponding observable value has been updated

#### Scenario: Observer sees an ignored event
- **WHEN** an observer is subscribed and a stale or cross-run event is ignored
- **THEN** it receives no notification for that event

### Requirement: First production routing has an explicit block boundary
The admitted in-process main pass SHALL report eligibility, batch and asset UI logs, updated/skipped/failed dispositions, cancellation, failure, and completion through its run session rather than performing duplicate direct mutations. Startup, next-schedule, contention, and pending control-plane state SHALL retain their direct behavior. Processing-time resolver/cache progress SHALL retain its existing direct state bridge in this change and SHALL be moved by block 10; no new resolver result, cache, Lookup, scheduling, admission, or cancellation ownership behavior SHALL be introduced.

#### Scenario: Main pass executes through the adapter
- **WHEN** an admitted run evaluates and writes or skips assets
- **THEN** each main-pass lifecycle/log/disposition mutation reaches WebUI state exactly once through the event session

#### Scenario: Resolver reports progress during block 9
- **WHEN** the production resolver reports cache activity or diagnostic progress during this change
- **THEN** its existing direct state bridge remains the source of that progress and no duplicate event copy is emitted

#### Scenario: Control-plane message occurs
- **WHEN** service startup, next scheduling, admission contention, or pending state changes
- **THEN** its current direct WebUI state behavior remains unchanged and no processing-run event is fabricated
