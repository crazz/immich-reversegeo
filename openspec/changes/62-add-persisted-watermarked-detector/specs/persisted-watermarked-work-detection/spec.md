## Purpose

Defines the evidence gate that keeps persisted watermarked work detection absent when no source proves complete, commit-ordered eligibility coverage.

## ADDED Requirements

### Requirement: No-go preserves full-eligibility detection
While the finalized change 61 decision selects no watermark, the system SHALL continue using change 58's exact full-eligibility `EXISTS` detector for frequent work checks and SHALL NOT introduce persisted incremental detection.

#### Scenario: Current no-watermark decision governs block 62
- **WHEN** block 62 is evaluated against the finalized change 61 evidence
- **THEN** the persisted watermarked detector SHALL remain unimplemented and the change 58 `EXISTS` behavior SHALL remain unchanged

### Requirement: Rejected change is runtime-neutral
Applying this change under the current no-go decision SHALL be limited to decision verification and removal of stale planning assumptions. It SHALL NOT add a cursor file or other detector state, an incremental or tail query, cursor advancement, corruption fallback, schema object, trigger, listener, replication slot, detector implementation, dependency-injection registration, configuration, or implementation test that pretends the rejected detector exists.

#### Scenario: Apply is requested without passing evidence
- **WHEN** this change is applied while change 61 still selects no watermark
- **THEN** no source, database, configuration, dependency-injection, test, or runtime behavior SHALL be introduced or changed

### Requirement: Reopening requires a revised evidence decision
The no-go status SHALL remain in force unless new or revised evidence satisfies every change 61 revisit criterion and block 62's proposal, design, specification, and tasks are explicitly revised before any implementation is authorized. Reconciliation, overlap windows, deduplication, or an observed low miss rate SHALL NOT satisfy this gate.

#### Scenario: Proposed evidence is incomplete
- **WHEN** a future source lacks any required compatibility, zero-miss mutation, inverse-commit, restart/replay/corruption, schema-drift, multi-container, bounded-cost, or source-specific operational evidence from change 61
- **THEN** block 62 SHALL remain no-go and full-eligibility `EXISTS` detection SHALL be preserved

#### Scenario: New evidence satisfies the gate
- **WHEN** a new or revised evidence review satisfies every change 61 revisit criterion
- **THEN** block 62 SHALL remain unimplemented until its artifacts are explicitly revised to define the newly approved source and safeguards
