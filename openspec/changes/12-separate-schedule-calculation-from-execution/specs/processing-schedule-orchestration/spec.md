## Purpose

Provides deterministic UTC schedule calculation, waiting, and scheduled-trigger generation while keeping processing admission, eligibility, and execution outside the scheduler.

## ADDED Requirements

### Requirement: Schedule evaluation is deterministic and UTC-only
The system SHALL evaluate each schedule snapshot from an explicit zero-offset UTC instant. Enabled expressions SHALL use standard five-field cron syntax and UTC calendar rules, and the next occurrence SHALL be strictly later than the evaluation instant. The system SHALL NOT interpret a schedule in the host's local time zone or apply local daylight-saving rules.

#### Scenario: Valid enabled expression is evaluated
- **WHEN** an enabled valid five-field cron expression is evaluated at a supplied UTC instant
- **THEN** the system produces the first matching UTC occurrence strictly after that instant

#### Scenario: Host local time zone differs from UTC
- **WHEN** the same enabled expression and UTC instant are evaluated on hosts with different local time zones
- **THEN** the calculated due occurrence is identical and has zero UTC offset

#### Scenario: Expression has no valid next occurrence
- **WHEN** an enabled expression is invalid or cannot produce a next occurrence
- **THEN** the system produces an invalid-schedule retry plan and no scheduled trigger

### Requirement: Disabled and invalid schedules retain bounded retry behavior
A disabled schedule SHALL wait one minute before reading configuration again. An enabled schedule with an invalid expression or no next occurrence SHALL wait five minutes before reading configuration again. Neither plan SHALL emit a scheduled trigger or a next-run log line.

#### Scenario: Schedule is disabled
- **WHEN** a configuration snapshot has scheduling disabled
- **THEN** the scheduler waits one minute, emits no trigger, and then reads a fresh configuration snapshot

#### Scenario: Schedule expression is invalid
- **WHEN** a configuration snapshot enables an invalid cron expression
- **THEN** the scheduler waits five minutes, emits no trigger, and then reads a fresh configuration snapshot

#### Scenario: Shutdown occurs during retry wait
- **WHEN** host shutdown cancels a disabled or invalid retry wait
- **THEN** the wait ends cooperatively without emitting a scheduled trigger or treating shutdown as an ordinary scheduling failure

### Requirement: A valid due occurrence produces one scheduled trigger
For a valid enabled schedule, the scheduler SHALL publish the existing next-run UI log before a positive wait, wait until the calculated relative due delay completes, and then emit exactly one trigger with Scheduled origin. Schedule calculation and waiting SHALL NOT acquire admission, mark pending state, create a run request, count eligible assets, or execute processing.

#### Scenario: Future occurrence becomes due
- **WHEN** a valid occurrence is in the future and its cancellable wait completes
- **THEN** the scheduler emits exactly one Scheduled trigger for that occurrence

#### Scenario: Clock has advanced past the calculated occurrence before waiting
- **WHEN** the calculated due instant is not later than the clock instant used to begin the wait
- **THEN** the scheduler skips the log and positive delay and emits one Scheduled trigger without adding a second occurrence

#### Scenario: Shutdown occurs before occurrence
- **WHEN** host shutdown cancels the due wait before completion
- **THEN** no scheduled trigger is emitted for that occurrence

### Requirement: Next-run visibility remains log-based and UTC
For each valid occurrence that requires a positive wait, the system SHALL append exactly one existing-format `Next run scheduled at <UTC universal-sortable value>` line to the UI-visible processing log before waiting. The change SHALL NOT add or persist a separate next-run state value.

#### Scenario: Positive due wait is planned
- **WHEN** a valid due occurrence is later than the wait-start instant
- **THEN** one UTC next-run line is visible before the wait begins

#### Scenario: Disabled, invalid, or already-due plan is evaluated
- **WHEN** schedule evaluation does not require a positive due wait
- **THEN** no next-run line is appended for that evaluation

### Requirement: Configuration changes take effect at existing reevaluation points
The scheduler SHALL read a fresh persisted schedule snapshot at the start of each loop iteration. A save during an active one-minute, five-minute, or valid-occurrence wait SHALL NOT interrupt or replace that wait; the new enabled flag or cron text SHALL take effect after the current wait and any due trigger handling complete. Schedule editor parsing, preset generation, persisted fields, and save behavior SHALL remain unchanged.

#### Scenario: Configuration changes during disabled retry
- **WHEN** scheduling is enabled while the one-minute disabled wait is active
- **THEN** the current wait completes before the newly saved schedule is evaluated

#### Scenario: Configuration changes during valid occurrence wait
- **WHEN** the enabled flag or cron expression is saved while a valid occurrence wait is active
- **THEN** the already planned occurrence remains the one trigger considered before the scheduler reads the new snapshot

#### Scenario: Preset or custom cron is saved
- **WHEN** the existing schedule editor saves either generated preset text or custom cron text
- **THEN** the runtime consumes that persisted five-field text without changing or adding schedule fields

### Requirement: Startup and hosted-service lifecycle remain compatible
The hosted singleton SHALL complete skipped-storage initialization, append the existing service-started UI log, and only then read the first schedule snapshot. Startup SHALL NOT synthesize an immediate run; a valid cron plan begins with the first occurrence strictly after evaluation. Host shutdown SHALL cancel schedule waits and any awaited scheduled execution through the hosted stopping token. The concrete hosted service identity and existing manual Dashboard surface SHALL remain available.

#### Scenario: Hosted service starts
- **WHEN** the hosted service begins normally
- **THEN** initialization completes before the service-started log and before schedule evaluation, and no startup-only trigger is emitted

#### Scenario: Accepted scheduled run is in progress during shutdown
- **WHEN** host shutdown cancels the stopping token while the scheduler is awaiting an accepted scheduled run
- **THEN** the token reaches the existing run-control/executor path and the hosted loop does not start another occurrence

#### Scenario: Dashboard triggers a manual run
- **WHEN** the existing Run Now or Cancel command is used during this change
- **THEN** its characterized admission, pending, cancellation, and terminal behavior remains unchanged and does not depend on schedule calculation

### Requirement: Admission and execution remain downstream of scheduling
The scheduled-trigger boundary SHALL report whether the due trigger was rejected because another pass is active or was accepted. A rejection SHALL preserve the existing scheduled-contention UI log and SHALL invoke the executor zero times. An accepted call SHALL preserve immediate pending visibility and SHALL not complete back to the schedule loop until that scheduled run reaches a terminal path, so occurrences during its execution are not replayed. The downstream run-control path, not the scheduler, SHALL own the lock, request identity, reporter arming, cancellation source policy, executor invocation, and terminal release. A temporary adapter SHALL NOT be DI-registered with a `ProcessingBackgroundService` back-edge while that hosted service consumes the scheduler boundary; it must be direct host implementation or a private non-DI delegate adapter.

#### Scenario: Due trigger is rejected
- **WHEN** a due Scheduled trigger reaches admission while another pass owns admission
- **THEN** the scheduler records the existing skipped-because-in-progress line and performs no execution work

#### Scenario: Due trigger is accepted
- **WHEN** a due Scheduled trigger is admitted
- **THEN** downstream run control marks pending and performs one execution, and the scheduler awaits its terminal completion before reevaluating configuration

#### Scenario: Run control is replaced by block 13 coordinator
- **WHEN** the temporary adapter is replaced with a coordinator implementing the same scheduler-facing contract
- **THEN** schedule calculation, waiting, trigger origin, visibility, and reevaluation behavior remain unchanged

### Requirement: The executor owns authoritative eligibility and processing facts
Every accepted scheduled trigger SHALL reach block 11's executor, whose exact count and eligibility event remain authoritative, including an empty pass. The scheduler SHALL NOT query or infer eligibility, asset counts, skipped IDs, batches, processing configuration, geodata, or persistence state. Any later lightweight preflight SHALL be advisory downstream of schedule generation and SHALL NOT replace, publish, or modify the executor's exact count.

#### Scenario: Accepted trigger has no eligible assets
- **WHEN** a scheduled trigger is accepted and the executor's authoritative count is zero
- **THEN** the executor preserves the characterized empty-pass lifecycle without a scheduler-owned preflight suppressing it

#### Scenario: Future lightweight preflight disagrees with execution count
- **WHEN** a later advisory preflight and the executor observe different database states
- **THEN** only the executor's exact count is published as eligibility and used for run accounting
