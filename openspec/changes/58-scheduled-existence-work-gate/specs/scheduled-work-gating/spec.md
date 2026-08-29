## Purpose

Scheduled work gating avoids an unnecessary exact pre-launch count while retaining current eligibility, advisory scheduling, and worker-owned processing authority.

## ADDED Requirements

### Requirement: Count-free bounded scheduled preflight
The system SHALL evaluate an admitted internal scheduled full-eligibility request with a bounded existence observation and SHALL return only the established boolean launch decision and bounded safe diagnostics. A successful observation SHALL NOT return, publish, or derive an exact or estimated count. Dashboard statistics and manual-run progress SHALL retain exact-count behavior, and the processing worker SHALL retain its independent authoritative exact count and zero gate.

#### Scenario: Scheduled preflight finds work
- **WHEN** at least one row satisfies current full eligibility at the time of the scheduled observation
- **THEN** the detector reports work without invoking an exact count solely for the preflight and the existing admitted scheduled lifecycle may launch one worker

#### Scenario: Scheduled preflight finds no work
- **WHEN** no row satisfies current full eligibility at the time of the scheduled observation
- **THEN** the detector reports no work without invoking an exact count and the existing identity-checked local zero-work finalizer completes the occurrence without resolving a backend or launching a worker

#### Scenario: Exact count remains user-facing and authoritative
- **WHEN** Dashboard statistics are requested or an admitted worker starts processing
- **THEN** the existing exact count remains available for that purpose and is not replaced by, shared from, or inferred from the scheduled existence result

### Requirement: Existence eligibility exactly matches current full eligibility
The existence observation SHALL report work exactly when at least one non-deleted asset has a matching EXIF row whose city and country are both null and whose latitude and longitude are both non-null. State SHALL NOT affect eligibility. The observation SHALL read no processing or overwrite setting; no overwrite eligibility setting exists in the current contract. It SHALL neither read nor filter skipped-asset storage: skipped IDs remain part of database eligibility, and the worker SHALL retain its existing one-time skipped-ID snapshot and downstream skip behavior.

#### Scenario: Fully eligible EXIF row exists
- **WHEN** a non-deleted asset has a matching EXIF row with null city, null country, non-null latitude, and non-null longitude
- **THEN** the existence observation reports work regardless of the EXIF state value

#### Scenario: Eligibility near-miss exists
- **WHEN** every candidate is deleted, lacks a matching EXIF row, has either city or country populated, or has either latitude or longitude null
- **THEN** the existence observation reports no work

#### Scenario: Only skipped IDs satisfy database eligibility
- **WHEN** one or more database-eligible asset IDs are present in skipped-asset storage and no other asset is eligible
- **THEN** the scheduled observation still reports work without reading skipped storage and the launched worker applies its unchanged authoritative count and skipped-ID snapshot semantics

### Requirement: Cancellation and query failures never become no work
The existence observation SHALL pass the admitted cancellation token through connection opening and command execution. Matching cancellation SHALL propagate as cancellation, and connection, timeout, SQL, schema, result-conversion, or other unexpected query failures SHALL propagate as failure. Neither outcome SHALL produce a successful false result, use the exact count as fallback, launch a replacement path, or retry the occurrence automatically. Any command-timeout policy already established by the landed lightweight PostgreSQL boundary SHALL remain unchanged; this change SHALL NOT introduce a new timeout setting.

#### Scenario: Observation is cancelled
- **WHEN** the admitted cancellation token is cancelled before the observation completes
- **THEN** cancellation remains distinct from no work and the existing scheduled predispatch cancellation finalizer closes the matching occurrence

#### Scenario: Observation query fails
- **WHEN** opening the connection or executing or decoding the existence query fails
- **THEN** failure remains distinct from no work and the existing scheduled predispatch failure finalizer closes the matching occurrence with no count fallback or worker launch

### Requirement: Observation remains advisory and side-effect free
The existence result SHALL describe only one completed database observation; it SHALL NOT reserve rows or create an atomic snapshot with worker execution. It SHALL perform no Immich or schema mutation, skipped-store access, processing-configuration access, batch work, worker-request enrichment, detector persistence, backend resolution, protocol activity, geodata/cache/airport access, or worker launch. The existing pending-state, detector-call, local-finalization-or-child-dispatch order and matching-handle cleanup SHALL remain unchanged.

#### Scenario: Work disappears after positive observation
- **WHEN** the existence observation reports work but the worker's later authoritative count returns zero
- **THEN** the single launched worker completes through its ordinary zero-work path without fallback, retry, replay, replacement, catch-up, or resubmission

#### Scenario: Work appears after negative observation
- **WHEN** the existence observation reports no work and an asset becomes eligible immediately afterward
- **THEN** the locally completed occurrence remains closed and that asset waits for a later ordinary trigger

#### Scenario: Probe completes normally
- **WHEN** the scheduled existence observation returns work or no work
- **THEN** only the established detector result is produced and worker count, worker parallelism, processing configuration, skipped snapshot, and geodata behavior remain unchanged
