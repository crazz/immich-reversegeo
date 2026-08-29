## 1. Reconcile applied worker boundaries

- [ ] 1.1 Re-read the applied blocks 15 and 20–23 executor, reporter, terminal, outcome-accumulator, host, stream, and disposal APIs plus the finalized worker composition; stop rather than invent parallel ownership.
- [ ] 1.2 Identify and test the exact first executor/session seam after `run-started`; prove no eligibility, snapshot, skipped-asset, geodata/cache, lookup, mutation, or domain progress operation precedes it.

## 2. Define lock identity and unit seams

- [ ] 2.1 Add key-version 1 and signed-bigint `-7970420658158250032L` constants, document the SHA-256/UTF-8/big-endian derivation label, and add a drift test that recomputes it.
- [ ] 2.2 Add a narrow async acquisition boundary returning acquired lease, busy, cooperative cancellation, or infrastructure failure, with deterministic connection/command/monitor seams for unit tests.
- [ ] 2.3 Implement parameterized single-bigint `pg_try_advisory_lock` acquisition on a newly opened dedicated connection from the worker data source; keep the connection private and out of domain repositories.

## 3. Integrate accepted-session outcomes

- [ ] 3.1 Invoke acquisition immediately after `run-started` inside the exactly-once executor/reporter session and before all domain/heavy work, not before executor invocation.
- [ ] 3.2 Map false acquisition to the existing failed terminal with bounded safe busy detail and typed block-23 Busy/exit 3, with no eligibility event and terminal `ProcessedCount`, `UpdatedCount`, `SkippedCount`, and `FailedCount` all zero and no domain-failure increment.
- [ ] 3.3 Map attributable request/host cancellation during open, acquisition, or held execution to the existing cancellation/130 path; map open/acquisition/result-shape errors to safe infrastructure failure/5.
- [ ] 3.4 Link detected ownership loss into protected executor cancellation while preserving infrastructure/5 precedence over a racing cooperative cancellation.

## 4. Own, monitor, and release the session

- [ ] 4.1 Hold the exact dedicated connection from successful acquisition through protected work, terminal reporting, and finalization; add an injected-time five-second serialized health monitor and exactly-once loss notification.
- [ ] 4.2 Stop and await monitoring, execute parameterized `pg_advisory_unlock` on the owning session with a bounded cleanup token, require true, then dispose the connection exactly once.
- [ ] 4.3 On false/failed/ambiguous unlock or disposal failure, contribute infrastructure/5 without rewriting a flushed terminal and clear the associated Npgsql pool before disposal when reuse could retain ownership.
- [ ] 4.4 Confirm the Web local coordinator/`SemaphoreSlim` remains unchanged and complementary, and add no schema object, migration, retry, public setting, or new protocol terminal/event type.

## 5. Verify focused behavior

- [ ] 5.1 Add unit tests for exact ordering, parameter/key selection, acquired/busy/cancelled/infrastructure mapping, zero busy failed-asset count, safe diagnostics, and block-23 precedence.
- [ ] 5.2 Add deterministic lease tests for monitor serialization, caller cancellation, detected loss, loss/cancel races, explicit true unlock, false/throw/timeout unlock, pool sanitation, and exactly-once disposal without wall-clock delays.
- [ ] 5.3 Add real-PostgreSQL `Integration`-category tests for two-session contention, unlock then reacquire, owner-session close release, and pooling/release behavior; keep process spawning and the success/failure/cancellation/crash cross-process matrix in block 32.
- [ ] 5.4 Run `npm run test`, run `npm run test:integration` when PostgreSQL is configured, confirm default tests still exclude `Integration`, run strict OpenSpec validation/status, and review the diff for block-31-only planning scope.

## Audit Reconciliation

Advisory-lock Busy is canonical: after `run-started`, it emits no eligibility event and commits the reserved failed Busy terminal with all four terminal counts exactly zero (`ProcessedCount=0`, `UpdatedCount=0`, `SkippedCount=0`, `FailedCount=0`). It performs no executor or producer work and retains exit code 3 as evidence, not a domain failed-asset count.

