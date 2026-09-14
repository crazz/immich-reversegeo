## 1. Reconcile Finalized Owners and Scope

- [x] 1.1 Re-read the applied block 26 fixture staging/locator/descriptor/handshake/process-lease APIs, block 30 classifier/finalizer/projection-receipt/coordinator-release APIs, block 31 lock/lease/monitor/outcome APIs, and the Phase 2 coordinator/bridge composition; record exact reusable types and stop rather than adding parallel owners.
- [x] 1.2 Use the existing narrow internal `IProcessingRunDomainOperation.ExecuteAsync` post-lock seam, preserving its guarded session, exact production-domain closure, and linked token contract; do not expose lease/count context or add a public flag, production CLI branch, or default-backend switch.
- [x] 1.3 Keep edits scoped to block 32 integration support, tests, and maintainer setup guidance, plus the narrow compatible evidence-classifier repair for existing Failed exits 3/4/5 and paired private terminal-owned input half-close; do not change block 26's closed fixture modes, the production protocol or lock key, block 33, or CI workflows owned by block 69.

## 2. Define PostgreSQL Provisioning and Isolation

- [x] 2.1 Add a fail-fast parser for the test-only `IMMICH_REVERSEGEO_TEST_POSTGRES_CONNECTION_STRING` contract; reject missing/malformed/unreachable configuration in explicitly selected core integration tests with typed secret-free guidance before any worker starts.
- [x] 2.2 Derive each child's standard `DB_HOST`, `DB_PORT`, `DB_USERNAME`, `DB_PASSWORD`, and `DB_DATABASE_NAME` values by round-tripping the configured connection-string scalars through a literal production template; clear inherited `PG*`/`DB_*` values, retain encoded scalar values, assign a unique safe PostgreSQL application name, and keep secrets out of arguments, captures, stdout, test names, and diagnostics.
- [x] 2.3 Capability-detect advisory-lock access, database create/drop, backend inspection, and termination of the exact test-owned backend using bounded commands; distinguish mandatory setup failure from the optional connection-loss privilege.
- [x] 2.4 Implement database-per-case provisioning with safe unique names when create/drop is available, including the minimal disposable Immich-compatible schema/data needed for the production no-work smoke case.
- [x] 2.5 Implement the dedicated-database fallback only for a configured database named with the documented `immich_reversegeo_test_` prefix and owned by the configured role; before each serialized admission confirm the empty `public` schema and free `-7970420658158250032` key, leave unknown owners untouched, and revalidate all three invariants after cleanup releases a bounded completed attempt. The parent probe uses non-pooled connections and closes the physical connection before a database drop; warn that concurrent test processes require separate databases and never substitute a random key.
- [x] 2.6 Add maintainer setup guidance describing the setting, disposable/dedicated database requirement, least privileges, optional connection-loss capability, explicit commands, secret handling, and the fact that block 69—not block 32—will own CI provisioning.

## 3. Add the Block-32 Integration Worker Apphost

- [x] 3.1 Add a separate `net10.0` test apphost project under `tests/` and stage/locate it using block 26's exact build/publish and cross-platform conventions without modifying or referencing block 26's scenario CLI.
- [x] 3.2 In the child apphost, reference only the applied production worker assemblies needed to compose the real accepted-session host/executor/reporter, block-31 lock collaborator/lease, and block-23 outcome mapping; compose the applied block-30 finalizer/coordinator/projection owners only in the parent harness, and do not duplicate protocol DTOs, SQL/key constants, terminal types, or classification.
- [x] 3.3 Implement a strict closed test-only scenario parser for held success, controlled domain failure, cooperative cancellation, and connection-loss observation, accepting only unique non-secret resource/marker/gate paths and safe scenario tokens.
- [x] 3.4 Implement the controlled post-lock operation so, after the real lock path has verified ownership, it invokes the exact supplied production-domain closure once against the empty database to produce eligibility 0; only then atomically publish the entered/canary and backend PID/application markers and emit the typed log handshake, hold until explicit release/cancel/loss signals, and return on held success without invoking the closure again.
- [x] 3.5 Keep credentials and arbitrary database exceptions off stdout and safe diagnostics while publishing the post-eligibility, exact owned PostgreSQL backend PID/application marker for scoped connection-loss tests.
- [x] 3.6 Drive success, ordinary executor/domain failure, cancellation, and detected ownership loss through existing reporter/outcome paths so expected terminals/exits are Completed/0, Failed/4, Cancelled/130, and infrastructure Failed/5 rather than fixture-invented frames; preserve classifier strictness outside the compatible Failed 3/4/5 exit set.

## 4. Build Isolated Process and Database Leases

- [x] 4.1 Reuse the block 26 launcher descriptor, accepted-event collector, PID/handle registration, stream drainage, bounded deadlines, and last-chance reaper patterns for the new apphost and production application descriptor.
- [x] 4.2 Create one aggregate case lease with unique database or dedicated-database serialization lease, run IDs, resource roots, capture/marker/gate paths, application-name prefix, process handles, coordinator generation, and database sessions.
- [x] 4.3 Implement idempotent unconditional cleanup that releases safe gates, closes stdin, terminates only registered process trees, awaits exit/stdout/stderr/finalizer/disposal, verifies PIDs are gone, checks the exact key, closes scoped sessions/pools, drops only case-owned databases, and deletes resource roots.
- [x] 4.4 Add bounded behavior and cleanup watchdogs that report only safe phase/PID/application/database identifiers; use them to fail and reap hangs, never to order an expected transition.
- [x] 4.5 Inject an assertion failure after owner registration and run multiple database-per-case instances where supported to prove no process, marker, capture, backend, lock owner, or database crosses cases; keep dedicated-database fallback cases serialized.

## 5. Verify Production Composition and Contention

- [x] 5.1 Launch the exact production internal-worker descriptor against the isolated minimal no-work database and assert ready, execute capture, `run-started`, zero eligibility, one Completed terminal, exit 0, stdout/stderr finality, disposal, and no residual production-key owner without any fixture selector.
- [x] 5.2 Start owner process A, await accepted ready/execute/`run-started` plus the atomic post-lock-held handshake, then start independent contender process B against the same database through the real launcher/coordinator projection composition.
- [x] 5.3 Assert B emits exactly one valid Failed busy terminal with safe detail, exits 3, emits no eligibility event and has zero terminal `ProcessedCount`, `UpdatedCount`, `SkippedCount`, and `FailedCount`, produces no domain/heavy/producer work inside the already-invoked executor and no canary/database effect, records no contradiction anomaly, schedules no retry, and reaches stream/disposal finality.
- [x] 5.4 Assert B's Failed terminal is projected once through block 30, all activities/callbacks close, no duplicate fatal/summary effect occurs, and only B's matching coordinator handle becomes idle after full finality.
- [x] 5.5 Release A through controlled success, await its terminal/exit/drains/projection/idle, and start a fresh process to prove the same exact key is acquirable after contention cleanup.

## 6. Verify the Release and Reacquisition Matrix

- [x] 6.1 For a held owner released to controlled success, assert committed Completed/0, exact cleanup/idle, no residual key owner, and fresh-process no-work Completed/0 reacquisition.
- [x] 6.2 For a held owner released to controlled domain failure, assert committed Failed/4 with ordinary domain-failure semantics, exact cleanup/idle, and fresh-process reacquisition.
- [x] 6.3 For a held owner sent the exact correlated cancel, assert cooperative Cancelled/130 with no tree kill or fatal/anomaly, exact cleanup/idle, and fresh-process reacquisition.
- [x] 6.4 For a held owner terminated abruptly through its registered process-tree handle without Stop, assert no worker terminal, block-30 Failed missing-terminal/crash finality after exit and both drains, no cancellation classification, PostgreSQL session release, exact coordinator idle, and fresh-process reacquisition without asserting a portable kill exit number.
- [x] 6.5 When capability detection permits, terminate only the published test-owned PostgreSQL backend, assert block-31 ownership loss stops later protected work and produces infrastructure Failed/5 without a spurious `TerminalExitMismatch` when output remains healthy, then prove exact cleanup/idle and fresh-process reacquisition.
- [x] 6.6 When backend termination is not permitted, mark only task 6.5's test inconclusive with the safe missing-capability reason; keep contention and tasks 6.1–6.4 mandatory.
- [x] 6.7 Across every row, assert exactly one projection receipt/terminal mutation, expected counter/fatal/summary behavior, no activity residue, callback closure, no retry, process/stream/disposal finality before exact-handle idle, and successful later coordinator admission.

## 7. Verify Categories, Commands, and Final Scope

- [x] 7.1 Mark every new PostgreSQL/process case with `[TestCategory("Integration")]` and add a focused filterable test class/name without weakening the repository's Integration/Performance semantics.
- [x] 7.2 Run the focused block-32 integration tests repeatedly against database-per-case provisioning and, when available, the serialized dedicated-database fallback; confirm all registered process/backend/database resources are gone after each run.
- [x] 7.3 Run `npm run test:integration` with valid PostgreSQL configuration and prove block-32 cases are selected while Performance remains excluded.
- [x] 7.4 Run `npm run test` without the test PostgreSQL setting and prove block-32 Integration cases do not execute while ordinary non-integration tests do; change `package.json`/runsettings only if this verification reveals an actual defect.
- [x] 7.5 Run a clean build followed by focused `--no-build` integration tests and a test-project publish/staging smoke on the current platform to verify both the block-32 apphost and production apphost locators.
- [x] 7.6 Run `openspec validate 32-cover-cross-process-run-exclusion --strict`, `openspec status --change 32-cover-cross-process-run-exclusion`, and final diff/status review; reconcile every warning/error and confirm no block-33 or CI workflow file changed.

## 8. Repair Accepted-Terminal Input Finality

- [x] 8.1 Re-read the existing session stdin writer, terminal acceptance, memoized close owner, cancellation operation, finalizer, and child command-pump owners; preserve their ownership boundaries rather than introducing a detached pump or native-platform-I/O subsystem.
- [x] 8.2 Immediately after a fully validated exact-correlated terminal is recorded and before sink callback admission, seal new stdin writes, start a separately tracked non-stdout-pump-awaited close action, allow an already admitted canonical write/flush to settle under the existing sole writer, and invoke the existing controller-input half-close owner exactly once. Keep invalid, wrong-correlated, and unaccepted terminal frames unable to trigger that close. Before exit, directly observe physical close failure, retain the typed independent `TerminalInputCloseFailed` fact and accepted terminal even when an earlier sink fault exists, and enter existing bounded InputTransport containment without self-joining; post-exit cleanup remains best effort.
- [x] 8.3 Keep successful terminal-owned input completion separate from Stop: it must not latch cancellation, send a cancel, create or reset grace, kill a process, cancel the executor, or alter accepted-terminal/raw-exit authority; a typed pre-exit physical-close failure instead joins only existing bounded containment. Retain process ownership until exit, both stream drains, finalizer settlement, and disposal complete.
- [x] 8.4 Retain the child command pump's structured cancellation/disposal/join behavior and make peer EOF after an accepted terminal its deterministic normal completion path; document that local `Console.OpenStandardInput` cancellation/disposal alone cannot be promised to unblock a pending native read.
- [x] 8.5 Add a real-process regression in which a normal fully validated terminal is accepted while child stdin remains open and prove the child exits after the one terminal-owned half-close, with stdout/stderr drains, finalizer/disposal settlement, and coordinator idle. Add writer/cancel/terminal, callback-suppression-or-failure, invalid-or-wrong-correlation terminal, disposal-race, and synthetic pre-exit close-failure coverage proving exact-once close, typed bounded InputTransport containment with accepted-terminal preservation, compound earlier-sink/later-close evidence and continued containment, and preserved Stop/fault semantics.

## Audit Reconciliation

The real-process Busy assertion must require the canonical sequence: `run-started`, no eligibility event, one failed Busy terminal whose four counts are all zero, and reserved exit evidence 3. It must also prove no domain, heavy, or producer work inside the already-invoked executor, rather than accepting a merely zero aggregate result.
