## Purpose

Records when a separate periodic eligibility-reconciliation cadence is rejected because every ordinary scheduled check already observes the full current eligibility set.

## ADDED Requirements

### Requirement: Scheduled checks retain one full-eligibility path
While no watermark source has passed the block 61 safety gate, the system SHALL retain its existing single schedule and SHALL evaluate the complete current eligibility predicate on every scheduled check. It SHALL NOT add a separate reconciliation cadence, trigger path, or persisted reconciliation state.

#### Scenario: An ordinary scheduled check occurs
- **WHEN** the existing schedule starts a check while no approved watermark is in use
- **THEN** that check uses the full current eligibility observation rather than a watermarked tail or a separate reconciliation path

#### Scenario: A second cadence is considered
- **WHEN** a daily, weekly, or other reconciliation trigger is proposed while scheduled checks already use full eligibility
- **THEN** the trigger is rejected because it duplicates the existing correctness path

### Requirement: Rejected reconciliation adds no user or operational surface
The withdrawn feature SHALL add no reconciliation-specific configuration, cadence selection, settings or dashboard control, activity or log classification, persisted state, or processing mode. Existing manual and scheduled behavior SHALL remain unchanged.

#### Scenario: Configuration and user surfaces are inspected
- **WHEN** the no-go decision is verified
- **THEN** no reconciliation-specific option or status is present and the existing schedule remains the only scheduling surface

#### Scenario: Processing activity is observed
- **WHEN** a scheduled or manual pass reports its existing activity and outcome
- **THEN** no reconciliation-specific activity type, log outcome, or dashboard semantic is introduced

### Requirement: Future reconsideration remains evidence-gated
A separate reconciliation feature SHALL remain withdrawn unless a future watermark proposal first passes block 61's zero-false-negative, compatibility, and operational evidence gate. Any later reconciliation design SHALL be introduced through a new or revised proposal and SHALL NOT rely on this rejected design as implementation authority.

#### Scenario: No watermark has passed the gate
- **WHEN** no new evidence changes block 61's no-watermark decision
- **THEN** the single full-eligibility schedule remains authoritative and no reconciliation work proceeds

#### Scenario: A future watermark passes the gate
- **WHEN** a future proposal proves an eligible watermark source under block 61's revisit criteria
- **THEN** reconciliation may be evaluated in that proposal's actual architecture rather than reviving the stale daily-or-weekly assumptions from block 63
