## Why

The Web process's local semaphore cannot exclude independently started workers or duplicate containers that target the same Immich database. A database-scoped advisory lock is needed at the accepted worker-session boundary without changing Immich's schema.

## What Changes

- Add a worker-side, non-blocking PostgreSQL advisory run lock held by one dedicated open connection.
- Enter the lock gate as the first executor/session step after `run-started`, before eligibility, snapshots, geodata, or mutation; keep exact-once executor invocation and existing terminal ownership intact.
- Reserve a documented, versioned signed-bigint key and acquire it with `pg_try_advisory_lock` in the target Immich database.
- Map contention to an existing valid failed terminal plus busy exit code 3, without incrementing the domain failed-asset count.
- Treat acquisition, ownership-loss, unlock, and disposal failures as infrastructure outcomes; preserve cooperative cancellation semantics and sanitize pooled sessions.
- Keep the Web local lock as a complementary admission boundary and make no table, index, migration, or other schema change.
- Add unit seams and real-PostgreSQL `Integration` coverage for connection/session behavior; leave the cross-process matrix to block 32.

## Capabilities

### New Capabilities
- `cross-process-run-locking`: Excludes concurrent heavy processing across workers that use the same Immich PostgreSQL database and the same lock-key version.

### Modified Capabilities
- None.

## Impact

The accepted worker executor/session, its block-23 typed outcome accumulator, worker dependency composition, and Npgsql connection lifecycle gain a lock collaborator and lease. Focused unit tests and PostgreSQL-backed `Integration` tests are added; Web-local coordination remains unchanged and no Immich schema objects are added.

## Audit Reconciliation

Advisory-lock Busy is canonical: after `run-started`, it emits no eligibility event and commits the reserved failed Busy terminal with all four terminal counts exactly zero (`ProcessedCount=0`, `UpdatedCount=0`, `SkippedCount=0`, `FailedCount=0`). It performs no executor or producer work and retains exit code 3 as evidence, not a domain failed-asset count.

