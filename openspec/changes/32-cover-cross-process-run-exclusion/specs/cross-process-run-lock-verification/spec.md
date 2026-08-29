## Purpose

Defines real-PostgreSQL process-boundary verification for worker exclusion, release, control-plane finality, and cleanup across independent operating-system processes.

## ADDED Requirements

### Requirement: Explicit PostgreSQL integration configuration
The integration suite SHALL use one documented test-only PostgreSQL connection-string contract, SHALL fail fast with safe actionable setup guidance when mandatory configuration or connectivity is absent during an explicit integration run, and SHALL derive child worker database settings without placing credentials in arguments, protocol captures, stdout, or diagnostics. The configured database SHALL be disposable or dedicated to the suite and SHALL NOT be a production Immich database.

#### Scenario: Explicit integration run lacks configuration
- **WHEN** the PostgreSQL cross-process tests are selected without the required test connection setting
- **THEN** they fail before starting a worker with a secret-free message naming the required setup contract rather than silently passing or skipping the core suite

#### Scenario: Configured server is unusable
- **WHEN** the setting is present but connection, advisory-lock, or required database-isolation capability cannot be established
- **THEN** setup fails before worker launch, reports only the safe failed capability, and performs bounded cleanup

### Requirement: Fixed production lock identity is isolated by database
Every cross-process case SHALL use block 31's exact production key and single-bigint PostgreSQL advisory-lock family. The suite SHALL prefer a unique disposable database per case when the configured role can create databases; otherwise it SHALL require a dedicated pre-provisioned database whose name has the documented `immich_reversegeo_test_` prefix, serialize fixed-key cases, and verify the key has no owner before and after each case. It SHALL NOT randomize the production lock key to obtain parallelism.

#### Scenario: Database creation is available
- **WHEN** the configured role can create and drop databases
- **THEN** each case uses a uniquely named database and both workers in that case target that same database

#### Scenario: Dedicated database fallback is used
- **WHEN** database creation is unavailable but a dedicated suite database is configured
- **THEN** fixed-key cases execute non-concurrently and prove no pre-existing or residual owner for the production key

### Requirement: The process harness exercises the production lock path
The suite SHALL launch independent test apphost processes using block 26's finalized descriptor, staging, stream, handshake, and process-lease patterns, but SHALL NOT add PostgreSQL scenarios to block 26's closed hermetic fixture. The block-32 apphost SHALL compose the applied worker host/executor/reporter and real block-31 lock collaborator, substituting only a deterministic post-lock domain-operation probe. The parent harness SHALL route its observations through the applied block-30 finalizer and coordinator/projection path. The production internal-worker descriptor SHALL also receive an uncontended no-work smoke test.

#### Scenario: Lock-owning worker reaches its hold point
- **WHEN** the first block-32 worker accepts execute
- **THEN** an accepted run-scoped handshake proves `run-started` occurred and the real lock gate admitted the post-lock domain probe before the test holds that process

#### Scenario: Production role smoke test
- **WHEN** the production internal-worker descriptor runs against an isolated uncontended no-work database/configuration
- **THEN** production role selection, composition, terminal reporting, and process exit complete without a fixture-only selector or hook

### Requirement: A contended worker reports the reserved busy outcome
After the first process reaches the post-lock hold handshake, the suite SHALL start a second independent process against the same database. The second process SHALL emit exactly one accepted Failed terminal with bounded safe busy detail, SHALL exit with code 3, SHALL emit no eligibility event and SHALL retain zero terminal `ProcessedCount`, `UpdatedCount`, `SkippedCount`, and `FailedCount`, and SHALL produce no post-lock domain-operation marker or database/domain mutation. Block 30 projection SHALL commit that Failed terminal once, add no contradiction anomaly or retry, clean activities, and return the exact coordinator handle to idle after process and stream finality.

#### Scenario: Second worker contends with held owner
- **WHEN** the first worker holds the production advisory key and the second worker starts against the same database
- **THEN** the second worker reports one valid Failed busy terminal, exits 3, performs no domain work, and is projected and released exactly once

#### Scenario: Busy is not domain failure accounting
- **WHEN** contention selects the reserved busy outcome
- **THEN** failed-asset count and every domain-effect probe remain zero even though the existing Failed terminal UI semantics are preserved

### Requirement: Every mandatory owner outcome releases for a fresh process
The suite SHALL prove a newly started second process can acquire the same production key after the first process reaches each mandatory terminal or death path: Completed success, executor/domain Failed with managed exit 4, cooperative Cancelled with exit 130, and test-induced abrupt process death without a terminal. The abrupt-death case SHALL be classified by block 30 as Failed missing-terminal/crash evidence rather than cancellation. Each reacquirer SHALL complete a valid no-work run with exit 0.

#### Scenario: Reacquire after orderly terminal outcomes
- **WHEN** the owner completes, fails in the controlled domain operation, or cooperatively cancels after lock acquisition
- **THEN** its process and streams finalize, its lock session releases, and a fresh process acquires and completes

#### Scenario: Reacquire after abrupt process death
- **WHEN** the test terminates the registered lock-owning process tree after the held handshake without sending Stop or allowing a terminal
- **THEN** PostgreSQL releases the session lock, block 30 records one Failed crash/missing-terminal finality, and a fresh process acquires and completes

### Requirement: Detected owner connection loss is covered when supported
The suite SHALL capability-detect whether its PostgreSQL role can terminate the exact test-owned backend. When supported, it SHALL terminate only the backend identified by the unique test application/session marker, require ownership-loss infrastructure finality with exit 5 when stdout remains healthy, stop further protected work after detected loss, and prove fresh-process reacquisition. Only this case MAY be reported inconclusive when the privilege is unavailable, with the missing capability stated explicitly.

#### Scenario: Test role can terminate the owner backend
- **WHEN** the harness terminates the exact lock-owning backend after the held handshake
- **THEN** the owner reports one infrastructure Failed outcome/exit 5, performs no later protected operation, and a fresh process acquires

#### Scenario: Backend termination privilege is unavailable
- **WHEN** explicit capability detection shows the configured role cannot terminate its test-owned backend
- **THEN** only the connection-loss case is inconclusive with a safe capability reason while contention and all other release cases remain mandatory

### Requirement: Coordinator projection and idle state are verified
For busy and every owner terminal/death path, the suite SHALL observe the finalized block-30 evidence path through the real coordinator/projection composition. It SHALL require exactly one terminal commitment, no duplicate summary or fatal effect, no activity residue, callback closure, no automatic retry, and release of only the matching coordinator handle after exit and stdout/stderr finality. A subsequent run SHALL be admitted only after that idle boundary.

#### Scenario: Worker finality precedes idle
- **WHEN** a worker terminates through any covered outcome
- **THEN** the coordinator becomes idle only after final classification, projection, activity cleanup, process/stream finality, and exact-session disposal

#### Scenario: Subsequent acquisition is admitted
- **WHEN** the prior coordinator handle is idle and the production key is free
- **THEN** a fresh worker is admitted and its successful no-work terminal is projected once

### Requirement: Handshakes and cleanup are deterministic and bounded
Expected ordering SHALL use flushed protocol events, atomic markers, backend identity publication, release/cancel gates, and process/stream completion rather than sleeps or polling delays. Finite deadlines SHALL serve only as failure and cleanup watchdogs. Each case SHALL use unique run IDs, resource roots, marker/capture paths, application names, and database names where available. Cleanup SHALL run unconditionally, terminate only registered process trees and scoped database sessions, await exit/stdout/stderr/disposal finality, remove test resources, and prove no registered PID or production-key owner remains.

#### Scenario: Assertion fails after owner starts
- **WHEN** a test faults after registering a worker or database session
- **THEN** cleanup releases gates, reaps only registered process trees/sessions, drains all streams, removes owned resources, and reports any residual PID or lock owner as a failure

#### Scenario: Expected phase ordering
- **WHEN** a contention or release case advances between phases
- **THEN** it advances from a positive handshake and never from an elapsed sleep

### Requirement: Test category and command ownership remain explicit
All PostgreSQL process tests SHALL carry the `Integration` category. The repository's default test command SHALL continue excluding Integration and Performance tests, and `npm run test:integration` SHALL include Integration while excluding Performance. Block 32 SHALL NOT add or modify CI workflows; block 69 owns CI provisioning and orchestration.

#### Scenario: Default test run
- **WHEN** `npm run test` executes without PostgreSQL integration configuration
- **THEN** the cross-process PostgreSQL tests do not execute and ordinary non-integration tests still run

#### Scenario: Explicit integration run
- **WHEN** `npm run test:integration` executes with valid PostgreSQL configuration
- **THEN** the cross-process tests are selected and the Performance category remains excluded

## Audit Reconciliation

The real-process Busy assertion must require the canonical sequence: `run-started`, no eligibility event, one failed Busy terminal whose four counts are all zero, and reserved exit evidence 3. It must also prove no executor/producer work, rather than accepting a merely zero aggregate result.

