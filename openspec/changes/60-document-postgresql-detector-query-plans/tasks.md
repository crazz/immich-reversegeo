## 1. Reconcile Applied Prerequisites and Scope

- [ ] 1.1 Verify changes 58 and 59 are applied; re-read their landed repository operation, query/parity/performance tests, command-timeout behavior, event 5901 schema, and exact `postgres-exists-v1` / `eligibility-existence-probe` literals. Stop and reconcile block 60 on any divergence.
- [ ] 1.2 Reconfirm the existing standalone `docs/maintainer/` layout and absence of a public maintainer-escalation route, then select one maintainer page path and leave public website content/navigation unchanged.
- [ ] 1.3 Confirm the implementation edit surface is documentation-only and excludes runtime code/tests, SQL behavior, schema/index/statistics changes, configuration, UI, telemetry, and blocks 61 onward.

## 2. Author the Safe Diagnostic Procedure

- [ ] 2.1 Open with prerequisites and risks: `EXPLAIN ANALYZE` executes the query; use a sanitized representative staging copy, least-privilege read-only role, explicit read-only transaction, local lock/statement/idle timeouts, rollback, and no cache flush, load generation, production credentials, or production recommendation.
- [ ] 2.2 Copy the exact landed change-58 scalar `EXISTS` SQL from the repository operation, cite its code and test provenance, cross-check predicate/token parity, and wrap only that statement in `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`.
- [ ] 2.3 Document representative early-match, late-match, and no-match staging cases using the applied performance fixture as the reference; label dataset provenance, case construction, repetition order, and cache warmth without copying production rows.
- [ ] 2.4 Add safe read-only metadata collection for PostgreSQL/Immich version context, table/column definitions, existing index definitions, statistics freshness, and approximate `pg_class` cardinality/page counts; do not require asset-row selection or exact `COUNT(*)` scans.

## 3. Explain and Correlate Evidence

- [ ] 3.1 Explain `EXISTS` potential early exit and early/late/no-match work; actual versus estimated rows/loops, rows removed, planning/execution time, and why no-match may scan broadly.
- [ ] 3.2 Explain shared-buffer hits/reads without equating reads to physical-device I/O; explain cache-order/warmth limits and prohibit PostgreSQL/OS cache clearing.
- [ ] 3.3 Explain why index scans are not automatically good and sequential scans are not automatically bad, including heap/filter work, selectivity, statistics, cardinality, version, hardware, and existing-index context.
- [ ] 3.4 Correlate representative plans with bounded event 5901 strategy/operation/outcome/duration/roundtrip fields; distinguish PostgreSQL execution time from detector duration and state that the 1000 ms Warning boundary is not a tuning SLA.

## 4. Baseline, Decide, and Redact

- [ ] 4.1 Define repeated per-environment early/late/no-match baselines and trend comparisons with no rigid node, index, cost, time, row, buffer, or speedup thresholds.
- [ ] 4.2 Add the ordered remediation decision tree for source/schema mismatch, non-repeatable context, application-versus-server cost, stale estimates/statistics, existing-index non-use, expected late/no-match work, and sustained regression/new-index hypotheses; defer all mutations and DDL to supported Immich guidance and DBA-reviewed staging work.
- [ ] 4.3 Add a minimization-first redaction checklist covering secrets/connection data, topology, database/user/object names, paths/build details, exact scale/timestamps/environment labels when sensitive, unrelated SQL, adjacent logs, raw production data, and raw production logs.

## 5. Documentation Verification and Handoff

- [ ] 5.1 Build the documentation and inspect the rendered page, commands, code fences, internal navigation, and any conditional link; verify there is no production `EXPLAIN ANALYZE` recommendation, DDL, cache flush, rigid threshold, unsupported schema/index claim, or future implementation documentation.
- [ ] 5.2 Compare the rendered diagnostic SQL token-for-token (ignoring formatting only) with landed code and its query/parity/performance tests, and verify the transaction is read-only, timeout-bounded, and rolled back.
- [ ] 5.3 Run `openspec validate 60-document-postgresql-detector-query-plans --strict` and `openspec status --change 60-document-postgresql-detector-query-plans`; require all four artifacts complete and review a block-60-only diff excluding block 61 and project implementation files.
