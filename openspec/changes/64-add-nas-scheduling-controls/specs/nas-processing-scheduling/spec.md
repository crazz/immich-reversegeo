## Purpose

Records why NAS-specific umbrella scheduling controls remain absent when existing schedule and deployment-mode contracts already cover valid operation without a watermark or reconciliation path.

## ADDED Requirements

### Requirement: Existing schedule behavior remains authoritative
While block 61 selects no watermark and blocks 62–63 remain no-go, the system SHALL retain the existing single schedule. Enabled scheduling SHALL continue to support hourly, minute/hour interval, daily, weekly, and custom five-part cron behavior through the existing schedule editor; disabled scheduling SHALL continue to suppress automatic runs while Dashboard manual runs remain available.

#### Scenario: Existing schedule is enabled
- **WHEN** an operator saves an existing preset or custom cron with automatic scheduling enabled
- **THEN** Standard scheduling continues to use that one schedule without a NAS-specific processing mode

#### Scenario: Existing schedule is disabled
- **WHEN** an operator disables automatic scheduling
- **THEN** no automatic scheduled run is originated and the existing Dashboard manual-run contract remains available

### Requirement: Deployment modes remain separate from schedule preferences
Standard, Web-only, and Run-once SHALL retain their separately owned deployment contracts. Standard SHALL own internal scheduling, Web-only SHALL structurally suppress internal scheduling without rewriting saved schedule settings and SHALL preserve manual Dashboard processing, and Run-once SHALL remain the separate one-shot contract for an external scheduler. Block 64 SHALL NOT duplicate these choices as saved NAS scheduling modes.

#### Scenario: Web-only has an enabled saved schedule
- **WHEN** the process runs in Web-only with automatic scheduling enabled in saved settings
- **THEN** internal scheduling remains suppressed, the saved schedule remains unchanged, and manual Dashboard processing remains governed by the Web-only contract

#### Scenario: External scheduler invokes Run-once
- **WHEN** an external scheduler starts a separately configured Run-once process
- **THEN** that process follows the Run-once one-attempt contract rather than a block 64 setting or reconciliation mode

### Requirement: Scheduled checks remain full-eligibility checks
Every scheduled check SHALL continue to use block 58's exact full-eligibility `EXISTS` observation while no watermark has passed block 61's gate. The system SHALL NOT add frequent-watermark, cursor, reconciliation cadence, or catch-up controls under block 64.

#### Scenario: Standard reaches a scheduled check
- **WHEN** the existing Standard schedule reaches a due occurrence while the no-watermark decision remains in force
- **THEN** the check evaluates the complete current eligibility predicate rather than a tail, watermark, or separate reconciliation path

#### Scenario: A NAS-specific optimization is proposed
- **WHEN** a proposed control depends on frequent incremental checks or periodic repair of missed eligibility
- **THEN** it is rejected because its prerequisite architecture did not pass block 61 and was withdrawn by blocks 62–63

### Requirement: Rejected controls are runtime-neutral
Applying this no-go decision SHALL NOT change schedule configuration or persistence, defaults, migration behavior, schedule-editor parsing or copy, Settings or Dashboard controls, runtime scheduling, manual processing, deployment-mode behavior, tests for invented settings, or public documentation. Any genuinely needed deployment-mode documentation clarification SHALL remain in block 70 rather than create a block 64 setting.

#### Scenario: Block 64 is applied under current evidence
- **WHEN** the planning decision is verified without new evidence satisfying block 61
- **THEN** no runtime, test, configuration, migration, copy, or documentation file is changed

### Requirement: Reconsideration remains evidence-gated
NAS-specific scheduling controls SHALL remain withdrawn unless a new or revised watermark proposal satisfies every block 61 compatibility, zero-miss, commit-order, recovery, schema-drift, coordination, bounded-cost, and source-specific operational criterion. Block 64 artifacts MUST then be explicitly revised for the approved architecture before implementation.

#### Scenario: Reopen evidence is incomplete
- **WHEN** a future proposal relies on reconciliation, overlap, deduplication, or observed low miss rates instead of satisfying every block 61 criterion
- **THEN** block 64 remains no-go and the existing full-eligibility schedule remains authoritative
