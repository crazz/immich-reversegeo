## Purpose

Defines the observable processing lifecycle and single-run arbitration behavior that orchestration changes must preserve across later executor, coordinator, and worker boundaries.

## ADDED Requirements

### Requirement: Admitted processing exposes pending and active execution
An admitted processing run SHALL be observable as running immediately after it owns execution and SHALL record its start only after eligibility evaluation completes.

#### Scenario: Run is pending during eligibility evaluation
- **WHEN** an admitted run is waiting for eligibility evaluation to complete
- **THEN** the run is observable as running without a start timestamp

#### Scenario: Run enters active execution
- **WHEN** eligibility evaluation completes
- **THEN** the run remains observable as running and has a start timestamp

### Requirement: Processing pass exposes terminal cleanup
A processing pass that has entered active execution SHALL become inactive and record completion after success, manual cancellation, or pass-level failure.

#### Scenario: Successful processing pass
- **WHEN** an active processing pass finishes without cancellation or pass-level failure
- **THEN** the run is inactive with a completion timestamp and a completion summary

#### Scenario: Manually cancelled processing pass
- **WHEN** manual cancellation is requested while a manually admitted processing pass is active at a cancellable operation boundary
- **THEN** the run is inactive with a completion timestamp and a cancellation log entry
- **AND** cancellation is not exposed as an ordinary processing error

#### Scenario: Processing pass fails
- **WHEN** an active processing pass encounters an exception handled by the pass-level failure boundary
- **THEN** the run is inactive with a completion timestamp
- **AND** the failure is exposed as an error and fatal log entry before the completion summary

### Requirement: Processing admissions are mutually exclusive
The system SHALL admit only one processing pass at a time and SHALL preserve the current trigger-specific contention feedback.

#### Scenario: Duplicate manual trigger
- **WHEN** a manual trigger arrives while another run owns processing execution
- **THEN** no second processing pass starts, the rejected attempt does not mark pending or alter the owning run's start timestamp, and no contention log entry is added

#### Scenario: Duplicate scheduled admission
- **WHEN** scheduled work is admitted while another run owns processing execution
- **THEN** no second processing pass starts, the rejected attempt does not mark pending or alter the owning run's start timestamp, and exactly `Scheduled run skipped because a processing pass is already in progress.` is added

### Requirement: Processing ownership is released after terminal cleanup
After success, manual cancellation, or pass-level failure has completed terminal cleanup, processing ownership SHALL be available to later manual and scheduled admissions.

#### Scenario: Manual trigger after terminal cleanup
- **WHEN** a prior processing pass has completed terminal cleanup
- **THEN** a subsequent manual trigger can start a new processing pass

#### Scenario: Scheduled admission after terminal cleanup
- **WHEN** a prior processing pass has completed terminal cleanup
- **THEN** subsequent scheduled work can start a new processing pass
