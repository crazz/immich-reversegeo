## 1. Reconcile the landed change-57 seam

- [ ] 1.1 Verify change 57 is applied and inventory the exact `IProcessingWorkDetector` implementation, immutable request/result/diagnostic types, singleton registration, Standard scheduled call site/order, repository abstraction, Dashboard exact-count caller, and worker authoritative exact-count caller; stop rather than create a parallel detector or retrofit the pre-change-57 monolith.
- [ ] 1.2 Record the landed full-eligibility SQL, Npgsql data-source/command-timeout policy, worker processing-config snapshot, skipped-ID snapshot timing, and supported Immich schema assumptions; reconcile any difference from the inspected predicate before editing.
- [ ] 1.3 Confirm the block-58 edit surface excludes block 59, public settings, worker protocol, worker count/progress/parallelism, skipped storage, Immich schema/index DDL, batch code, and every geodata/resolver/cache/airport dependency.

## 2. Add the count-free repository operation

- [ ] 2.1 Add `HasUnprocessedAssetsAsync(CancellationToken)` (or the exact landed repository naming equivalent) beside `GetUnprocessedCountAsync`, returning only `Task<bool>` and exposing no count, row, ID, enumerable, SQL, or Npgsql object.
- [ ] 2.2 Implement one scalar `SELECT EXISTS (SELECT 1 FROM asset a INNER JOIN asset_exif e ON e."assetId" = a.id WHERE e.city IS NULL AND e.country IS NULL AND e.latitude IS NOT NULL AND e.longitude IS NOT NULL AND a."deletedAt" IS NULL)` operation with no parameters for the fixed predicate.
- [ ] 2.3 Pass the exact cancellation token to connection opening and scalar execution, strictly decode the PostgreSQL boolean, dispose command/connection, and preserve the landed explicit command timeout if one exists; otherwise retain the current Npgsql/data-source default without adding a setting.
- [ ] 2.4 Propagate cancellation, timeout, connection, SQL, schema, and decoding failures; add no catch-to-false, exact-count fallback, retry, cached result, alternate query, or schema/index mutation.

## 3. Replace only the count-backed detector adapter

- [ ] 3.1 Change the existing stateless full-eligibility detector implementation to invoke the new boolean repository operation exactly once and map it directly to the unchanged `ProcessingWorkDetectionResult.HasWork` with bounded existence/full-eligibility/no-fallback diagnostics.
- [ ] 3.2 Preserve the same detector interface, immutable request/snapshot/result, singleton identity, coordinator call site, admission → pending → state-arm → detect → local-finalize-or-child-dispatch order, and matching-handle cleanup.
- [ ] 3.3 Prove the detector reads no AppConfig/overwrite setting and no skipped-ID snapshot, sends no parameters or eligibility data to a worker, and resolves no backend, protocol, batch, worker executor, geodata, resolver, cache, or airport service.
- [ ] 3.4 Keep Dashboard statistics and worker execution on the unchanged exact-count operation; retain the worker's count-derived total, one skipped-ID snapshot, one non-empty processing-config snapshot, batch/delay behavior, and clamped worker parallelism.

## 4. Verify eligibility parity and safe result semantics

- [ ] 4.1 Add repository tests covering eligible match and empty set plus near misses for missing EXIF, deleted asset, populated city, populated country, null latitude, and null longitude; prove state is irrelevant and both text fields and both GPS fields retain exact null semantics.
- [ ] 4.2 Add parity coverage that runs the existence and exact-count operations independently over the same cases and asserts only `hasWork == (count > 0)`, including multiple eligible rows and no count in the detector result.
- [ ] 4.3 Add a skipped-only seam test proving database eligibility remains true without skipped-store access and that the worker retains its later immutable skipped-ID snapshot/skip behavior; do not filter or parameterize skipped IDs in Web.
- [ ] 4.4 Add cancellation and query-failure tests proving neither returns successful false, falls back to count, launches a worker, retries, or leaks raw SQL/connection/schema detail into the bounded result or local presentation.

## 5. Preserve scheduled lifecycle, authority, and races

- [ ] 5.1 Test Standard scheduled work/no-work paths with one detector invocation, unchanged lock/admission/pending/state-arm/finalization order, zero exact-count calls solely for the Web gate, and backend/worker launch only for true.
- [ ] 5.2 Test positive-Web/worker-zero and negative-Web/work-appears-later races using deterministic signals, proving one child at most, ordinary worker zero versus closed local zero, and no fallback, replacement, replay, resubmission, catch-up, or retry.
- [ ] 5.3 Re-run manual Dashboard, Dashboard statistics, Web-only, public Run-once, private-worker, worker advisory-lock Busy, singleton/stateless concurrency, startup-laziness, and fail-on-resolution coverage inherited from changes 35–57.
- [ ] 5.4 Add dependency/side-effect guards proving the existence path performs only the PostgreSQL read and no Immich write, schema write, skipped/config/batch/protocol/backend/worker/geodata/cache/airport operation or detector persistence.

## 6. Add PostgreSQL plan and performance evidence

- [ ] 6.1 Add a real-PostgreSQL integration fixture with isolated test-owned `asset`/`asset_exif` schema/data that records supported key, join, nullability, and existing-index assumptions without modifying production schema or inventing an index recommendation.
- [ ] 6.2 Add opt-in Integration+Performance cases for representative early-match, late-match, and no-match data using `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`; assert successful execution, valid plan JSON, semantic result, and read-only behavior only.
- [ ] 6.3 Emit plan/execution/buffer evidence for diagnosis but assert no node type, join order, index name, cost, timing, row-scan/buffer count, or speedup ratio; keep Performance excluded from normal runs and use no production credentials or objects.
- [ ] 6.4 Run focused repository/detector/coordinator/composition tests, the real-PostgreSQL integration suite, the opt-in performance suite explicitly, and `npm run test` with normal exclusions.

## 7. Validate scope

- [ ] 7.1 Run `openspec validate 58-scheduled-existence-work-gate --strict` and `openspec status --change 58-scheduled-existence-work-gate`.
- [ ] 7.2 Review a block-58-only diff and confirm block 59 and later artifacts/code, worker exact count, public settings, skipped storage, schema/indexes, protocol, processing parallelism, and geodata remain unchanged.
