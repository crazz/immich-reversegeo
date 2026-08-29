## Purpose

Lets a user start one processing pass in a temporary child worker while the Dashboard retains its existing admission, live feedback, Stop, terminal status, and retrigger behavior.

## ADDED Requirements

### Requirement: Manual admission remains backend-agnostic
The system SHALL route the Dashboard manual Run action through the shared processing coordinator rather than through a backend-specific UI path. An accepted request SHALL publish one new non-empty run ID and its live cancellation owner before pending is visible, then mark pending, arm reporting for the same request, resolve the internally selected child-worker backend, and establish exactly one dispatch in that order. The accepted call SHALL return after dispatch ownership is established, not after run completion.

#### Scenario: Manual child request is accepted
- **WHEN** the user invokes Run while the internal child-worker selection is active and no run owns local admission
- **THEN** one manual request, run ID, cancellation owner, pending transition, matching reporter arm, and child dispatch are established in coordinator order

#### Scenario: Stop races the pending transition
- **WHEN** the user invokes Stop as soon as an accepted manual request becomes pending
- **THEN** Stop targets that exact accepted run and is not lost or directed to an earlier run

#### Scenario: Duplicate manual trigger is rejected
- **WHEN** another manual Run action occurs while the first request still owns admission or child finality cleanup
- **THEN** the duplicate remains silent and creates no run ID, pending transition, reporter arm, backend resolution, child process, run event, or in-process work

### Requirement: Child lifecycle and events preserve Dashboard state
The selected child backend SHALL carry the exact admitted request and run ID through command resolution, process start, readiness, execute request, typed event validation, state projection, and normalized result. Readiness SHALL NOT mutate processing state; run-started SHALL establish correlation without resetting counters; eligibility SHALL start or reset the visible run; and accepted progress, activity, diagnostic, and terminal events SHALL be projected in validated order through the existing processing state. The Dashboard SHALL remain unaware of process, protocol, backend-selection, and classification details.

#### Scenario: Child reports an active run
- **WHEN** the child becomes ready, accepts the execute request, and emits valid run-started, eligibility, progress, activity, and log events for the admitted run
- **THEN** the Dashboard moves from pending to active and displays compatible totals, counters, activity, and log updates without duplicate state mutations

#### Scenario: Manual run has no eligible work
- **WHEN** an accepted manual child reports zero eligibility and a valid completed terminal
- **THEN** the Dashboard shows the existing no-work start/status and zero-count completion behavior, emits one terminal summary, and becomes idle

#### Scenario: Child completes successfully
- **WHEN** the child emits valid progress and a completed terminal and later reaches process and stream finality
- **THEN** the committed completion remains authoritative, user-visible state and logs retain their compatible successful-run semantics, and ownership is released only after final cleanup

### Requirement: Child failures produce one compatible failed run
An admitted manual child run SHALL use the finalized evidence classifier and finalization gate for command-resolution failure, operating-system start failure, readiness timeout or pre-ready exit, execute write or flush failure, malformed, oversized, unknown, incompatible, mis-sequenced, or wrong-run protocol input, bridge or projection failure, post-ready crash or missing terminal, mapped or unmapped raw exit evidence, and process cleanup failure. Without an already committed terminal, these conditions SHALL produce one bounded safe failed-run projection. A valid committed terminal SHALL remain authoritative and later contradictory transport evidence SHALL add at most one safe anomaly rather than rewriting the terminal.

#### Scenario: Child cannot start or become ready
- **WHEN** command resolution or process start fails, or readiness times out or ends before valid readiness
- **THEN** the accepted run becomes failed exactly once, displays a bounded phase-specific error, leaves no activity, and releases only its matching handle after owned cleanup

#### Scenario: Protocol fails after start
- **WHEN** the child emits invalid protocol data or exits after readiness without a committed terminal
- **THEN** invalid callbacks mutate no further processing state and the run becomes failed exactly once after process and stream finality

#### Scenario: Worker reports busy
- **WHEN** the child commits the finalized failed terminal for advisory-lock contention and exits with the reserved busy evidence
- **THEN** the Dashboard retains the existing failed-terminal experience, starts no retry or replacement process, and does not treat the raw exit value alone as a successful or non-error outcome

#### Scenario: Terminal conflicts with later process evidence
- **WHEN** a completed, cancelled, or failed terminal was committed before contradictory exit, output, or disposal evidence becomes final
- **THEN** the committed terminal remains unchanged and at most one bounded supplementary anomaly is added

### Requirement: Stop controls the exact child session
The existing Dashboard Stop action SHALL cancel whichever local run owns admission. For an accepted manual child it SHALL translate the coordinator token into at most one correlated cancel command after the execute request is written and flushed, retain ownership through one monotonic `TimeProvider` deadline whose production default is exactly 10 seconds from the first accepted Stop and which readiness, execute flush, repeated Stop, or caller wait cancellation never resets, and continue process-exit and stream drainage. Cancellation of a wait, raw exit 130, EOF, or process termination alone SHALL NOT prove a cancelled outcome.

#### Scenario: Cooperative manual cancellation
- **WHEN** Stop targets the active manual child and the worker commits a cancelled terminal before exiting
- **THEN** the Dashboard records one cancelled completion without an ordinary fatal error and the matching run remains owned until full child finality

#### Scenario: Stop requires forced termination
- **WHEN** the exact stopped child does not exit before the shared grace deadline and whole-tree kill is accepted
- **THEN** the run becomes cancelled once with a bounded forced-termination warning after exit and stream finality

#### Scenario: Forced termination cannot settle ownership
- **WHEN** tree termination fails or process ownership cannot yet be settled
- **THEN** the run is not released early, the eventual outcome is failed when evidence permits finalization, and no replacement run begins meanwhile

### Requirement: Terminal cleanup and retrigger are exactly once
For every accepted manual child outcome, normal terminal projection and abnormal classification SHALL share one linearizable finalization gate. The winner SHALL perform the sole terminal state mutation and summary, close all run-owned activities and callbacks, finish launcher, cancellation, stream, process, and scope cleanup, and then release only the matching coordinator handle. Late, duplicate, stale, cross-run, and post-finality events SHALL NOT mutate state or affect a replacement run. A later manual request SHALL be eligible only after matching cleanup completes.

#### Scenario: Successful run is retriggered
- **WHEN** a completed manual child has reached state, process, stream, scope, and coordinator cleanup
- **THEN** a later manual Run action can be accepted with a different non-empty run ID

#### Scenario: Failure is retriggered
- **WHEN** a start, protocol, crash, timeout, cancellation, or cleanup failure has reached matching finality and released admission
- **THEN** a later manual Run action can be accepted without inheriting prior cancellation, activities, callbacks, terminal receipts, or backend scope

#### Scenario: Late event follows cleanup
- **WHEN** an event for the finalized run arrives after callback closure or after a replacement run is admitted
- **THEN** it is rejected or ignored without changing the retained terminal snapshot or the replacement run

### Requirement: Manual transition has no fallback or public mode
For this numbered transition the Web production default SHALL remain unchanged, while authorized internal composition and focused tests MAY explicitly select the child-worker backend for manual-path verification. Child selection SHALL instantiate only the child backend graph for the admitted run and SHALL NOT resolve or execute the in-process executor or Web geodata graph. Any child resolution, start, protocol, cancellation, classification, or cleanup failure SHALL remain the outcome of that run without retry, replacement child, or in-process fallback. Backend selection SHALL NOT appear in settings, environment variables, command-line deployment modes, public APIs, or the Dashboard, and scheduled-run routing SHALL remain outside this capability.

#### Scenario: Internal manual transition selects child
- **WHEN** an authorized internal composition or focused test selects child-worker and admits a manual request
- **THEN** only the child backend is resolved and the Dashboard follows the same manual coordinator contract

#### Scenario: Child execution fails
- **WHEN** any selected child path fails before or after execute acceptance
- **THEN** no in-process executor, Web processing geodata dependency, fallback, retry, or replacement child is invoked

#### Scenario: User inspects application controls
- **WHEN** the application runs with this change
- **THEN** no public backend selector or mode toggle is available and no scheduled trigger behavior is changed

## Audit Reconciliation

Block 26 is a prerequisite for deterministic real-worker fixture coverage. The manual request uses one exact `Guid` identity whose canonical wire representation is preserved unchanged through child launch, events, bridge, cancellation, and finality. It consumes the internal exact 10-second `TimeProvider` cancellation policy without adding a public setting. UI `Processed` is projected from `UpdatedCount`, never aggregate `ProcessedCount`.

