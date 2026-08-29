## Context

See [proposal.md](proposal.md) and [specs/postgresql-detector-diagnostics/spec.md](specs/postgresql-detector-diagnostics/spec.md). Finalized change 58 defines one parameterless scalar PostgreSQL `EXISTS` operation with exact full-eligibility parity; finalized change 59 names it `strategy=postgres-exists-v1` / `database_operation=eligibility-existence-probe` and emits event 5901 duration/outcome evidence while intentionally omitting plans, buffers, rows scanned, and index claims. The current source tree still shows the pre-58 exact-count repository query, so documentation implementation must wait for changes 58–59 to be applied and then bind to landed code/tests rather than treating this planning copy as runtime truth.

This change authors maintainer documentation only. It does not execute diagnostics now, alter runtime behavior, or edit public product docs during planning.

## Goals / Non-Goals

**Goals:**

- Produce a safe, reproducible maintainer workflow for contextual plan evidence on a representative staging copy.
- Prevent diagnostic SQL drift by deriving it from and cross-checking it against landed code and tests.
- Teach cautious interpretation and correlation with event 5901 without universal thresholds or plan prescriptions.
- End with actionable evidence-gathering/escalation branches and minimum necessary redaction.

**Non-Goals:**

- No production `EXPLAIN ANALYZE` recommendation, runtime `EXPLAIN`, automated plan capture, load test, cache flush, schema/statistics/index mutation, or DDL advice.
- No claim that an index exists, is missing, or should be added on every supported Immich version.
- No performance SLA, rigid warning threshold, preferred plan node/join order, or guaranteed `EXISTS` speedup.
- No public end-user setup material, future watermark implementation documentation, or change to changes 58–59.

## Decisions

### 1. Keep the procedure maintainer-only and staging-first

Create one focused page under `docs/maintainer/`. The inspected maintainer directory contains standalone pages and the public Zensical navigation exposes no maintainer section or advanced maintainer escalation. Do not add a website navigation item or public troubleshooting link; public docs remain unchanged.

The opening warning says that `EXPLAIN ANALYZE` executes the query. Although the finalized query is a read-only `SELECT EXISTS`, accidental source drift and unbounded scans remain risks. Recommend an access-controlled, sanitized, representative staging copy, a least-privilege read-only role, and a bounded transaction such as:

`BEGIN; SET TRANSACTION READ ONLY; SET LOCAL lock_timeout = '1s'; SET LOCAL statement_timeout = '5s'; SET LOCAL idle_in_transaction_session_timeout = '30s'; ...; ROLLBACK;`

Timeout values are conservative examples to review with the DBA and staging baseline, not runtime-policy changes or universal limits. If a safe copy is unavailable, stop: use event 5901 and existing DBA observability while seeking approval/copy creation. Alternative: recommend direct production capture because the query is read-only. Rejected because `ANALYZE` executes it, no-match can scan broadly, and diagnostic concurrency can affect production.

### 2. Treat landed code and tests as the SQL provenance chain

At apply time, re-read the applied change-58 repository operation and the tests that prove query/predicate parity and run the performance fixture. Copy the SQL statement verbatim, preserving this finalized logical shape:

`SELECT EXISTS (SELECT 1 FROM asset AS a INNER JOIN asset_exif AS e ON e."assetId" = a.id WHERE e.city IS NULL AND e.country IS NULL AND e.latitude IS NOT NULL AND e.longitude IS NOT NULL AND a."deletedAt" IS NULL);`

Wrap only that statement with `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`. Record the exact source/test paths and strategy literals on the page. Review the doc query token-for-token (ignoring formatting only) against both sources. If landed names, predicate, parameters, timeout policy, or supported schema differ, stop and reconcile block 60; do not “improve” the diagnostic with `LIMIT`, planner hints, rewritten joins, parameters, or an assumed index.

Alternative: maintain a hand-optimized diagnostic query. Rejected because its plan would no longer diagnose the runtime strategy. Alternative: make runtime code load SQL from docs. Rejected because documentation is not a runtime dependency.

### 3. Use representative early/late/no-match scenarios and safe context inventory

Reuse the applied change-58 Integration+Performance fixture's case definitions where possible. The maintainer page describes how to construct equivalent sanitized early-match, late-match, and no-match staging states without copying production values. Collect only context needed to interpret plans:

- `current_setting('server_version')` (prefer this over full build output when sharing);
- table/column and existing index inventory from `pg_catalog` / `information_schema`;
- approximate relation cardinality and page counts from `pg_class`; and
- statistics freshness/change context from safe catalog/statistics views.

Do not select asset rows or require `COUNT(*)`. Record Immich/PostgreSQL versions, dataset provenance, approximate cardinalities, existing index definitions, case, run order, and whether runs were first or repeated. Never clear PostgreSQL/OS caches. A first run after copy/start is only “colder in this sequence,” not a controlled cold-cache measurement.

### 4. Explain plan evidence without ranking node types

The page walks from the JSON root to actual/estimated rows, loops, rows removed by filter, planning/execution time, and buffers. `EXISTS` can stop after the first qualifying tuple, so early match may touch little, late match more, and no match may inspect the entire relevant search space; PostgreSQL still chooses the plan, and early exit is not guaranteed to imply an index plan.

Explain `shared hit` as a page found in PostgreSQL shared buffers and `shared read` as a page read into shared buffers. Do not label every read as physical-device I/O because the operating-system cache may satisfy it. Explain that an index scan can still incur heap access/filtering and that a sequential scan can be rational for small tables or low selectivity. Interpret plans only with version, statistics, selectivity, cardinality, existing indexes, cache warmth, and hardware context.

Alternative: publish “good plan” thresholds. Rejected because change 58 explicitly avoids pinning plan shape and environment-specific costs.

### 5. Correlate plans with event 5901 as two different measurement boundaries

Use only the safe bounded fields from `EventId(5901, "ProcessingWorkDetectorCompleted")`: `strategy`, `database_operation`, `outcome`, `duration_ms`, and successful `database_roundtrips` where present. Compare the same strategy and representative scenario over a close, documented interval; do not expect exact equality. PostgreSQL execution time covers server execution, while detector duration also includes client/database boundary overhead. Event 5901 cannot establish rows scanned, buffers, reads, or index use. Its 1000 ms Warning boundary is a log classification from change 59, not an SLA or remediation trigger.

### 6. Baseline trends, then follow a no-DDL decision tree

Record repeated early/late/no-match observations and a local range/distribution with context instead of a pass/fail number. The decision tree is:

1. **Predicate, schema, strategy, or version differs:** stop and reconcile sources/support before comparing.
2. **Result is not repeatable or context changed:** control staging workload, statistics state, case data, and run order; gather more evidence.
3. **PostgreSQL execution is stable but event 5901 duration regresses:** investigate connection acquisition, network/container load, pool pressure, and application scheduling outside this query-plan procedure.
4. **Estimates diverge materially from actuals or statistics are stale:** consult supported Immich maintenance guidance and a DBA; this procedure does not run `ANALYZE` or mutate statistics.
5. **An existing index is not selected:** examine selectivity, cardinality, heap/filter cost, cache state, and PostgreSQL version before concluding non-use is wrong.
6. **Broad work is limited to late/no-match and is consistent:** record the baseline; consider cadence/design research rather than speculative schema changes.
7. **A sustained comparable regression remains or a new index is hypothesized:** prepare redacted evidence, test only in disposable staging with rollback planning, and escalate to Immich-supported guidance/DBA review; do not publish unverified DDL.

### 7. Redact by minimization, not by assuming JSON is harmless

Prefer a derived summary containing scenario, versions generalized to needed granularity, approximate scale band, node excerpts, aggregate buffers/timing, and bounded event 5901 fields. Before sharing raw excerpts, remove credentials/connection strings, hosts/ports, database/user names, sensitive schema/relation/index names, paths/build details, precise scale or timestamps when sensitive, environment labels, unrelated SQL, and adjacent logs. The fixed detector SQL contains no values, but JSON plans and version/catalog output can reveal topology, object names, scale, and platform details. Never share production rows, raw production logs, secrets, or full connection configuration.

## Risks / Trade-offs

- [The documented SQL drifts from runtime] → Re-read and cross-check applied code plus parity/performance tests; stop on any mismatch.
- [A read-only query still adds harmful load] → Use a representative staging copy, read-only transaction, local timeouts, rollback, and no production recommendation.
- [Cache language overclaims physical I/O] → Distinguish shared-buffer reads from device reads and label run order/warmth honestly.
- [One plan becomes an index prescription] → Require three scenarios, repeated local baselines, version/statistics/cardinality context, and a no-DDL decision tree.
- [Telemetry and server timing are conflated] → Document their different boundaries and compare trends rather than exact values.
- [Shared evidence leaks operational details] → Minimize output and apply an explicit redaction checklist before it leaves the maintainer group.

## Migration Plan

1. After changes 58–59 are applied, verify the landed repository SQL, tests, strategy literals, event schema, supported schema, and timeout behavior; stop on divergence.
2. Select the maintainer page path using the existing standalone maintainer-doc pattern; leave public website content and navigation unchanged.
3. Author the maintainer page with warning/prerequisites, exact provenance-checked SQL, bounded read-only transaction, representative scenarios, context inventory, interpretation, event correlation, baseline, decision tree, and redaction.
4. Build documentation and review rendered commands/copy for safety, SQL parity, scope, links, and absence of credentials, production recommendation, DDL, and rigid thresholds.
5. Strict-validate/status the OpenSpec change and review a block-60-only diff. Rollback removes only the maintainer page/navigation entry; no runtime or data migration exists.
