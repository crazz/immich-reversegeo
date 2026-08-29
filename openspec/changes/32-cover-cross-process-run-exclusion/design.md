## Context

See [proposal.md](proposal.md) for motivation and [specs/cross-process-run-lock-verification/spec.md](specs/cross-process-run-lock-verification/spec.md) for the verification contract. Block 26 supplies a hermetic staged apphost, shell-free descriptor construction, accepted-event handshakes, process leases, complete stream drainage, and orphan reaping. Its closed fixture intentionally references no production worker composition or PostgreSQL. Block 30 owns terminal authority, busy/crash/cancellation/infrastructure classification, projection receipts, cleanup ordering, and exact coordinator-handle release. Block 31 owns the stable key, real acquisition/lease/monitor/release path, and in-process PostgreSQL session tests.

The root scripts already provide the desired category split: `npm run test` excludes Integration and Performance, while `npm run test:integration` selects Integration under `integration.runsettings`, which still excludes Performance. `tests/ImmichReverseGeo.Tests/IntegrationTests.cs` is only a placeholder. CI database/container ownership remains block 69.

## Goals / Non-Goals

**Goals:**

- Exercise the real block-31 acquisition and lease across two independent OS processes using the exact production key and one real PostgreSQL database.
- Make lock ownership, contention, owner release, process death, connection loss, process/stream finality, projection, and coordinator idle visible through positive handshakes.
- Prove busy has a valid Failed terminal, exit 3, zero domain accounting/effects, and no retry.
- Prove fresh-process reacquisition after success, domain failure, cooperative cancel, abrupt death, and supported connection loss.
- Keep PostgreSQL credentials, processes, databases, and fixed-key ownership isolated and clean on every test outcome.
- Smoke-test actual production internal-worker role selection/composition separately from the controllable matrix.

**Non-Goals:**

- Change block 26's fixture CLI or make it PostgreSQL-aware.
- Add fixture-only switches, gates, environment variables, or domain substitutes to the production internal-worker CLI/composition root.
- Re-test every block-31 unit/session case, block-30 classifier category, or block-26 launcher fault mode.
- Run live Overture/GADM work, mutate a real Immich installation, randomize the production key, add schema to production, or add automatic retry.
- Add CI services/workflows; block 69 owns that work.
- Touch block 33's backend switch.

## Decisions

### 1. Add a separate block-32 integration apphost, not a block-26 mode

Create a dedicated test apphost under `tests/` for this change and stage it beside the main test output by reusing block 26's exact MSBuild/locator conventions. Do not edit block 26's closed scenario parser or executable. The new apphost may reference the applied production worker assemblies needed to compose the real worker host/executor/reporter, advisory-lock collaborator, and outcome mapper. The parent test harness—not the child apphost—composes the applied launcher, block-30 finalizer, coordinator, and projection owners.

Its closed test-only scenarios are limited to controlled success, domain failure, cooperative cancellation, held owner, and connection-loss observation. Valid stdout and stdin still use the finalized shared protocol. Scenario/control values travel as discrete non-secret arguments and unique file paths; database credentials are inherited through standard child `DB_HOST`, `DB_PORT`, `DB_USERNAME`, `DB_PASSWORD`, and `DB_DATABASE_NAME` variables derived in the parent.

**Alternative considered:** extend block 26. Rejected because its finalized purpose and verification boundary explicitly prohibit PostgreSQL and production worker composition. **Alternative considered:** put a hold switch in production `--internal-worker`. Rejected because test control would become a production invocation surface. **Alternative considered:** use a toy process that directly calls `pg_try_advisory_lock`. Rejected because it would not verify worker terminal/exit/finalizer integration.

### 2. Compose the production lock/session path and substitute only post-lock domain work

The integration apphost invokes the same accepted-session executor/reporter entry and real lock collaborator used by production. It replaces only the collaborator invoked after successful lock admission for domain execution, through the narrow executor-operation seam finalized by prior blocks. If the applied API lacks such an injectable seam, add the narrowest internal composition interface whose production registration delegates unchanged to the real processing executor; do not branch on test mode inside production logic.

The controlled domain operation atomically publishes its entered marker and emits a normal typed log marker only after it is invoked. Because block 31 places lock acquisition immediately after `run-started` and before domain invocation, observing accepted `run-started` followed by this marker proves the real lock gate admitted the owner. The operation then waits on an explicit release, cancel, or connection-loss signal. Failure throws the controlled ordinary executor exception so block 23 selects domain failure/4. No marker means the domain operation was never reached.

A test-owned canary records every controlled domain invocation and mutation attempt. Busy assertions require no marker/canary change for the contender, zero terminal counts, and no changes to any minimal disposable database rows used by the case.

**Alternative considered:** have a parent test connection hold the key. Rejected as the primary matrix because it proves contention but not that a first worker retains ownership through its run. It may be used only in setup diagnostics.

### 3. Separately smoke the actual production internal-worker descriptor

The controllable apphost verifies the exact production worker executor/lock internals, while the parent verifies the production launcher/finalizer/coordinator path; it intentionally bypasses production role selection. Add one uncontended production-descriptor smoke case against a unique disposable database containing only the minimal Immich-compatible schema/data needed to reach a no-work terminal. Launch the exact block-24/production `--internal-worker` descriptor with no fixture selector, send the canonical request, and require ready, `run-started`, zero eligibility, Completed terminal, exit 0, stream finality, and no residual lock.

This smoke does not provide the hold matrix and does not load live geodata because the zero-work gate prevents it. Test-created schema exists only in the disposable integration database and is not a product migration.

**Alternative considered:** use only the production executable for every case. Rejected because deterministic post-lock holds and injected failures would require production test hooks or expensive/live domain work.

### 4. Use one fail-fast test connection-string contract

Document one test-only setting, `IMMICH_REVERSEGEO_TEST_POSTGRES_CONNECTION_STRING`, in maintainer integration-test guidance. It identifies a non-production PostgreSQL database/server and role. Explicitly selected core tests treat absence as failure, not an ignored or passed test. Setup parses it with Npgsql, opens a bounded connection, confirms server identity/version, confirms advisory-lock commands, and determines isolation capabilities before any child starts. Error output names the setting and failed capability but never renders the connection string, exception detail containing secrets, or derived child environment.

The harness derives standard `DB_*` variables for child processes. Passwords never appear in command arguments, capture files, stdout, test names, snapshots, or failure diagnostics. Each child receives a unique safe PostgreSQL `ApplicationName` so scoped backend inspection/termination cannot target unrelated sessions.

**Alternative considered:** reuse ambient production `DB_*` directly. Rejected because a developer could accidentally target a real Immich database and because setup cannot clearly distinguish integration ownership.

### 5. Prefer database-per-case; serialize the fixed-key fallback

At assembly setup, capability-detect `CREATE DATABASE`. When available, create a uniquely named database per case from the configured maintenance connection, connect both child processes to it, and drop it after clearing pools and terminating only sessions bearing the case's application-name prefix. Database names are generated from a safe fixed prefix plus GUID and quoted through Npgsql APIs/validated identifiers.

When database creation is unavailable, require the configured database name to begin with `immich_reversegeo_test_` as an explicit dedicated-integration safety marker, mark the fixed-key class nonparallel, and take a test-harness serialization lease. Before and after every case, query `pg_locks`/`pg_stat_activity` for the exact key and fail on an unexpected owner. All cases still have unique run IDs, process roots, marker paths, and application names. The key remains `-7970420658158250032`; using random keys would avoid the contract under test.

The fallback protects cases within one test process. Documentation warns that concurrent test processes must use separate dedicated databases. Database-per-case is the supported parallel path.

### 6. Drive contention entirely from positive handshakes

For the contention case:

1. Provision the isolated database and unique resources.
2. Register/start owner process A before awaiting output.
3. Await accepted ready, execute capture, accepted `run-started`, and atomic post-lock-held marker.
4. Start/register contender process B through the real launcher and coordinator projection composition.
5. Await B's accepted Failed busy terminal, exit 3, stdout/stderr finality, finalizer receipt, and coordinator idle.
6. Assert B has zero domain counts, no domain marker/canary/database effect, no anomaly, and no retry.
7. Release A, await its selected terminal/exit/drains/finalization/idle, then start process C or a fresh B lease to prove acquisition.

Every await has a finite diagnostic deadline. The deadline fails/reaps a hung case; it never establishes that A probably holds the lock or that B probably did no work. No `Thread.Sleep`, `Task.Delay` for ordering, or file polling is allowed. Atomic file publication is paired with a protocol event, so the event is the availability handshake.

### 7. Use one release/reacquisition matrix with explicit expected authority

Each row starts a fresh owner OS process, reaches the post-lock-held handshake, drives one outcome, waits for block-30 finality and complete cleanup, then starts a fresh reacquirer against the same database:

| Owner path | Owner authority/evidence | Reacquirer |
|---|---|---|
| controlled success | committed Completed, exit 0 | no-work Completed/0 |
| controlled domain failure | committed Failed, managed exit 4 | no-work Completed/0 |
| cooperative cancellation | exact correlated cancel, committed Cancelled, exit 130, no kill | no-work Completed/0 |
| abrupt test-induced process death | no terminal; synthesized Failed missing-terminal/crash after exit/drains | no-work Completed/0 |
| detected lock-session termination | committed/synthesized infrastructure Failed, exit 5 when output remains healthy | no-work Completed/0 |

Abrupt death is induced only after the held handshake by terminating the registered process tree without latching Stop; it must not be classified Cancelled. This is distinct from block 28's forced kill after exact-session Stop. Cooperative cancellation uses the real cancel path, not process termination.

For connection loss, the child atomically publishes its exact PostgreSQL backend PID/application marker after acquisition. The parent verifies it belongs to the case and capability-detects `pg_terminate_backend` on that backend. If permitted, terminate it, await block 31's ownership monitor/loss token and infrastructure path, assert no later protected operation begins, then reacquire. If not permitted, mark only this row inconclusive with the missing privilege; never weaken or skip the core rows.

### 8. Verify projection and coordinator release, not only PostgreSQL state

Use the applied real coordinator/bridge/finalizer composition around the launched session wherever prior APIs allow. The direct external owner and coordinator-launched contender remain separate processes; local admission is therefore not mistaken for database contention. For each observable run, assert the expected terminal receipt, exact counters, one summary/fatal effect as defined by block 30, no active activity, callbacks closed, no supplementary contradiction, no retry, disposal complete, and only the matching coordinator generation released.

Coordinator `IsIdle` alone is insufficient. The assertion waits for terminal classification/projection, exit and both pipe finality, session disposal, and exact-handle detachment. The next coordinator admission and fresh database acquisition then prove recovery rather than merely inspecting a flag.

### 9. Cleanup is layered, idempotent, and scoped

Each case owns one aggregate lease registered before behavioral awaits. Cleanup runs from `finally` and assembly-level last-chance support:

1. signal any release/cancel gate that is still safe;
2. close controller stdin where owned;
3. terminate only registered live process trees, retaining handles to avoid PID reuse;
4. await exit and complete stdout/stderr pumps;
5. dispose launcher/finalizer/coordinator leases exactly once;
6. verify registered PIDs are gone;
7. inspect the exact database/key for residual ownership;
8. terminate only scoped application-name sessions when needed;
9. clear relevant pools, drop a case-owned database, and remove unique resource roots.

Cleanup has a bounded watchdog and reports PIDs, safe application names, database IDs, and phases—but not secrets. It never terminates arbitrary PostgreSQL backends or searches/kills processes by name. An injected post-start assertion failure test proves this path. A residual owner or orphan fails the test even if primary assertions passed.

### 10. Preserve command/category ownership and defer CI

Place all new process/PostgreSQL cases under `[TestCategory("Integration")]`; optionally also tag a narrower class trait only if the existing test framework supports it without changing selection semantics. Verify both command paths:

- `npm run test`: Integration and Performance remain excluded and at least one ordinary test executes.
- `npm run test:integration`: Integration is selected through the current script/settings and Performance remains excluded.

Do not alter `.runsettings`, `integration.runsettings`, or scripts unless verification exposes a real selection defect. Do not add Docker services, workflow YAML, secrets, or CI matrices; record the provisioning contract for block 69 to consume later.

## Risks / Trade-offs

- **[The controllable apphost could drift from production composition]** → Compose the applied production host/executor/lock/finalizer types and add the actual production-descriptor no-work smoke case; substitute only the post-lock domain operation.
- **[A test setting could target production]** → Require explicit test-only configuration, disposable/dedicated ownership checks, safe naming, and documentation that refuses an unconfirmed target before worker launch.
- **[Fixed-key cases interfere in one shared database]** → Prefer database-per-case; otherwise require a dedicated database, nonparallel execution, before/after lock inspection, and separate databases for concurrent test processes.
- **[Abrupt process death reports platform-specific exit codes]** → Assert block-30 crash/missing-terminal authority and process finality, not an exact externally killed numeric status.
- **[Connection-loss privilege is unavailable]** → Capability-detect and make only that row inconclusive; all portable release paths remain mandatory.
- **[A deadline becomes a flaky ordering mechanism]** → Require a positive handshake before every action; deadlines only fail and initiate cleanup.
- **[Cleanup harms unrelated processes or sessions]** → Retain exact process handles and unique application/database identities; never kill by process name or unscoped backend query.
- **[Integration setup leaks credentials]** → Keep the parent connection string out of arguments/output and render only typed safe setup failures.

## Migration Plan

1. Re-read the applied blocks 26, 30, and 31 plus coordinator/bridge APIs and record the exact reusable staging, executor-operation, lock, finalizer, projection-receipt, session, and cleanup seams; stop rather than duplicate owners.
2. Add fail-fast PostgreSQL configuration, capability detection, database isolation, minimal no-work schema provisioning, and scoped cleanup support.
3. Add and stage the separate block-32 integration apphost by reusing block 26 infrastructure; compose the production lock/session/finalizer path and controlled post-lock domain operation.
4. Add production internal-worker no-work smoke coverage, then contention/busy/no-effects coverage.
5. Add the release/reacquisition matrix, connection-loss capability row, coordinator projection/idle assertions, and adversarial cleanup/isolation tests.
6. Run focused and full explicit integration tests, the default suite, strict OpenSpec validation/status, and block-32-only scope review.

Rollback removes only the block-32 integration apphost, test harness/tests, and maintainer provisioning guidance. It changes no production schema, lock identity, runtime behavior, default test selection, or CI workflow.

## Audit Reconciliation

The real-process Busy assertion must require the canonical sequence: `run-started`, no eligibility event, one failed Busy terminal whose four counts are all zero, and reserved exit evidence 3. It must also prove no executor/producer work, rather than accepting a merely zero aggregate result.
