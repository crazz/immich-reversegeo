# worker-event-state-bridge Specification

## Purpose

Keeps Dashboard and Logs behavior equivalent across in-process and child-worker execution by safely projecting an accepted worker event stream through the existing Web processing-state adapter.

## Requirements

### Requirement: Process readiness is separate from run lifecycle
The controller bridge SHALL be created for exactly one admitted processing request and SHALL consume the launcher session's accepted events. A process-scoped `ready` event SHALL confirm only the worker handshake, SHALL carry no run identity, and SHALL cause no processing-state lifecycle, counter, log, activity, timestamp, error, or notification mutation. The first run-scoped event SHALL be `run-started` for the bridge request; it SHALL claim the already-armed correlation but SHALL NOT determine eligibility or reset visible run state.

#### Scenario: Ready is accepted before execution
- **WHEN** sequence 1 is the accepted process-scoped ready event for a newly launched worker
- **THEN** the bridge acknowledges readiness without starting, resetting, completing, or notifying the UI processing state

#### Scenario: Run starts after readiness
- **WHEN** the next accepted event is run-started for the exact admitted request
- **THEN** the bridge establishes run-event projection while preserving the pending snapshot until eligibility is reported

### Requirement: Projection validates accepted-event correlation before mutation
Before projection, the bridge SHALL verify exact-next sequence, the closed event type and payload pairing, the expected request run ID for every run-scoped event, legal ready/run/eligibility/activity/terminal order, and terminal finality. A stale, mismatched, duplicate, regressive, skipped-sequence, unknown-type, invalid-activity, duplicate-terminal, or post-terminal event SHALL produce a typed bounded projection rejection, SHALL cause no processing-state mutation or notification, and SHALL be handed back through the launcher sink observation boundary for block 30 to classify. The bridge SHALL NOT parse raw stdout or classify the rejection as a crash, malformed-output failure, or fatal run.

#### Scenario: Accepted callback carries another run ID
- **WHEN** a run-scoped callback does not carry the bridge request's exact non-empty run ID
- **THEN** the callback is rejected, no state value or notification changes, and the rejection remains available to later failure classification

#### Scenario: Event repeats or follows terminal
- **WHEN** an event repeats a sequence, duplicates a lifecycle cardinality, or arrives after an accepted terminal
- **THEN** the bridge rejects it without replaying any state mutation or terminal summary

### Requirement: Eligibility and lifecycle preserve the existing state adapter contract
The bridge SHALL map worker run-started and eligibility events into the same transport-neutral lifecycle consumed by in-process execution. Eligibility with a non-negative total SHALL start the visible run, set the supplied total, reset the three visible counters, clear the prior error, retain prior completion/log history, and append the existing zero or nonzero start line. Cancellation or failure before eligibility SHALL reach terminal projection without fabricating a total or new start timestamp. Pending admission and `MarkPending` SHALL remain controller-owned and SHALL NOT be duplicated by ready or run-started projection.

#### Scenario: Eligibility determines an empty run
- **WHEN** the worker reports eligibility zero after run-started
- **THEN** the visible run starts with total zero and reset counters and appends the existing nothing-to-process line

#### Scenario: Worker fails before eligibility
- **WHEN** run-started is followed directly by a coherent failed terminal
- **THEN** the retained pending snapshot reaches the existing failed terminal behavior without a fabricated eligibility total or new start timestamp

### Requirement: Progress and diagnostics project exactly once
For each accepted coherent progress snapshot, the bridge SHALL project `UpdatedCount` to the visible processed counter, `SkippedCount` to skipped, and per-asset `FailedCount` to ordinary errors; aggregate `ProcessedCount` SHALL NOT replace the visible processed value. Projection SHALL use absolute accepted snapshots so replay cannot increment counters. Typed worker log events SHALL retain the block-9 severity, prefix, insertion-order, `LastError`, and newest-100 behavior. An Error diagnostic for a handled asset SHALL set/log the error while its progress snapshot supplies the failed count, with neither path double-counting or duplicating a line.

#### Scenario: One update, two skips, and one handled failure arrive
- **WHEN** the accepted progress snapshot reports aggregate processed four, updated one, skipped two, and failed one
- **THEN** the UI exposes processed one, skipped two, and errors one

#### Scenario: Handled asset failure has a diagnostic and disposition
- **WHEN** one accepted Error log and one accepted progress snapshot describe a handled per-asset failure
- **THEN** the latest error and exactly one error log are visible while the ordinary error counter advances exactly once

### Requirement: Per-asset failure and fatal run failure remain distinct
A failed terminal SHALL preserve the terminal result's per-asset `FailedCount` and SHALL add exactly one separate legacy fatal UI error with `Fatal: <failure detail>`. A cancelled terminal SHALL append only `Run cancelled.` and SHALL not add or replace an error. Completed runs MAY contain handled per-asset failures. Fatal outcome projection SHALL NOT modify the domain result's per-asset counts.

#### Scenario: Fatal follows handled failures
- **WHEN** the last progress snapshot contains two handled failures and the coherent terminal outcome is failed
- **THEN** the domain failed count remains two while the visible error counter becomes three and the newest error is the fatal detail

#### Scenario: Completed run contains handled failure
- **WHEN** a coherent completed terminal follows progress containing one handled per-asset failure
- **THEN** the run completes normally with one visible ordinary error and no added fatal error

### Requirement: Accepted sink delivery is ordered and awaited
The bridge SHALL process one launcher callback at a time in accepted sequence order and SHALL await the complete state-adapter projection before returning callback acceptance. It SHALL introduce no fire-and-forget mutation, event drop, coalescing, replacement, reordering, or second unbounded queue. Backpressure from state projection SHALL propagate to the block-25 stdout pump while that pump continues its independent stderr and process-exit work. An accepted callback SHALL return only after all synchronous observable mutations and their block-9 notifications have occurred, or after one typed rejection/failure is returned.

#### Scenario: State projection is gated
- **WHEN** one event's adapter projection is deliberately held while a later accepted event is available
- **THEN** the later event is not projected first and the earlier sink callback remains incomplete without dropping either event

#### Scenario: Observer receives projected state
- **WHEN** an accepted eligibility, progress, log, activity, or terminal callback mutates observable state
- **THEN** at least one notification occurs after the corresponding value is updated and before that callback returns

### Requirement: Activities preserve identities, labels, and forced cleanup
The bridge SHALL map each non-empty worker activity ID and label to the exact run-correlated activity operations of the state adapter. Distinct IDs with equal labels SHALL remain independently active; matching ends SHALL close only their own ID; duplicate or unknown ends SHALL be rejected before projection. A terminal received while any activity remains open SHALL be rejected without state mutation; valid v1 terminal projection begins only after every activity has ended. If the launcher session or bridge is disposed without an accepted terminal, the bridge SHALL force idempotent cleanup of its run's projected activity scopes, SHALL prevent late disposal from affecting another run, and SHALL NOT synthesize a terminal outcome or summary.

#### Scenario: Equal labels overlap
- **WHEN** two accepted starts use the same label and different activity IDs and one ID ends
- **THEN** the label remains visible until the other activity ends or forced cleanup occurs

#### Scenario: Bridge closes without terminal
- **WHEN** bridge disposal occurs with projected activities still open and no terminal accepted
- **THEN** those activities are cleared exactly once without completing or failing the processing run

### Requirement: Terminal payload is cross-checked before authoritative projection
Before terminal projection, the bridge SHALL reconstruct the transport-neutral result and verify that its request identity and trigger match the admitted request, its outcome matches the terminal event type, its UTC timestamps and failure-detail rules remain valid, and its counts are coherent with both `ProcessedCount = UpdatedCount + SkippedCount + FailedCount` and the latest accepted progress snapshot, or all zero when terminal legally precedes eligibility/progress. A mismatch SHALL be rejected with no completion mutation for block 30 handoff. A coherent terminal SHALL be projected exactly once through the block-9 adapter, which SHALL close activities, apply cancellation or fatal behavior, mark state inactive, append the unchanged final summary, and release run ownership in its existing order.

#### Scenario: Terminal counters contradict progress
- **WHEN** a terminal result's counts differ from the latest accepted progress snapshot
- **THEN** terminal projection is rejected and no completion timestamp, summary, fatal increment, or ownership release is produced

#### Scenario: Coherent completion is accepted
- **WHEN** the completed terminal matches the admitted request, completed outcome, timestamps, and final progress counts
- **THEN** the state adapter completes once and appends the unchanged final summary after completion

### Requirement: Bridge disposal preserves later failure-policy boundaries
Bridge disposal SHALL be idempotent and SHALL suppress later callbacks, await any projection already accepted by the bridge, and force only bridge-owned activity cleanup. Disposal before terminal SHALL expose a bounded nonterminal session observation for block 30 and SHALL NOT fabricate failed/cancelled completion, append crash diagnostics, interpret exit codes or stderr, retry the run, or redesign processing state. PID and run/job identity SHALL remain launcher/bridge control-plane data and SHALL not become public UI-state fields in this change.

#### Scenario: Session is disposed repeatedly after terminal
- **WHEN** the bridge is disposed more than once after a coherent terminal was projected
- **THEN** no additional activity disposal, completion, summary, error, or notification is produced

#### Scenario: Session ends without terminal
- **WHEN** the launcher reaches finality or disposal suppresses callbacks before a terminal is accepted
- **THEN** the bridge reports nonterminal finality for later classification while leaving run failure presentation outside this change

## Audit Reconciliation

A terminal received while this bridge has any open projected activity is a typed terminal-coherence rejection, not an instruction to close activities. Only a coherent accepted terminal performs normal terminal cleanup. Forced activity cleanup is limited to nonterminal bridge/session abandonment. A terminal that follows eligibility but no accepted progress is coherent only when all four result counts (`ProcessedCount`, `UpdatedCount`, `SkippedCount`, and `FailedCount`) are zero; eligibility alone never permits nonzero counts.

