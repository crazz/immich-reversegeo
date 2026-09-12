## Why

Change 59 can identify slow detector calls, but runtime telemetry intentionally cannot show plan shape, buffers, rows examined, or index use. Maintainers need a safe, evidence-based procedure for diagnosing the finalized change-58 PostgreSQL `EXISTS` strategy without experimenting on production or turning one plan into a universal tuning rule.

## What Changes

- Add a maintainer-only query-plan procedure under `docs/maintainer/` for `strategy=postgres-exists-v1` / `database_operation=eligibility-existence-probe`.
- Source the diagnostic statement verbatim from the landed change-58 repository SQL and its parity/performance tests, and require a stop-and-reconcile check if code, tests, schema, or timeout policy differ.
- Define prerequisites and guardrails for a sanitized representative staging copy, least-privilege access, an explicit read-only transaction, local timeouts, and `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`; do not recommend running `ANALYZE` plans against production.
- Explain `EXISTS` early exit, early/late/no-match cases, index and sequential scans, buffer hits/reads, cache warmth, version/statistics/cardinality context, and comparison with detector event 5901.
- Use scenario baselines and trends rather than rigid plan, timing, or buffer thresholds; provide a no-DDL remediation decision tree and a plan/log redaction checklist.
- Keep the procedure maintainer-only. The inspected public troubleshooting/navigation has no maintainer escalation route, so add no public website link; runtime/implementation documentation remains deferred to later changes.

## Capabilities

### New Capabilities
- `postgresql-detector-diagnostics`: Safe, repeatable maintainer diagnosis of the scheduled PostgreSQL detector using provenance-checked SQL and contextual plan evidence.

### Modified Capabilities
- None.

## Impact

Planning targets a future maintainer page and, only if the existing maintainer navigation pattern requires it, a maintainer index entry. There is no runtime, SQL, schema, index, configuration, UI, public product-documentation, dependency, or deployment behavior change. The procedure depends on applied/finalized changes 58–59 and must be reconciled to their landed code and tests before documentation is authored.
