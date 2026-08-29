## Purpose

Defines database-scoped mutual exclusion for heavy processing across independently launched workers that share one Immich PostgreSQL database.

## ADDED Requirements

### Requirement: Lock acquisition is the first accepted-session gate
After the existing executor/reporter session emits `run-started`, the worker SHALL attempt the advisory run lock as its first executor/session step. It SHALL complete this gate before eligibility discovery, snapshots, geodata loading or lookup, asset mutation, or other domain/heavy work. The host SHALL still invoke the executor exactly once, and the existing executor/reporter path SHALL retain terminal ownership.

#### Scenario: Uncontended accepted run enters domain work
- **WHEN** an accepted request emits `run-started` and the lock is available
- **THEN** the worker acquires the lock before eligibility, snapshots, geodata, mutation, or other domain/heavy work and continues through the existing executor path

#### Scenario: Lock gate does not bypass executor entry
- **WHEN** an accepted request begins execution
- **THEN** lock acquisition occurs after the exact-once executor invocation and `run-started`, not before executor/session entry

### Requirement: Contention uses the reserved busy outcome
The worker SHALL use non-blocking acquisition. When the lock is already held, it SHALL perform no eligibility, snapshot, geodata, mutation, or other domain/heavy work; SHALL emit the existing valid failed terminal with bounded safe busy detail; SHALL select exit code 3 through the typed block-23 outcome path; and SHALL NOT add a terminal type or increment the domain failed-asset count.

#### Scenario: Contended worker start
- **WHEN** another session in the same database holds the application advisory run lock
- **THEN** the accepted worker emits one failed terminal with safe busy detail, reports zero domain failed assets, returns busy code 3 absent a higher-precedence condition, and performs no domain/heavy work

#### Scenario: Busy is not a domain failure
- **WHEN** advisory-lock acquisition returns false
- **THEN** the worker records typed contention rather than an executor/domain failure and does not select exit code 4 for that fact

### Requirement: Lock identity is stable, versioned, and database scoped
Lock-key version 1 SHALL use the signed bigint value `-7970420658158250032` (hex bits `0x916360A3F80AD7D0`). That value SHALL be derived as the first eight bytes of SHA-256 over the UTF-8 bytes of `immich-reversegeo/postgresql-advisory-run-lock/v1`, interpreted as a big-endian two's-complement signed 64-bit integer. Workers using this version SHALL call the single-bigint PostgreSQL advisory-lock family against the configured Immich database. A key-version change SHALL be treated as a coordination-contract change requiring an overlap-safe rollout plan.

#### Scenario: Same database and version contend
- **WHEN** two workers use key version 1 against the same PostgreSQL database
- **THEN** both address the exact signed bigint key and at most one session owns it at a time

#### Scenario: Separate databases have separate lock scopes
- **WHEN** two workers use the same key against different PostgreSQL databases
- **THEN** this advisory lock does not claim to coordinate those databases

### Requirement: Ownership is bound to one dedicated live session
A successful acquisition SHALL return a lease that exclusively retains the exact dedicated open PostgreSQL connection and session for the entire protected run through terminal reporting and lock finalization. The connection SHALL NOT be used for domain queries or returned to the pool while ownership is active. The worker SHALL stop protected work when ownership loss is detected, classify that loss as infrastructure failure, and attempt the existing failed terminal when stdout remains healthy. PostgreSQL session termination or process death SHALL release the server-side lock without application cleanup.

#### Scenario: Owner completes normally
- **WHEN** protected execution and terminal reporting finish while the owning session remains healthy
- **THEN** the worker explicitly unlocks on that same session, verifies release, and only then disposes or returns the connection

#### Scenario: Owning session is lost
- **WHEN** the dedicated connection detects that its PostgreSQL session was lost during protected execution
- **THEN** the worker stops further protected work, records infrastructure failure rather than busy or domain failure, and does not reuse that session

#### Scenario: Worker dies while holding the lock
- **WHEN** the worker process terminates and its dedicated PostgreSQL session closes
- **THEN** PostgreSQL releases the session lock so a later session can acquire it

### Requirement: Cancellation and lock errors preserve outcome semantics
Cooperative request cancellation or host shutdown before or during open, acquisition, or protected execution SHALL follow the existing cancelled-terminal and exit-130 contract unless a higher-precedence condition occurs. A database open/acquisition exception SHALL be infrastructure failure, not contention. Unlock returning false, unlock failure, ambiguous release, lease disposal failure, or detected connection loss SHALL contribute an infrastructure outcome. Cleanup SHALL be attempted with a bounded internal cleanup token rather than a token already cancelled by the caller.

#### Scenario: Cancellation occurs during acquisition
- **WHEN** the accepted run's cancellation token is cancelled while opening the dedicated connection or executing the non-blocking acquisition
- **THEN** the worker follows cooperative cancellation, performs no domain/heavy work, and does not report busy solely because of cancellation

#### Scenario: Acquisition command fails
- **WHEN** opening the connection or executing the advisory-lock command fails without cooperative cancellation
- **THEN** the existing failed terminal is attempted with safe infrastructure detail and infrastructure exit code 5 is selected absent a higher-precedence condition

#### Scenario: Release cannot be confirmed
- **WHEN** explicit unlock does not return true or throws
- **THEN** the worker records infrastructure failure and prevents the possibly locked physical session from being reused

### Requirement: Cross-process locking complements local admission without schema changes
The PostgreSQL lock SHALL complement, not replace, the Web process's existing local run lock and coordinator admission. This capability SHALL create no Immich table, index, row, migration, or other schema object and SHALL imply no automatic retry. Other applications sharing the database SHALL avoid the documented bigint key unless they intentionally join this exclusion domain.

#### Scenario: Web-launched run is admitted locally
- **WHEN** the Web coordinator admits a worker run
- **THEN** local admission remains in force and the admitted worker independently performs the PostgreSQL lock gate

#### Scenario: Change is deployed
- **WHEN** this capability is installed or rolled back
- **THEN** no Immich schema migration or persistent cleanup is required

## Audit Reconciliation

Advisory-lock Busy is canonical: after `run-started`, it emits no eligibility event and commits the reserved failed Busy terminal with all four terminal counts exactly zero (`ProcessedCount=0`, `UpdatedCount=0`, `SkippedCount=0`, `FailedCount=0`). It performs no executor or producer work and retains exit code 3 as evidence, not a domain failed-asset count.

