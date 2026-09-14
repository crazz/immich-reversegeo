## MODIFIED Requirements

### Requirement: Projection validates accepted-event correlation before mutation
Before projection, the bridge SHALL verify exact-next sequence, the closed event type and payload pairing, the expected request run ID for every run-scoped event, legal ready/run/eligibility/activity/terminal order, and terminal finality. A stale, mismatched, duplicate, regressive, skipped-sequence, unknown-type, invalid-activity, duplicate-terminal, or post-terminal event SHALL produce a typed bounded projection rejection, SHALL cause no processing-state mutation or notification, and SHALL be handed back through the launcher sink observation boundary for block 30 to classify. Semantic rejection SHALL retain its typed failure detail and MUST NOT retain a retryable terminal candidate. A terminal that passed Preview but was definitely rejected before a receipt claim SHALL instead expose its exact validated result as noncommitted evidence. An indeterminate projection response SHALL remain distinct and SHALL be resolved only by querying the exact-request receipt. The bridge SHALL NOT parse raw stdout or classify the rejection as a crash, malformed-output failure, or fatal run.

#### Scenario: Accepted callback carries another run ID
- **WHEN** a run-scoped callback does not carry the bridge request's exact non-empty run ID
- **THEN** the callback is rejected, no state value or notification changes, and the rejection remains available to later failure classification

#### Scenario: Event repeats or follows terminal
- **WHEN** an event repeats a sequence, duplicates a lifecycle cardinality, or arrives after an accepted terminal
- **THEN** the bridge rejects it without replaying any state mutation or terminal summary

#### Scenario: Semantic rejection is not replay authority
- **WHEN** a terminal is rejected for correlation, progress, lifecycle, or activity inconsistency
- **THEN** later classification never resubmits the session raw terminal as a valid UI result

### Requirement: Terminal payload is cross-checked before authoritative projection
Before terminal projection, the bridge SHALL reconstruct the transport-neutral result and verify that its request identity and trigger match the admitted request, its outcome matches the terminal event type, its UTC timestamps and failure-detail rules remain valid, and its counts are coherent with both `ProcessedCount = UpdatedCount + SkippedCount + FailedCount` and the latest accepted progress snapshot, or all zero when terminal legally precedes eligibility/progress. A mismatch SHALL be rejected with no completion mutation for block 30 handoff. A coherent terminal SHALL enter the same exact-request finalization gate used by abnormal child finalization. The adapter SHALL publish an immutable in-memory receipt before any terminal callback or observable mutation; that receipt remains queryable until another exact request is armed. A recorded winner SHALL preserve its outcome, counts, completion timestamp, fatal accounting, and single summary even if a callback throws. Normal terminal commitment SHALL occur during event projection without waiting for process exit. A coherent terminal SHALL be projected exactly once through the block-9 adapter, which SHALL close activities, apply cancellation or fatal behavior, mark state inactive, append the unchanged final summary, and release run ownership in its existing order.

#### Scenario: Terminal counters contradict progress
- **WHEN** a terminal result's counts differ from the latest accepted progress snapshot
- **THEN** terminal projection is rejected and no completion timestamp, summary, fatal increment, or ownership release is produced

#### Scenario: Coherent completion is accepted
- **WHEN** the completed terminal matches the admitted request, completed outcome, timestamps, and final progress counts
- **THEN** the state adapter completes once and appends the unchanged final summary after completion

#### Scenario: Terminal callback response is indeterminate
- **WHEN** a callback throws after the finalization receipt was recorded
- **THEN** the bridge exposes indeterminate response evidence, the receipt remains authoritative, and finalization never changes the recorded outcome or repeats its summary

#### Scenario: Normal and abnormal projection compete
- **WHEN** normal terminal projection and control-plane finalization target the same exact request
- **THEN** the one receipt gate records a single winner before terminal mutation and every later attempt observes that winner without additional mutation
