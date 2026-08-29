## Purpose

Watermark source selection prevents silent processing omissions by requiring proof that an incremental signal covers every transition into eligibility across supported Immich schemas and transaction schedules.

## ADDED Requirements

### Requirement: Evidence record covers every candidate and eligibility transition
The research outcome SHALL identify the inspected repository revision, commit-pinned Immich versions or revisions, relevant schema objects and indexes, mutation semantics, ordering semantics, transaction behavior, and compatibility limits for every candidate considered. It SHALL evaluate inserts, delayed EXIF creation, GPS additions/corrections/clears, metadata clears, ReverseGeo writes, current and future overwrite eligibility, soft delete/restore, hard delete/recreate, migration backfills, restores, timezone/precision/ties, concurrent transactions, multi-container use, and schema-version drift.

#### Scenario: Candidate evidence is incomplete
- **WHEN** any relevant eligibility transition, supported schema, index, ordering property, or transaction schedule lacks durable evidence
- **THEN** that candidate SHALL be recorded as unproven rather than inferred safe

#### Scenario: Equal values have a secondary key
- **WHEN** a candidate can produce equal timestamp or identifier values
- **THEN** the evidence SHALL distinguish deterministic pagination from proof of commit-ordered, no-loss watermark advancement

### Requirement: A selected watermark has no false-negative schedule
A candidate SHALL pass only if durable evidence and reproducible tests prove that every transaction capable of making an asset eligible emits a durable, strictly commit-ordered observation, including inserts, updates, clears, restores, and deletes where relevant, and that advancement cannot overtake an earlier uncommitted observation.

#### Scenario: Scalar is assigned before commit
- **WHEN** one transaction receives a lower scalar and commits after another transaction with a higher scalar has been observed
- **THEN** the scalar candidate SHALL fail even if tuple ordering, an overlap window, idempotence, or periodic reconciliation reduces practical risk

#### Scenario: Source is a durable committed-change feed
- **WHEN** a future candidate provides commit-ordered INSERT, UPDATE, and DELETE observations with durable replay, atomic bootstrap, idempotent consumption, and acknowledgment only after durable processing state
- **THEN** it MAY be proposed for a new evidence review but SHALL not pass this change without the required compatibility and operational tests

### Requirement: No-go preserves full eligibility detection
When no candidate proves zero false negatives, the outcome SHALL select no watermark, SHALL preserve the block 58 full-eligibility EXISTS detector as the frequent-check correctness path, and SHALL block block 62's persisted watermarked detector. Reconciliation SHALL not be represented as proof that a lossy watermark is safe.

#### Scenario: Current evidence is evaluated
- **WHEN** asset and EXIF timestamps/update IDs, asset IDs, PostgreSQL transaction IDs, LISTEN/NOTIFY, custom state tables, and related combinations are assessed
- **THEN** the decision SHALL be no watermark because none is both durable and commit-ordered across all relevant writes and deletes

#### Scenario: Block 62 is considered after no-go
- **WHEN** implementation of block 62 is proposed without new passing evidence
- **THEN** work SHALL stop and block 58's EXISTS detector SHALL remain unchanged

### Requirement: Revisit criteria are measurable
The no-go decision SHALL be revisited only through a new or revised proposal that names the supported Immich/version matrix and demonstrates all required guarantees with repeatable database tests and production-operability evidence.

#### Scenario: Revisit gate is evaluated
- **WHEN** a future source is proposed
- **THEN** evidence SHALL include zero missed eligible transitions across an automated mutation matrix, an adversarial commit-inversion test, restart/replay and equal-value tests, schema/DDL drift failure behavior, multi-container single-consumer or coordination proof, and indexed or bounded-cost measurements on every supported schema

#### Scenario: Logical decoding is proposed
- **WHEN** a durable replication-slot design is the future candidate
- **THEN** evidence SHALL additionally cover atomic snapshot/slot bootstrap, both asset relations and all operation types, WAL retention exhaustion, crash replay, slot loss, failover/timeline behavior, privilege requirements, schema-change handling, and durable commit-LSN acknowledgment
