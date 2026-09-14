# Processing State Observability Characterization Specification

## Purpose

Defines the UI-visible processing-state contract that later event adapters must preserve for run snapshots, activity, recent logs, and mutation notifications.

## Requirements

### Requirement: Processing state exposes the current run snapshot
When a processing run starts, the system SHALL expose its supplied total, a start time, zero processed/skipped/error counters, and no prior error while retaining the most recent completion time and recent logs. Recorded work SHALL update the matching counter, and each recorded error SHALL expose and log the newest error message.

#### Scenario: New run replaces prior run values
- **WHEN** a processing run starts after prior counter and error activity
- **THEN** the supplied total is exposed, all three counters are zero, the prior error is cleared, a new start time is available, and the prior completion time and recent logs remain observable

#### Scenario: Work and errors update the snapshot
- **WHEN** processed, skipped, and error outcomes are recorded during a run
- **THEN** each matching counter reflects the recorded outcomes and the latest error is exposed in an error log entry

### Requirement: Processing state preserves a terminal run snapshot
When a run completes, the system SHALL expose inactive state and completion timing, clear current activity, and retain that run's total, counters, latest error, and recent logs for observation.

#### Scenario: Completion clears transient activity but retains results
- **WHEN** a run with recorded results and active scoped activity completes
- **THEN** the run is inactive with a completion time and no current activity while its final results and recent logs remain available

### Requirement: Processing state preserves scoped activity visibility
The system SHALL reference-count equal activity labels, SHALL expose a newly begun label as current, and SHALL not allow repeated or post-completion scope disposal to revive cleared activity.

#### Scenario: Equal overlapping scopes end independently
- **WHEN** two scopes with the same label overlap and one scope ends
- **THEN** that label remains current until the final matching scope ends

#### Scenario: Current distinct label ends with one survivor
- **WHEN** one activity scope begins, a distinct scope begins after it, and the later scope ends while the first remains active
- **THEN** the sole remaining label becomes current

#### Scenario: Completion precedes scope disposal
- **WHEN** completion clears active scopes and an earlier scope is then disposed one or more times
- **THEN** no activity becomes current again

### Requirement: Processing state retains bounded ordered recent logs
The system SHALL expose a snapshot of no more than the newest 100 log entries in insertion order.

#### Scenario: Log volume exceeds retention limit
- **WHEN** more than 100 uniquely identifiable log entries are appended
- **THEN** exactly the newest 100 entries remain available in their original insertion order

### Requirement: Processing mutations notify observers
Each observable state mutation SHALL raise at least one change notification; the number of notifications produced by a compound mutation is not part of the contract.

#### Scenario: Observer subscribes before a mutation
- **WHEN** an observer subscribes and a run, counter, error, log, activity, or completion mutation occurs
- **THEN** the observer receives at least one change notification for that mutation
