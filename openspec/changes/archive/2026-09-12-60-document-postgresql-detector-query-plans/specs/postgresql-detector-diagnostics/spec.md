## Purpose

PostgreSQL detector diagnostics give maintainers a safe, repeatable way to relate detector telemetry to contextual query-plan evidence without changing production data or presenting environment-specific plans as universal tuning rules.

## ADDED Requirements

### Requirement: The procedure is maintainer-only and begins with explicit safety prerequisites

The documentation SHALL live under the maintainer documentation and SHALL state that `EXPLAIN ANALYZE` executes the enclosed statement. It SHALL recommend a sanitized, access-controlled, representative staging copy rather than production, require a least-privilege read-only database role, an explicit read-only transaction, local statement/lock/idle-in-transaction timeouts, and rollback, and prohibit cache flushing, load generation, schema mutation, index creation, and use of production credentials or raw production data for the diagnostic.

#### Scenario: Maintainer prepares a diagnostic environment
- **WHEN** a maintainer follows the prerequisites
- **THEN** the procedure directs them to a representative staging copy and bounded read-only session before any analyzed plan is collected

#### Scenario: Only production is available
- **WHEN** a maintainer cannot obtain an approved representative staging copy
- **THEN** the procedure does not recommend running the analyzed diagnostic on production and directs them to use event 5901 and ordinary database observability while obtaining DBA approval or a safe copy

### Requirement: Diagnostic SQL has auditable provenance and exact predicate parity

The procedure SHALL identify the bounded runtime strategy as exactly `postgres-exists-v1` and the database operation as exactly `eligibility-existence-probe`. Its runnable statement SHALL be copied from the landed change-58 repository operation and cross-checked against the corresponding query/parity/performance tests at documentation time, preserving the scalar `EXISTS`, inner join, quoted join column, null city/country, non-null latitude/longitude, and non-deleted-asset predicates without parameter, hint, predicate, join, ordering, limit, or index variation. The procedure SHALL identify its source locations and SHALL direct maintainers to stop and reconcile rather than run it when code, tests, supported schema, or timeout policy diverge.

#### Scenario: Code and tests agree
- **WHEN** the landed repository SQL and its tests express the same finalized predicate
- **THEN** the procedure presents that exact statement inside `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` and records both provenance locations

#### Scenario: Source or schema differs
- **WHEN** the landed SQL, tests, supported Immich schema, or timeout policy differs from the documented assumptions
- **THEN** the procedure requires reconciliation and forbids substituting a guessed query or index

### Requirement: Evidence collection is bounded, contextual, and non-destructive

The procedure SHALL collect the analyzed JSON plan for representative early-match, late-match, and no-match cases in the read-only staging session. It SHALL also provide read-only catalog queries or equivalent safe steps for PostgreSQL server version, table definitions, existing index definitions, planner statistics freshness, and approximate table cardinalities, without selecting asset rows or requiring exact full-table counts. It SHALL label dataset provenance, case construction, repetition order, and cache state honestly and SHALL forbid clearing PostgreSQL or operating-system caches to manufacture a cold run.

#### Scenario: Representative cases are captured
- **WHEN** the maintainer records early-match, late-match, and no-match evidence
- **THEN** each plan is accompanied by version, schema/index inventory, approximate cardinality, statistics freshness, repetition order, and an explicit cache-warmth caveat

#### Scenario: Exact cardinality would add avoidable load
- **WHEN** table size context is needed
- **THEN** the procedure uses safe catalog estimates and labels them approximate instead of prescribing an exact count scan

### Requirement: Interpretation distinguishes semantics from planner choices

The procedure SHALL explain that `EXISTS` permits but does not guarantee early termination after a qualifying row, that early match, late match, and no match can inspect materially different work, and that no-match may scan broadly. It SHALL explain actual versus estimated rows and loops, rows removed by filters, planning versus execution time, shared-buffer hits and reads, and that a buffer read is not proof of a physical device read because the operating-system cache may satisfy it. It SHALL state that index scans are not inherently good, sequential scans are not inherently bad, plan shape depends on selectivity, statistics, cardinality, cache warmth, PostgreSQL/Immich version, and hardware, and repeated warm results are not a synthetic cold-cache benchmark.

#### Scenario: Plan uses a sequential scan
- **WHEN** a representative plan contains a sequential scan
- **THEN** the procedure evaluates match position, selectivity, cardinality, buffers, timing, and available indexes before treating the scan as a problem

#### Scenario: Plan uses an index
- **WHEN** a representative plan contains an index-backed scan
- **THEN** the procedure does not infer low total cost without examining heap access, filters, loops, buffers, and elapsed evidence

### Requirement: Query-plan evidence is correlated with event 5901 without conflating timings

The procedure SHALL explain how to compare a representative plan with the corresponding bounded `EventId(5901, "ProcessingWorkDetectorCompleted")` fields, including strategy, operation, outcome, and duration. It SHALL state that PostgreSQL execution time and detector duration cover different boundaries, that event 5901 exposes no plan/buffer/index facts, and that the 1000 millisecond warning boundary is an observability classification rather than a tuning SLA or automatic remediation threshold.

#### Scenario: Detector warning is investigated
- **WHEN** event 5901 reports `postgres-exists-v1` at Warning
- **THEN** the maintainer compares repeated representative evidence and environmental context rather than declaring regression from the warning level alone

### Requirement: Baselines and remediation remain evidence-based and non-prescriptive

The procedure SHALL establish per-environment baselines for representative cases and compare distributions or repeated observations over time rather than require a fixed node type, index name, cost, time, row, buffer, or speedup threshold. Its decision tree SHALL first check predicate/schema parity and repeatability, then distinguish database-plan cost from application/network/pool effects, stale or missing statistics from legitimate selectivity, existing-index non-use from absent supported index evidence, and isolated anomalies from sustained regressions. It SHALL direct schema/index/statistics changes to supported Immich guidance and a qualified DBA, require staging validation and rollback planning, and SHALL NOT provide unverified DDL or imply that this project owns the Immich schema.

#### Scenario: Evidence shows a sustained regression
- **WHEN** comparable repeated staging evidence and event 5901 history show a sustained change
- **THEN** the decision tree identifies the responsible branch and an evidence-gathering or escalation step without prescribing unverified DDL

#### Scenario: Evidence remains within the local baseline
- **WHEN** plan and detector observations vary within the established environment-specific baseline
- **THEN** the procedure recommends recording the evidence and avoiding speculative tuning

### Requirement: Shared diagnostics are minimized and redacted

The procedure SHALL prefer a short derived summary over raw plans or logs. Before sharing, it SHALL require removal or generalization of credentials, connection strings, hosts, ports, database/user names, relation/schema/index identifiers when sensitive, SQL outside the fixed detector statement, row/cardinality values when operationally sensitive, timestamps, paths, environment labels, and unrelated adjacent log fields. It SHALL warn that JSON plans and version output can expose topology, schema, scale, and build details even though the detector query returns only a boolean, and SHALL prohibit sharing raw production logs, data, or credentials.

#### Scenario: Maintainer shares evidence
- **WHEN** diagnostic evidence leaves the trusted maintainer group
- **THEN** the maintainer shares only the minimum redacted plan excerpts, bounded event 5901 fields, and contextual summary needed for review

### Requirement: Public and implementation documentation remain scoped

The procedure SHALL remain in maintainer documentation. Because the inspected public troubleshooting and navigation contain no maintainer escalation route, this change SHALL NOT add a public website link. It also SHALL NOT document future detector implementations, watermarking, runtime `EXPLAIN`, or database tuning automation.

#### Scenario: No suitable public escalation route exists
- **WHEN** the documentation structure has no appropriate advanced maintainer escalation
- **THEN** only maintainer navigation is updated and public documentation remains unchanged
