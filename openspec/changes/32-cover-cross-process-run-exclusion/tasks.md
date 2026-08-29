## 1. Reconcile Finalized Owners and Scope

- [ ] 1.1 Re-read the applied block 26 fixture staging/locator/descriptor/handshake/process-lease APIs, block 30 classifier/finalizer/projection-receipt/coordinator-release APIs, block 31 lock/lease/monitor/outcome APIs, and the Phase 2 coordinator/bridge composition; record exact reusable types and stop rather than adding parallel owners.
- [ ] 1.2 Confirm the executor exposes a narrow post-lock domain-operation injection seam; if it does not, add only an internal interface whose production registration delegates unchanged to the real executor, with no test flag or branch in the production internal-worker CLI.
- [ ] 1.3 Keep edits scoped to block 32 integration support, tests, and maintainer setup guidance; do not change block 26's closed fixture modes, the production protocol or lock key, default runtime behavior, block 33, or CI workflows owned by block 69.

## 2. Define PostgreSQL Provisioning and Isolation

- [ ] 2.1 Add a fail-fast parser for the test-only `IMMICH_REVERSEGEO_TEST_POSTGRES_CONNECTION_STRING` contract; reject missing/malformed/unreachable configuration in explicitly selected core integration tests with typed secret-free guidance before any worker starts.
- [ ] 2.2 Derive each child's standard `DB_HOST`, `DB_PORT`, `DB_USERNAME`, `DB_PASSWORD`, and `DB_DATABASE_NAME` environment from the parent setting without placing secrets in arguments, captures, stdout, test names, or diagnostics; assign a unique safe PostgreSQL application name.
- [ ] 2.3 Capability-detect advisory-lock access, database create/drop, backend inspection, and termination of the exact test-owned backend using bounded commands; distinguish mandatory setup failure from the optional connection-loss privilege.
- [ ] 2.4 Implement database-per-case provisioning with safe unique names when create/drop is available, including the minimal disposable Immich-compatible schema/data needed for the production no-work smoke case.
- [ ] 2.5 Implement the dedicated-database fallback only for a configured database named with the documented `immich_reversegeo_test_` prefix, with explicit ownership confirmation, nonparallel fixed-key execution, before/after inspection for `-7970420658158250032`, and a warning that concurrent test processes require separate databases; never substitute a random key.
- [ ] 2.6 Add maintainer setup guidance describing the setting, disposable/dedicated database requirement, least privileges, optional connection-loss capability, explicit commands, secret handling, and the fact that block 69—not block 32—will own CI provisioning.

## 3. Add the Block-32 Integration Worker Apphost

- [ ] 3.1 Add a separate `net10.0` test apphost project under `tests/` and stage/locate it using block 26's exact build/publish and cross-platform conventions without modifying or referencing block 26's scenario CLI.
- [ ] 3.2 In the child apphost, reference only the applied production worker assemblies needed to compose the real accepted-session host/executor/reporter, block-31 lock collaborator/lease, and block-23 outcome mapping; compose the applied block-30 finalizer/coordinator/projection owners only in the parent harness, and do not duplicate protocol DTOs, SQL/key constants, terminal types, or classification.
- [ ] 3.3 Implement a strict closed test-only scenario parser for held success, controlled domain failure, cooperative cancellation, and connection-loss observation, accepting only unique non-secret resource/marker/gate paths and safe scenario tokens.
- [ ] 3.4 Implement the controlled post-lock domain operation so it atomically publishes an entered/canary marker and emits a valid typed log handshake only after the real lock gate invokes it, then waits on explicit release/cancel/loss signals with no sleep or polling.
- [ ] 3.5 Publish the exact owned PostgreSQL backend PID/application marker after acquisition for scoped connection-loss tests, while keeping credentials and arbitrary database exceptions off stdout and safe diagnostics.
- [ ] 3.6 Add success, ordinary executor/domain failure, and cancellation behavior through existing reporter/outcome paths so expected terminals/exits are Completed/0, Failed/4, and Cancelled/130 rather than fixture-invented frames.

## 4. Build Isolated Process and Database Leases

- [ ] 4.1 Reuse the block 26 launcher descriptor, accepted-event collector, PID/handle registration, stream drainage, bounded deadlines, and last-chance reaper patterns for the new apphost and production application descriptor.
- [ ] 4.2 Create one aggregate case lease with unique database or dedicated-database serialization lease, run IDs, resource roots, capture/marker/gate paths, application-name prefix, process handles, coordinator generation, and database sessions.
- [ ] 4.3 Implement idempotent unconditional cleanup that releases safe gates, closes stdin, terminates only registered process trees, awaits exit/stdout/stderr/finalizer/disposal, verifies PIDs are gone, checks the exact key, closes scoped sessions/pools, drops only case-owned databases, and deletes resource roots.
- [ ] 4.4 Add bounded behavior and cleanup watchdogs that report only safe phase/PID/application/database identifiers; use them to fail and reap hangs, never to order an expected transition.
- [ ] 4.5 Inject an assertion failure after owner registration and run multiple database-per-case instances where supported to prove no process, marker, capture, backend, lock owner, or database crosses cases; keep dedicated-database fallback cases serialized.

## 5. Verify Production Composition and Contention

- [ ] 5.1 Launch the exact production internal-worker descriptor against the isolated minimal no-work database and assert ready, execute capture, `run-started`, zero eligibility, one Completed terminal, exit 0, stdout/stderr finality, disposal, and no residual production-key owner without any fixture selector.
- [ ] 5.2 Start owner process A, await accepted ready/execute/`run-started` plus the atomic post-lock-held handshake, then start independent contender process B against the same database through the real launcher/coordinator projection composition.
- [ ] 5.3 Assert B emits exactly one valid Failed busy terminal with safe detail, exits 3, emits no eligibility event and has zero terminal `ProcessedCount`, `UpdatedCount`, `SkippedCount`, and `FailedCount`, produces no domain/canary/database effect, records no contradiction anomaly, schedules no retry, and reaches stream/disposal finality.
- [ ] 5.4 Assert B's Failed terminal is projected once through block 30, all activities/callbacks close, no duplicate fatal/summary effect occurs, and only B's matching coordinator handle becomes idle after full finality.
- [ ] 5.5 Release A through controlled success, await its terminal/exit/drains/projection/idle, and start a fresh process to prove the same exact key is acquirable after contention cleanup.

## 6. Verify the Release and Reacquisition Matrix

- [ ] 6.1 For a held owner released to controlled success, assert committed Completed/0, exact cleanup/idle, no residual key owner, and fresh-process no-work Completed/0 reacquisition.
- [ ] 6.2 For a held owner released to controlled domain failure, assert committed Failed/4 with ordinary domain-failure semantics, exact cleanup/idle, and fresh-process reacquisition.
- [ ] 6.3 For a held owner sent the exact correlated cancel, assert cooperative Cancelled/130 with no tree kill or fatal/anomaly, exact cleanup/idle, and fresh-process reacquisition.
- [ ] 6.4 For a held owner terminated abruptly through its registered process-tree handle without Stop, assert no worker terminal, block-30 Failed missing-terminal/crash finality after exit and both drains, no cancellation classification, PostgreSQL session release, exact coordinator idle, and fresh-process reacquisition without asserting a portable kill exit number.
- [ ] 6.5 When capability detection permits, terminate only the published test-owned PostgreSQL backend, assert block-31 ownership loss stops later protected work and produces infrastructure Failed/5 when output remains healthy, then prove exact cleanup/idle and fresh-process reacquisition.
- [ ] 6.6 When backend termination is not permitted, mark only task 6.5's test inconclusive with the safe missing-capability reason; keep contention and tasks 6.1–6.4 mandatory.
- [ ] 6.7 Across every row, assert exactly one projection receipt/terminal mutation, expected counter/fatal/summary behavior, no activity residue, callback closure, no retry, process/stream/disposal finality before exact-handle idle, and successful later coordinator admission.

## 7. Verify Categories, Commands, and Final Scope

- [ ] 7.1 Mark every new PostgreSQL/process case with `[TestCategory("Integration")]` and add a focused filterable test class/name without weakening the repository's Integration/Performance semantics.
- [ ] 7.2 Run the focused block-32 integration tests repeatedly against database-per-case provisioning and, when available, the serialized dedicated-database fallback; confirm all registered process/backend/database resources are gone after each run.
- [ ] 7.3 Run `npm run test:integration` with valid PostgreSQL configuration and prove block-32 cases are selected while Performance remains excluded.
- [ ] 7.4 Run `npm run test` without the test PostgreSQL setting and prove block-32 Integration cases do not execute while ordinary non-integration tests do; change `package.json`/runsettings only if this verification reveals an actual defect.
- [ ] 7.5 Run a clean build followed by focused `--no-build` integration tests and a test-project publish/staging smoke on the current platform to verify both the block-32 apphost and production apphost locators.
- [ ] 7.6 Run `openspec validate 32-cover-cross-process-run-exclusion --strict`, `openspec status --change 32-cover-cross-process-run-exclusion`, and final diff/status review; reconcile every warning/error and confirm no block-33 or CI workflow file changed.

## Audit Reconciliation

The real-process Busy assertion must require the canonical sequence: `run-started`, no eligibility event, one failed Busy terminal whose four counts are all zero, and reserved exit evidence 3. It must also prove no executor/producer work, rather than accepting a merely zero aggregate result.
