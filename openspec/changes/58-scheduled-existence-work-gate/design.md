## Context

See [proposal.md](proposal.md), [specs/scheduled-work-gating/spec.md](specs/scheduled-work-gating/spec.md), and change 57's processing-work-detection contract. Change 57 establishes `IProcessingWorkDetector.DetectAsync(ProcessingWorkDetectionRequest, CancellationToken)`, immutable request/result/diagnostic types, one Standard scheduled predispatch call site, and a stateless singleton adapter that temporarily calls the exact count. Block 58 changes only that adapter's full-eligibility database observation.

The inspected baseline repository operation is `ImmichDbRepository.GetUnprocessedCountAsync(CancellationToken)`. It inner-joins `asset` and `asset_exif` on `e."assetId" = a.id` and requires null city/country, present latitude/longitude, and null `a."deletedAt"`. It has no SQL parameters, explicit command timeout, overwrite option, or skipped-ID predicate. The current configuration has no overwrite eligibility setting. Skipped IDs are loaded later once per non-empty worker run and do not change the repository count. No repository migration or checked-in Immich index evidence exists, and current tests contain no real-PostgreSQL repository, EXPLAIN, or performance coverage.

Apply must first confirm that change 57 is landed and bind to its exact names and repository abstraction. If its eligibility predicate or command-timeout policy differs from this inspected evidence, stop and reconcile rather than silently changing eligibility or inventing a second detector seam.

## Goals / Non-Goals

**Goals:**

- Replace one count-backed full-eligibility adapter with a count-free PostgreSQL boolean existence operation.
- Preserve exact predicate, request/result shape, call order, cancellation/failure behavior, no-fallback policy, and advisory races.
- Keep the exact count unchanged for Dashboard and the worker and keep all worker totals, snapshots, batching, and parallelism unchanged.
- Establish correctness and opt-in query-plan/performance evidence without promising a particular PostgreSQL plan.

**Non-Goals:**

- No eligibility broadening, overwrite mode, skipped-ID filtering, processing-setting read, worker-count or progress change.
- No scheduler/coordinator, state finalizer, request/protocol, advisory lock, deployment-mode, telemetry, retry, or fallback redesign.
- No Immich schema or index DDL and no recommendation that an unverified index exists or should be added.
- No geodata, resolver, cache, airport, batch, asset-write, or skipped-store access.
- No block-59 instrumentation or block-60 maintainer query-plan procedure.

## Decisions

### 1. Put the boolean operation beside the exact repository count and keep change 57's public seam

Add one count-free method to the same lightweight PostgreSQL repository boundary/concrete repository that owns `GetUnprocessedCountAsync`; the preferred landed name is `HasUnprocessedAssetsAsync(CancellationToken cancellationToken = default)` returning `Task<bool>`. It owns connection/command creation and SQL decoding. It returns no count, row, asset ID, enumerable, or Npgsql object.

Change 57's concrete full-eligibility detector calls this method once and maps the returned boolean directly into the unchanged `ProcessingWorkDetectionResult`, with existence/full-eligibility/no-fallback bounded diagnostics. Do not add a second scheduler interface, SQL to the detector request, or an existence-specific caller branch. The Standard coordinator continues to branch only on `result.HasWork`. Dashboard and the worker continue to call the unchanged exact-count operation.

If change 57 landed a narrow repository interface rather than the concrete repository, add the boolean operation there and to its production implementation; do not make the detector depend on the Web page, worker executor, configuration, skipped repository, or Npgsql directly. The adapter remains stateless and mapped to the one existing singleton detector identity.

Alternative: change `GetUnprocessedCountAsync` to return bool. Rejected because Dashboard and worker authority still require the exact count. Alternative: issue `Take(1)` through batch APIs. Rejected because that crosses batch/cursor and worker boundaries and can drift from eligibility.

### 2. Use one scalar EXISTS statement with the exact current predicate

Use the following logical SQL shape (formatting may follow repository conventions):

```sql
SELECT EXISTS (
    SELECT 1
    FROM asset AS a
    INNER JOIN asset_exif AS e ON e."assetId" = a.id
    WHERE e.city IS NULL
      AND e.country IS NULL
      AND e.latitude IS NOT NULL
      AND e.longitude IS NOT NULL
      AND a."deletedAt" IS NULL
);
```

`EXISTS` is chosen over `COUNT(*) > 0` because PostgreSQL may stop after the first qualifying row and the scalar result naturally maps to bool. `SELECT 1 ... LIMIT 1` is semantically acceptable but would require careful no-row decoding; use one canonical `EXISTS` statement to keep the repository API total over match/no-match results.

The join remains inner, so assets without EXIF are ineligible. Both location text predicates remain null checks; an empty string is not silently reclassified. Both GPS coordinates must be non-null; no range, NaN, or spatial filter is added. `state` remains irrelevant. Soft-deleted assets remain excluded. The statement currently needs no SQL parameters because it compares only columns to null; do not add dummy values or interpolate identifiers. If the exact landed predicate has parameterized eligibility inputs, use typed Npgsql parameters and require parity tests, but do not read mutable settings inside the detector.

Schema assumptions are limited to the existing Immich tables/quoted columns and join types. Duplicate matching EXIF rows would not alter a boolean result, but apply should inspect the supported Immich schema/catalog to record the actual `asset.id` key, `asset_exif."assetId"` relationship, nullability, and available indexes used by tested versions. Evidence is documentation/test setup, not permission for DDL. A missing or incompatible schema is a query failure, never no work.

### 3. Preserve current overwrite and skipped-ID semantics explicitly

There is no current processing overwrite eligibility setting. Eligibility means city and country are both null; block 58 does not introduce a setting or reinterpret populated values. The detector therefore reads no `AppConfig` or processing snapshot.

The exact count includes database-eligible IDs even if they appear in `skipped.db`, and the worker obtains one skipped-ID snapshot only after its authoritative non-zero count. The existence probe must match that database predicate and must not load, parameterize, or subtract skipped IDs. If only skipped IDs qualify, a child can still launch and later skip them; this is preserved behavior, not a false detector result. Pushing a skipped-ID array into PostgreSQL was rejected because it changes semantics, creates unbounded parameters/snapshot races, and pulls SQLite state into the lightweight gate.

### 4. Preserve cancellation, timeout, and failure semantics with no fallback

Pass the exact admitted token to both `OpenConnectionAsync` and scalar execution. Decode the scalar strictly as PostgreSQL boolean using the repository's established Npgsql pattern; null or unexpected result type fails rather than mapping to false. Dispose connection and command normally.

The inspected code sets no explicit `NpgsqlCommand.CommandTimeout`, so block 58 adds no timeout setting and retains the data source/Npgsql default. If the applied change-57 repository boundary already establishes an explicit timeout, the existence command must inherit/copy that exact policy rather than reset, lengthen, or disable it. Timeout, connection, SQL, schema, decoding, and other faults propagate to change 57's established failure path. Matching cancellation propagates to its cancellation path. There is no catch-to-false, exact-count fallback, retry, stale cache, or alternate query.

Alternative: fall back to the exact count when EXISTS fails. Rejected because the same database/schema failure likely affects both and fallback would hide operational failure as scheduling behavior while adding load.

### 5. Keep worker authority and both race directions unchanged

The Web probe is advisory and does not share a transaction, snapshot, row lock, or PostgreSQL advisory lock with the worker. Positive probe followed by worker count zero launches exactly one child and completes through the worker's ordinary zero path. Negative probe followed by new eligibility closes the local occurrence and leaves work to a later normal trigger. A worker advisory-lock Busy outcome remains a launched-worker outcome. No race triggers replay, replacement, retry, catch-up, resubmission, or fallback.

The worker's exact count query, eligibility event/progress total, one skipped snapshot, one processing-config snapshot after non-zero authority, batch size/delay, and clamped maximum degree of parallelism remain untouched. Manual Dashboard processing, statistics reads, Web-only, public Run-once, and private-worker behavior retain change 57's bypass/authority boundaries.

### 6. Verify SQL correctness and performance without pinning a planner decision

Add focused repository tests for boolean typing, one command, cancellation propagation, unexpected scalar/fault propagation, no exact-count invocation, and no parameters for the current fixed predicate. Prefer observable repository behavior over a mock SQL parser; a narrowly normalized SQL-shape assertion may guard against `COUNT` reintroduction but must not become a formatting snapshot.

Add real-PostgreSQL integration coverage using a minimal, isolated `asset`/`asset_exif` fixture compatible with the supported schema assumptions. Cover eligible match, empty set, no EXIF row, deleted asset, city-only/country-only/populated values, null latitude, null longitude, arbitrary state, and multiple rows. Run the existence and exact-count methods over the same cases and assert only parity of `HasWork == (count > 0)`; do not share one implementation between them.

Add an opt-in Integration+Performance test that executes `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` for representative early-match, late-match, and no-match data. Assert only successful execution, valid JSON/plan presence, and correct query result/effect-free behavior. Emit plan, execution, and buffer measurements as diagnostic evidence. Do not assert node types, index names, join order, cost estimates, exact/relative timing, scanned-row counts, buffer counts, or a speedup ratio; these vary by PostgreSQL version, statistics, cache warmth, hardware, and fixture size. Normal tests continue to exclude Performance, and the test must use test-owned schema/data with no production credentials or DDL.

Block 60 may later turn the finalized query into a maintainer procedure. Block 58 only proves the query can be analyzed and captures reproducible test evidence; it does not document or prescribe a production plan.

## Risks / Trade-offs

- [No-match EXISTS can still scan broadly] → State this limit, exercise no-match in the opt-in plan test, and defer index decisions until supported-version evidence; do not claim bounded total cost.
- [Count and existence predicates drift] → Keep explicit SQL parity cases and compare boolean outcome with `count > 0` over the same integration fixture.
- [Skipped-only rows launch an apparently empty worker] → Preserve the existing database-eligibility/skipped-snapshot boundary and test it at detector/coordinator/worker seams rather than filtering in Web.
- [Planner assertions become flaky] → Assert semantic execution and parseability only; record variable plan metrics without gating on unstable details.
- [Query failure suppresses schedules] → Propagate failure through the established local failure finalizer; never return false or fall back.
- [Prerequisite names or predicate changed when change 57 landed] → Inventory the landed detector/repository/schema first and stop for reconciliation rather than creating duplicate APIs or silently changing semantics.

## Migration Plan

1. Verify change 57 is applied and inventory its exact detector, diagnostics enum, singleton registration, repository boundary, coordinator call order, Dashboard count caller, worker authoritative count, skipped snapshot, and timeout policy.
2. Verify supported Immich schema assumptions and record existing catalog/index evidence without applying DDL.
3. Add the repository boolean existence operation and parity/integration tests.
4. Replace only the count-backed detector adapter call and diagnostics kind; retain the same detector singleton/interface/request/result and coordinator path.
5. Add coordinator/authority/race/no-side-effect tests and opt-in EXPLAIN ANALYZE performance coverage.
6. Run focused tests, integration/performance tests explicitly, the normal suite, strict OpenSpec validation/status, and a block-58-only diff review proving block 59, worker count, settings, skipped storage, schema, and geodata are unchanged.

Rollback restores the count-backed adapter call. There is no schema, data, settings, protocol, index, or persisted-state migration.
