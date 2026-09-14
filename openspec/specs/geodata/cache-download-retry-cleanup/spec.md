# Cache Download Retry Cleanup Specification

## Purpose

Ensures Overture and GADM administrative cache coordination is released after every terminal task outcome without disrupting active shared work or source-specific cache publication.

## Requirements

### Requirement: Terminal source work remains retryable
For both Overture and GADM administrative caches, the system SHALL release a country's completed coordination state when the underlying source task succeeds, faults, or is cancelled. The owned lifetime SHALL include readiness checks and setup performed after the task becomes shared, as well as transfer, export, validation, and publication work.

#### Scenario: Early shared-task failure is retried after repair
- **WHEN** a shared administrative cache task faults during its readiness preflight or setup and the cause is repaired
- **THEN** a later request for the same country starts a new source-specific task rather than receiving the earlier fault

#### Scenario: Cancelled source task is retried
- **WHEN** the underlying shared source task is cancelled before it publishes a cache
- **THEN** a later request for the same country starts a new source-specific task

#### Scenario: Successful task releases coordination
- **WHEN** a source task completes successfully and its published cache is subsequently removed
- **THEN** a later request for the same country can start new source-specific work

### Requirement: Active same-country work remains shared
For each source, the system SHALL allow only one underlying task to own active work for a country and SHALL let concurrent same-country callers await that exact task. Caller-local waiting cancellation SHALL NOT discard underlying work that remains active.

#### Scenario: Concurrent callers join one active task
- **WHEN** multiple callers request the same unavailable country while source work is active
- **THEN** exactly one underlying source operation runs
- **AND** every caller receives the same active task
- **AND** exactly one caller is identified as the starter while the others are identified as awaiters

#### Scenario: One waiter stops waiting
- **WHEN** a caller cancels only its wait for an active shared task
- **THEN** the underlying source task remains registered and active
- **AND** a later same-country caller joins that task rather than starting duplicate work

### Requirement: Stale completion cannot evict replacement work
The system SHALL condition terminal cleanup on the identity of the exact source task that owns the country entry.

#### Scenario: Older cleanup observes newer work
- **WHEN** cleanup associated with an older task runs after a newer task owns the same country
- **THEN** the newer task remains registered and joinable

### Requirement: Ready cache and source-specific cleanup remain compatible
The system SHALL preserve existing valid-cache short circuits, successful publication rules, and each source's temporary-artifact cleanup behavior.

#### Scenario: Existing valid cache
- **WHEN** a valid source-specific country cache is already available
- **THEN** the request returns the ready result without creating or invoking source work

#### Scenario: Overture task terminates before publication
- **WHEN** Overture source work faults or is cancelled after creating its temporary export artifact
- **THEN** the existing Overture temporary-export cleanup behavior is retained
- **AND** no invalid final cache is published

#### Scenario: GADM task terminates before publication
- **WHEN** GADM source work faults or is cancelled after creating its temporary database or package-download artifact
- **THEN** the existing GADM temporary database and package-download cleanup behavior is retained
- **AND** no invalid final cache is published
