## Purpose

Protects the admitted empty scheduled-pass optimization with deterministic regression coverage that proves local completion without worker, protocol, database, geodata, or heavy dependency effects.

## ADDED Requirements

### Requirement: Accepted empty schedule performs one advisory detection
The automated test suite SHALL verify that a process-locally admitted scheduled occurrence whose detector completes normally with no work invokes that detector exactly once with the admitted request's cancellation token. A detector cancellation, detector failure, or locally busy occurrence MUST NOT be treated as this empty outcome.

#### Scenario: Admitted detector reports no work
- **WHEN** a scheduled occurrence owns local admission and its detector completes normally with a no-work decision
- **THEN** exactly one detector operation is observed for that admitted request

#### Scenario: Empty outcome boundary remains distinct
- **WHEN** the focused regression is executed
- **THEN** it starts with admission available and observes neither cancellation nor failure presentation, while the separate block-35 coverage remains authoritative for locally busy, duplicate, cancelled, and failed occurrences

### Requirement: Empty schedule materializes no worker or heavy graph
The automated test suite SHALL deterministically verify that the admitted no-work path does not resolve a processing backend, build a worker command, start a process, access a worker protocol/session or worker-event state bridge, resolve geodata, or construct any forbidden heavy dependency. The verification MUST use fail-on-resolution factories, throwing sentinels, or constructor counters rather than inferring laziness only from the absence of external symptoms.

#### Scenario: Detector returns no work before backend resolution
- **WHEN** the admitted detector returns a normal no-work decision
- **THEN** backend resolution, command construction, launcher and process-start calls, protocol/session and worker-event bridge access, and worker event/result input all remain zero

#### Scenario: Heavy dependencies remain unmaterialized
- **WHEN** the local empty finalizer completes the admitted request
- **THEN** skipped/config/batch collaborators and Overture, GADM, airport, country-index, resolver, and in-process execution dependencies have zero resolution, construction, and operation counts

### Requirement: Empty schedule completes through the exact local zero lifecycle
The automated test suite SHALL verify the accepted request's established local transition from pending state to zero eligibility/start and then completion. It SHALL observe total, processed, skipped, and error counts of zero; no last error or activity; bounded start and completion timestamps; the exact nothing-to-process log before the exact zero summary; no worker event or result; and terminal release of the matching request, cancellation owner, callbacks, and coordinator handle to idle.

#### Scenario: Local zero finalization reaches clean idle state
- **WHEN** the detector's normal no-work result is finalized locally
- **THEN** the state transitions from pending to a started zero snapshot and then inactive completion with no active request, cancellation owner, activity, callback, or coordinator residue
- **AND** `Run started — nothing to process, all assets already have location data.` precedes `Run complete. Processed=0 Skipped=0 Errors=0`

### Requirement: Empty-schedule regression is hermetic
The focused regression SHALL use fakes only and MUST NOT connect to PostgreSQL or SQLite, load or inspect geodata files, materialize the production heavy dependency graph, or spawn a real child process.

#### Scenario: Regression runs without external resources
- **WHEN** the focused empty-schedule test runs in the normal unit-test suite
- **THEN** all detector, launcher, backend, protocol, state, and geodata observations come from deterministic in-memory fakes and counters

## Audit Reconciliation

This test-only change depends on the block-35 fixture and its landed scheduled detector/local-finalizer/child-backend seams. It reuses that fixture to prove detector-zero behavior rather than inventing a second scheduler, detector, child boundary, or worker fixture; implementation must conditionally bind to the exact landed names after block 35.

