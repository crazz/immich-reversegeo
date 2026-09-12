# Diagnose the scheduled PostgreSQL detector

Use this procedure to investigate `strategy=postgres-exists-v1` and `database_operation=eligibility-existence-probe` in Immich ReverseGeo. It collects contextual plan evidence on a prepared staging copy. It does not tune the Immich database.

**`EXPLAIN ANALYZE` executes the enclosed statement.** A read-only query can still consume CPU, hold locks and scan broadly. Use an approved, access-controlled, sanitized staging copy with representative schema, indexes, scale and eligibility distribution. Use a least-privilege role with read-only access to the required tables and metadata, staging credentials, and one diagnostic session at a time. Do not use production credentials or raw production rows. Do not run this analyzed diagnostic on production, generate load, clear PostgreSQL/OS caches, or change schema, indexes or statistics. [PostgreSQL EXPLAIN](https://www.postgresql.org/docs/16/sql-explain.html).

If a safe copy is unavailable, stop here. Use bounded event 5901 history and ordinary DBA observability while arranging approval and a representative staging copy. This procedure does not authorize a production capture.

## Check the source before connecting

The provenance baseline is applied changes 58–59, through commit `39a1cff4f392eeac6aa868c3f7c7316ea5c79c70`:

| Source | What to verify |
| --- | --- |
| [ImmichDbRepository.cs](../../src/ImmichReverseGeo.Web/Services/ImmichDbRepository.cs) | `UnprocessedAssetsExistenceSql` and `HasUnprocessedAssetsAsync`: one parameterless scalar read; no command-timeout override. |
| [ScheduledExistenceRepositoryTests.cs](../../tests/ImmichReverseGeo.Tests/ScheduledExistenceRepositoryTests.cs) | Compiled boundary forbids count calls, parameters and timeout overrides. |
| [ScheduledExistencePostgresFixture.cs](../../tests/ImmichReverseGeo.Tests/ScheduledExistencePostgresFixture.cs) | `ExistenceSql` reads the production constant, rather than maintaining another query. |
| [ProcessingWorkDetectionPostgresTests.cs](../../tests/ImmichReverseGeo.Tests/ProcessingWorkDetectionPostgresTests.cs) | Eligibility parity with an independent count and unchanged logical table values. |
| [ScheduledExistencePostgresPlanTests.cs](../../tests/ImmichReverseGeo.Tests/ScheduledExistencePostgresPlanTests.cs) | Early/late/no-match definitions and `EXPLAIN` of that exact constant. |
| [ScheduledExistencePostgresFailureTests.cs](../../tests/ImmichReverseGeo.Tests/ScheduledExistencePostgresFailureTests.cs) | Real command timeout propagates from the data-source policy. |
| [InstrumentedProcessingWorkDetector.cs](../../src/ImmichReverseGeo.Web/Services/InstrumentedProcessingWorkDetector.cs) and [observability tests](../../tests/ImmichReverseGeo.Tests/ProcessingWorkDetectorObservabilityTests.cs) | Event 5901, bounded dimensions, elapsed duration and successful roundtrip evidence. |

The predicate requires an EXIF row joined by `e."assetId" = a.id`, null city **and** country, both GPS coordinates present, and no asset deletion timestamp. Empty strings are populated values. State and the local skipped-assets store are outside this predicate. The result is advisory; the worker still takes its own lock and fresh exact count.

The [schema witness and fixture limits](scheduled-existence-evidence.md#schema-witness-and-fixture-limits) identify Immich v3.2.0, commit `1b6098c9dbfffe978bec2d414606ed7a4c8e019a`. That witness covers the fields used by this query; it does not establish compatibility of every Immich version or reproduce all production indexes. The recorded fixture used PostgreSQL 16.15. Use documentation for the actual PostgreSQL version and record the Immich version from the staging deployment's image/release metadata.

Compare the query below token-for-token with the landed constant, ignoring whitespace and the script's final statement terminator only. Check the tests, relation resolution, privileges, supported schema and effective timeout policy as well. **On any mismatch, stop and reconcile this page with the supported code and schema.** Do not guess a join, add a limit, parameter, planner setting, predicate, index or hint. Runtime inherits the existing Npgsql data-source timeout and caller cancellation; the local limits below are diagnostic examples, not runtime defaults.

## Prepare three representative cases

Have the staging owner prepare sanitized datasets before this read-only session. Reuse the performance fixture's case definitions as a reference:

| Case | Reference construction | Expected boolean |
| --- | --- | --- |
| Early match | 10,000 synthetic asset/EXIF pairs; only the pair inserted at position 1 is eligible. | True |
| Late match | Same scale; only the pair inserted at position 10,000 is eligible. | True |
| No match | Same scale; every EXIF city is populated. | False |

Eligible fixture pairs have null city/country, non-null coordinates and no deletion timestamp. The names describe **insertion position**, not guaranteed access or join order. PostgreSQL may find either matching tuple differently. Preserve realistic schema/indexes and choose representative scale and distributions with the staging owner; 10,000 is a fixture size, not a production baseline.

The test harness creates disposable data, collects row snapshots/counts and refreshes fixture statistics. Those setup actions are not part of this procedure: do not copy its seed, `ANALYZE`, exact-count or row-export commands into a diagnostic session. Record dataset provenance, sanitization method, case construction, approximate scale, preparation/statistics state, server/container resources, concurrent workload and run order. Keep sensitive details local.

## Collect context, then one analyzed plan

Use a dedicated staging SQL client session with automatic transaction handling understood. Review these example limits with the DBA: 1 second waiting for a lock, 5 seconds per statement, and 30 seconds idle inside the transaction. Do not disable or silently raise limits to obtain a plan. Submit each block promptly so an idle transaction is not left open. `SET LOCAL` ends with the transaction. [Transaction access modes](https://www.postgresql.org/docs/16/sql-set-transaction.html), [local settings](https://www.postgresql.org/docs/16/sql-set.html) and [timeout semantics](https://www.postgresql.org/docs/16/runtime-config-client.html).

First collect metadata only:

```sql
BEGIN;
SET TRANSACTION READ ONLY;
SET LOCAL lock_timeout = '1s';
SET LOCAL statement_timeout = '5s';
SET LOCAL idle_in_transaction_session_timeout = '30s';

SELECT current_setting('server_version') AS postgres_version;
SELECT to_regclass('asset') = to_regclass('public.asset') AS asset_resolves_to_public,
       to_regclass('asset_exif') = to_regclass('public.asset_exif') AS exif_resolves_to_public;

SELECT table_schema, table_name, column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_schema = 'public'
  AND ((table_name = 'asset' AND column_name IN ('id', 'deletedAt'))
    OR (table_name = 'asset_exif' AND column_name IN
        ('assetId', 'city', 'country', 'latitude', 'longitude')))
ORDER BY table_name, ordinal_position;

SELECT c.relname AS table_name, con.conname AS constraint_name,
       pg_get_constraintdef(con.oid) AS definition
FROM pg_catalog.pg_constraint con
JOIN pg_catalog.pg_class c ON c.oid = con.conrelid
JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND c.relname IN ('asset', 'asset_exif')
  AND con.contype IN ('p', 'f')
ORDER BY c.relname, con.conname;

SELECT schemaname, tablename, indexname, indexdef
FROM pg_catalog.pg_indexes
WHERE schemaname = 'public' AND tablename IN ('asset', 'asset_exif')
ORDER BY tablename, indexname;

SELECT n.nspname AS schema_name, c.relname AS table_name,
       c.reltuples AS estimated_rows, c.relpages AS estimated_pages,
       c.relrowsecurity AS row_security_enabled,
       s.last_analyze, s.last_autoanalyze, s.n_mod_since_analyze
FROM pg_catalog.pg_class c
JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
LEFT JOIN pg_catalog.pg_stat_all_tables s ON s.relid = c.oid
WHERE n.nspname = 'public' AND c.relname IN ('asset', 'asset_exif')
  AND c.relkind IN ('r', 'p')
ORDER BY c.relname;
ROLLBACK;
```

Require both resolution checks to be true and all expected metadata visible. A false/null result, missing permissions, different schema, unexpected row-security policy or incompatible fields requires reconciliation with the DBA before continuing. Do not change the query to bypass that discrepancy.

The column inventory is limited to this query's fields. `indexdef` and constraint definitions describe existing objects; they are evidence, never commands to execute. Metadata may expose identifiers and expressions. [Column visibility](https://www.postgresql.org/docs/16/infoschema-columns.html) and [index inventory](https://www.postgresql.org/docs/16/view-pg-indexes.html).

`reltuples` and `relpages` are approximate planner values; `reltuples = -1` means unknown. A null analyze timestamp or reset statistics is not proof that analysis never occurred. Interpret timestamps and estimated modifications with the copy/reset history; do not select asset rows or run an exact count to fill gaps. [Relation estimates](https://www.postgresql.org/docs/16/catalog-pg-class.html) and [table statistics](https://www.postgresql.org/docs/16/monitoring-stats.html#MONITORING-PG-STAT-ALL-TABLES-VIEW).

After reviewing the inventory, collect one plan for the prepared case in a fresh bounded transaction:

```sql
BEGIN;
SET TRANSACTION READ ONLY;
SET LOCAL lock_timeout = '1s';
SET LOCAL statement_timeout = '5s';
SET LOCAL idle_in_transaction_session_timeout = '30s';

EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
SELECT EXISTS (
    SELECT 1 FROM asset a
    INNER JOIN asset_exif e ON e."assetId" = a.id
    WHERE e.city IS NULL AND e.country IS NULL
      AND e.latitude IS NOT NULL AND e.longitude IS NOT NULL
      AND a."deletedAt" IS NULL
);
ROLLBACK;
```

Save the returned JSON locally with the case and run order, then confirm the session is outside a transaction. `EXPLAIN` returns a plan, not the query's boolean value; use the prepared case's known eligibility. After any error or cancellation, stop, issue `ROLLBACK` if the session remains connected, and close it. An idle timeout can close the session; reconnect only after investigating. A timed-out statement is incomplete evidence, not a no-work result. Do not automatically retry or omit the transaction to get output.

## Read the evidence in context

Start at the JSON array's first object, then its `Plan` and nested `Plans`. The outer scalar EXISTS result produces one output row even for false; that is not the number of eligible assets. `Plan Rows` is an estimate; `Actual Rows` describes emitted rows, not every examined row. For repeated nodes, row/time measurements are per-loop averages: inspect `Actual Loops` and filter/recheck removals. Early termination can leave nodes partly executed or unexecuted, so a partially consumed EXISTS plan is not a full-cardinality estimate check.

Separate top-level `Planning Time` from `Execution Time`; planner cost units are not milliseconds. Compare buffers at corresponding nodes and do not sum parent and child totals, which overlap. An index scan may still fetch heap pages and discard rows; a sequential scan may be reasonable for a small table or low selectivity. Assess both using cardinality, estimates, filters, loops and elapsed work, rather than ranking node names. [Reading EXPLAIN](https://www.postgresql.org/docs/16/using-explain.html).

EXISTS can stop after a qualifying tuple. Early, late and absent matches may therefore require very different work, and no-match may inspect the entire relevant search space. There is no promised index, fixed plan shape or guaranteed speedup. [EXISTS semantics](https://www.postgresql.org/docs/16/functions-subquery.html#FUNCTIONS-SUBQUERY-EXISTS).

`Shared Hit Blocks` were already in PostgreSQL shared buffers. `Shared Read Blocks` were brought into shared buffers; the OS cache may have supplied them, so they do not establish physical-device reads. Include selectivity, current statistics, approximate cardinality, existing indexes, cache warmth, PostgreSQL/Immich versions and hardware in comparisons. Label the first observation “first in this sequence,” or “colder in this sequence” with preparation context. Repeated runs are warmer observations, not a controlled cold-cache benchmark. Never flush PostgreSQL or OS caches. [Buffer interpretation](https://www.postgresql.org/docs/16/sql-explain.html).

## Compare with detector events and a local baseline

Use `EventId(5901, "ProcessingWorkDetectorCompleted")` from the same strategy and a close, documented interval. Prefer these bounded fields:

| Field | Interpretation |
| --- | --- |
| `strategy`, `database_operation` | Exactly `postgres-exists-v1` and `eligibility-existence-probe`. |
| `outcome` | `HasWork`, `NoWork`, `Cancelled` or `Failed`. |
| `duration_ms` | Monotonic elapsed time around the detector call. |
| `database_roundtrips` | `1` on a successful result; absent on cancellation/failure. Absence is unknown, not zero. |

Structured logging exposes these keys; a plain console formatter may show only the common rendered fields. Inspect structured output for optional keys: a missing key in plain text cannot establish the roundtrip count. Runtime logs expose no plan, rows scanned, buffers, physical reads or index use.

Server execution time excludes client-side connection acquisition, pool waiting, network and application continuation overhead included in detector duration. EXPLAIN instrumentation also adds overhead. Compare repeated trends, not exact equality or subtraction as a precise overhead measurement. Event 5901 uses Warning for failure or duration at least 1000 ms; that boundary classifies a log event, not a tuning SLA or automatic remediation threshold.

Record a modest, agreed series for each prepared early/late/no-match case, sequentially and without concurrent load generation. Keep per-case ranges or distributions of execution/planning time, buffer evidence and detector duration, with versions, preparation, statistics, resources and run order. Re-establish context after a copy, upgrade or workload change. Use local comparable baselines over time; require no fixed node, index name, cost, timing, row, buffer or speedup threshold.

## Decide the next step

1. **Source, strategy, schema or version differs:** stop and reconcile supported code, tests and schema before comparison.
2. **Results vary or context changed:** document workload, statistics state, case preparation and run order; collect comparable observations before attributing a regression.
3. **Server execution is stable but detector duration regresses:** investigate connection acquisition, pool pressure, network/container load and application scheduling through ordinary observability.
4. **Estimates diverge or statistics appear stale:** first account for early termination and statistics/reset history. Consult supported Immich maintenance guidance and a DBA. This procedure does not run statistics maintenance.
5. **An existing index is unused:** assess selectivity, table size, heap/filter work, cache state and PostgreSQL version. Non-use alone does not prove a planner defect or a missing index.
6. **Broad work is confined to repeatable late/no-match cases:** record the local baseline. Within-baseline variation needs no speculative tuning; discuss scheduling cadence or design research separately if the established cost is unacceptable.
7. **A comparable sustained regression remains, or a new index is hypothesized:** prepare minimized evidence for Immich-supported guidance and DBA review. Any later mutation needs separate approval, disposable staging validation and rollback planning. Immich ReverseGeo does not own the Immich schema; this page supplies no DDL or tuning prescription.

## Minimize before sharing

Prefer a derived summary: case, necessary version granularity, approximate scale band, relevant node excerpts, aggregate timing/buffers, bounded event fields and the observed trend. JSON plans and catalog/version output can reveal schema, topology, scale and platform details even when the query only computes a boolean.

Before evidence leaves the trusted maintainer group:

- Remove secrets, credentials, connection strings, hosts/ports, database and user names.
- Remove or generalize sensitive schema/relation/index identifiers, expressions, paths, build details and environment labels.
- Generalize precise cardinalities, timestamps and resource scale when operationally sensitive; retain enough context to make the comparison meaningful.
- Remove unrelated SQL, parameter values and adjacent log fields. Share only necessary excerpts of the fixed detector query and bounded event 5901 data.
- Never include production rows, raw production data, raw production logs or full connection configuration.

Keep the reviewed excerpts and their local provenance together. A plan from a minimal synthetic fixture illustrates that fixture; it is not evidence of a production index, workload or performance guarantee.
