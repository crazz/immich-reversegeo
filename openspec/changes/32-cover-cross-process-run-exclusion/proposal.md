## Why

Block 31 proves PostgreSQL advisory-lock semantics between in-process sessions, but it does not prove that independently launched workers preserve the busy terminal, exit, release, projection, and cleanup contracts. Process-level evidence is required before the Web coordinator can rely on cross-process exclusion and recovery.

## What Changes

- Add real-PostgreSQL, `Integration`-category tests that launch two independent OS worker processes against the same database and exact production advisory-lock key.
- Add a block-32-only PostgreSQL-aware integration worker apphost that reuses block 26's staging/handshake/reaper architecture and composes the real block-31 worker lock path; route it from the parent through the applied block-30 finalizer/coordinator path, while keeping block 26's hermetic fixture and production worker CLI unchanged.
- Hold the first worker deterministically only after accepted `run-started` and successful real lock acquisition, then prove the second emits one valid Failed busy terminal, exits 3, and performs no domain work or mutation.
- Prove release and fresh-process reacquisition after success, executor/domain failure, cooperative cancellation, abrupt process death, and privilege-gated PostgreSQL connection loss.
- Verify exact-once terminal projection, activity cleanup, no retry, and coordinator return to idle for busy and every owner outcome.
- Define fail-fast PostgreSQL configuration, fixed-key database isolation, time-bounded handshake, unique-resource, secret-handling, cleanup, and no-orphan contracts.
- Preserve default `npm run test` exclusion and explicit `npm run test:integration` inclusion; defer CI orchestration to block 69.

## Capabilities

### New Capabilities
- `cross-process-run-lock-verification`: Verifies real advisory-lock contention, terminal/exit mapping, release, reacquisition, control-plane projection, and cleanup across independent worker processes.

### Modified Capabilities
- None.

## Impact

Planning affects the future integration-test project, a test-only staged PostgreSQL worker apphost, PostgreSQL test provisioning/maintainer setup, and tests around the applied worker launcher, coordinator, projection, classifier, and advisory-lock lease. No production protocol, internal-worker CLI, database schema, lock key, retry policy, default test filter, or CI workflow changes are authorized.

## Audit Reconciliation

The real-process Busy assertion must require the canonical sequence: `run-started`, no eligibility event, one failed Busy terminal whose four counts are all zero, and reserved exit evidence 3. It must also prove no executor/producer work, rather than accepting a merely zero aggregate result.
