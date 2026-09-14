## Purpose

Defines validated, immutable facts for identifying one accepted processing run and summarizing its terminal execution independently of mutable Web UI state or any worker-wire format.

## ADDED Requirements

### Requirement: A processing request has stable identity and trigger vocabulary
The system SHALL represent a processing request with an immutable non-empty `Guid` run identifier and exactly one defined trigger: `Manual`, `Scheduled`, or `RunOnce`. A new identifier SHALL identify each accepted invocation; an invocation rejected before admission SHALL produce no processing-run request or result. `RunOnce` identifies the deployment invocation source, while an internal worker or execution backend SHALL NOT be represented as a trigger.

#### Scenario: Each accepted invocation is identified
- **WHEN** manual, scheduled, or run-once work is accepted as a processing run
- **THEN** its request retains a non-empty run identifier and the corresponding defined trigger for the lifetime of that run

#### Scenario: Duplicate invocation is rejected before identity creation
- **WHEN** an invocation is rejected because another processing run already owns admission
- **THEN** no request or terminal result is created for the rejected invocation

#### Scenario: Invalid request values are rejected
- **WHEN** request construction receives an empty run identifier or an undefined trigger value
- **THEN** construction fails instead of creating an invalid request

### Requirement: A processing result is a valid immutable terminal snapshot
The system SHALL represent a terminal result with the originating immutable request, `DateTimeOffset` `StartedAtUtc` and `EndedAtUtc` values, non-negative `long` `ProcessedCount`, `UpdatedCount`, `SkippedCount`, and `FailedCount` values, one defined terminal outcome, and nullable failure detail. Both timestamps MUST have zero UTC offset, and `EndedAtUtc` MUST be equal to or later than `StartedAtUtc`.

#### Scenario: Completed result preserves lifecycle facts
- **WHEN** a processing run reaches normal completion
- **THEN** its immutable result retains the originating request, ordered zero-offset UTC timestamps, all four counts, and the `Completed` outcome

#### Scenario: Partial result preserves accumulated facts
- **WHEN** a run is cancelled or fails after some assets reach terminal dispositions
- **THEN** its immutable result retains the counts accumulated before termination and timestamps that bound the execution attempt

#### Scenario: Invalid timestamps or counts are rejected
- **WHEN** result construction receives a non-UTC timestamp, an end before its start, a negative count, or inconsistent aggregate accounting
- **THEN** construction fails instead of creating an invalid result

### Requirement: Processing and update counters have distinct asset semantics
For result accounting, `ProcessedCount` SHALL count assets that reached exactly one terminal per-asset disposition during this run and MUST equal `UpdatedCount + SkippedCount + FailedCount`. `UpdatedCount` SHALL count only successful Immich location writes. `SkippedCount` SHALL count assets actively evaluated in this run but deliberately left without an update because the current processing rules could not produce a writable location. `FailedCount` SHALL count handled per-asset processing exceptions. Previously suppressed asset IDs, assets only fetched or enumerated, and assets interrupted before a terminal per-asset disposition SHALL NOT contribute to these counts.

#### Scenario: Successful write is processed and updated
- **WHEN** an asset's location write completes successfully
- **THEN** the result accounting increments both processed and updated exactly once

#### Scenario: Unresolvable asset is processed and skipped
- **WHEN** an actively evaluated asset reaches a current no-country, no-admin-match, or no-writable-city skip decision
- **THEN** the result accounting increments both processed and skipped exactly once

#### Scenario: Per-asset exception is processed and failed
- **WHEN** an asset operation raises an exception that the processing pass handles and continues past
- **THEN** the result accounting increments both processed and failed exactly once

#### Scenario: Empty run has zero accounting
- **WHEN** eligibility counting finds no assets to process
- **THEN** the completed result reports zero for all four counts

#### Scenario: Suppressed or cancellation-interrupted asset is not processed
- **WHEN** an asset is ignored because its identifier was already suppressed or cancellation interrupts it before a terminal disposition
- **THEN** that asset does not increment processed, updated, skipped, or failed

### Requirement: Terminal outcomes distinguish completion, cancellation, and fatal failure
The system SHALL define exactly three terminal outcomes: `Completed`, `Cancelled`, and `Failed`. An empty pass and a pass containing handled per-asset failures SHALL be `Completed`. Cancellation recognized under the active run token SHALL be `Cancelled` and SHALL NOT itself be counted as an asset failure. An unexpected pass-level failure, including cancellation-like exceptions not attributable to the active requested token, SHALL be `Failed`. A failed result MUST carry a non-blank failure message; completed and cancelled results MUST carry no failure message. A pass-level failed outcome SHALL NOT itself increment `FailedCount` because that count is per-asset.

#### Scenario: Handled asset failures do not fail the pass
- **WHEN** one or more per-asset exceptions are counted and the pass otherwise reaches its normal end
- **THEN** the result is completed with the failed-asset count preserved

#### Scenario: Active-token cancellation terminates the pass
- **WHEN** cancellation requested through the active run token terminates execution
- **THEN** the result is cancelled, has no failure message, and preserves only prior terminal asset dispositions

#### Scenario: Fatal pass failure carries safe detail
- **WHEN** an unexpected pass-level exception terminates execution
- **THEN** the result is failed with a non-blank diagnostic message and does not retain an exception object or stack trace as contract data

#### Scenario: Outcome and failure detail mismatch is rejected
- **WHEN** a failed result has blank failure detail or a completed or cancelled result has any failure detail
- **THEN** construction fails instead of creating an ambiguous terminal result

### Requirement: The foundational models remain transport-neutral and behavior-compatible
Introducing the processing-run models SHALL NOT change processing eligibility, batching, resolution, write-back, skipped-asset persistence, scheduling, admission, cancellation flow, mutable Web UI state, or user-visible counters. The existing Web UI processed counter SHALL continue to mean successful writes and therefore corresponds to `UpdatedCount`, not the new aggregate `ProcessedCount`. The models SHALL NOT define worker protocol versions, envelopes, event sequences, JSON names, serialization attributes, stream framing, process exit codes, mutable status, progress, activity, logs, or run history.

#### Scenario: Existing in-process behavior remains unchanged
- **WHEN** the model-only change is introduced
- **THEN** manual and scheduled processing and all existing Web UI lifecycle observations behave as before

#### Scenario: Future worker transport consumes rather than changes the model
- **WHEN** a later phase defines worker-wire messages
- **THEN** that phase supplies its own versioning and serialization contract without adding wire concerns to these foundational models
