## Purpose

Lets the Web Data experience inspect administrative-cache storage safely and predictably without loading geodata or constructing the services that create and consume it.

## ADDED Requirements

### Requirement: Stable storage inventory contract
The system SHALL expose immutable cache inventory snapshots whose entries use closed source values `Overture` and `Gadm`, canonical uppercase ASCII ISO3 identifiers, and closed statuses `Available`, `Absent`, `InProgress`, `Invalid`, `Unreadable`, or `Unsafe`. An entry SHALL include source, ISO3, status, nullable file size, nullable last-modified UTC time, nullable downloaded UTC time, nullable dataset version/release, whether recognized operation-owned temporary artifacts were observed, and a nullable safe diagnostic code. An exact result SHALL also state whether bounded temporary discovery completed, so a false temporary flag cannot imply proven absence after truncation. It SHALL NOT derive an area or geometry row count from a cache data table.

#### Scenario: Existing cache has readable metadata
- **WHEN** inventory inspects a canonical final cache whose SQLite schema identifies the expected source table and whose optional metadata is readable
- **THEN** it returns an `Available` entry with filesystem size and modification time and with downloaded time and version/release when those metadata values are cheaply available

#### Scenario: Optional metadata is absent
- **WHEN** the expected SQLite cache table is present but an optional downloaded-time or version/release value is absent
- **THEN** the entry remains `Available` and the unavailable field is null

#### Scenario: Exact lookup is absent
- **WHEN** an exact source-and-ISO3 lookup finds neither a final cache nor a recognized operation-owned temporary for that key
- **THEN** it returns `Absent` only when bounded temporary discovery completed, while a source listing does not synthesize rows for every possible country

#### Scenario: Exact lookup discovery is truncated
- **WHEN** an exact source-and-ISO3 lookup inspects its canonical final but bounded source discovery cannot prove whether a recognized temporary exists
- **THEN** it marks discovery incomplete, may preserve positive final or temporary facts, and does not report `Absent` or present a false temporary flag as proof of absence

### Requirement: Partial, corrupt, and unreadable storage is explicit
The system SHALL distinguish operation-owned in-progress artifacts, structurally invalid final databases, access failures, and unsafe path objects without treating any of them as a ready cache or attempting repair.

#### Scenario: Only an owned temporary exists
- **WHEN** no final cache exists and at least one filename matching the finalized source mutation temporary policy exists for an ISO3
- **THEN** inventory reports `InProgress` without opening the temporary as a final database

#### Scenario: Final cache and temporary coexist
- **WHEN** a valid final cache and one or more recognized operation-owned temporaries coexist during refresh
- **THEN** inventory reports the final cache as `Available` and separately indicates the observed temporary artifacts

#### Scenario: Final cache is corrupt or incomplete
- **WHEN** a final file is empty, is not a readable SQLite database, or lacks the expected source table
- **THEN** inventory reports `Invalid` with a safe diagnostic code and does not scan a data or geometry table

#### Scenario: Permission or transient I/O failure
- **WHEN** inventory cannot enumerate a source directory or inspect a candidate because access is denied or an I/O failure prevents classification
- **THEN** it preserves other independently readable results and returns a source-level or entry-level `Unreadable` result without exposing the configured host path

### Requirement: Inventory inspection is contained and lightweight
The system SHALL derive the two fixed source directories from storage configuration, inspect only immediate canonical candidate names, and perform only filesystem inspection plus read-only SQLite schema and indexed `_meta`-style point reads. It SHALL reject links observed by bounded checks before and after an open and discard metadata from detected replacement races. Because the cross-platform SQLite string-path API is not an adversarial race-free no-follow primitive, the configured cache volume SHALL be writable only by trusted operators and Immich ReverseGeo. The inventory SHALL NOT resolve or invoke cache mutation services, exporters, DuckDB, GeoPackage readers, HTTP clients, resolvers, geometry readers, country indexes, processing state, or workers.

#### Scenario: Inventory is first resolved
- **WHEN** Web composition resolves the inventory service
- **THEN** construction performs no scan, SQLite open, directory creation, worker launch, or heavy-service resolution

#### Scenario: Candidate name attempts path escape
- **WHEN** a directory entry is not an exact canonical final or finalized owned-temporary basename, resolves outside its fixed source directory, or is observed as a symbolic link or reparse-point descendant before open
- **THEN** inventory ignores the unrelated name or reports an exact canonical candidate as `Unsafe` without opening the observed link target, and discards metadata if bounded post-open checks detect a replacement race

#### Scenario: SQLite metadata is inspected
- **WHEN** inventory opens a final candidate
- **THEN** it uses a read-only non-pooled connection with a native per-connection value-byte limit, counts every immediate schema object against a schema-object bound before classification, accepts only the expected indexed `_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)` shape before bounded point lookups, reads only schema and bounded metadata values, disposes all readers and connections before returning, and retains no open or pooled database handle

### Requirement: Scanning and snapshot caching are bounded
The system SHALL impose validated internal bounds on immediate entries visited, logical candidates, recognized temporaries per ISO, immediate SQLite schema objects of every type, managed metadata characters, and native SQLite value bytes, surface truncation or source-scan failure instead of silently claiming a complete inventory, and publish deterministic immutable snapshots. Every immediate entry, including junk, SHALL consume the visit budget. Concurrent readers MAY share one in-flight full scan, and every full or exact physical inspection SHALL pass through one fixed-capacity service-owned admission boundary, but no filesystem watcher, startup scan, or unbounded background refresh SHALL be started. Caller cancellation SHALL detach only that waiter, while service disposal SHALL cancel queued admission and drain queued and active inspection work.

#### Scenario: Candidate bound is exceeded
- **WHEN** a source directory contains more candidates than the configured internal scan bound
- **THEN** inventory observes at most the visit bound plus one entries, disposes enumeration, discards every collected row for that source, and returns only a source-level truncated result with a safe diagnostic while the other source may continue

#### Scenario: Concurrent pages request a refresh
- **WHEN** multiple Web circuits request the same inventory generation concurrently
- **THEN** they share bounded inspection work or receive immutable snapshots without mutating one another's view

#### Scenario: Exact and full inspections overlap
- **WHEN** exact lookups and a full refresh are requested concurrently, including duplicate exact keys and a caller that cancels its wait
- **THEN** at most one physical scanner call runs at once, caller cancellation does not cancel service-owned work, and disposal cancels queued admission and drains all admitted work

#### Scenario: Explicit Data access requests current storage
- **WHEN** the Data experience initializes or explicitly rereads after an operation
- **THEN** it requests a refresh scan rather than relying indefinitely on the previous snapshot

### Requirement: Publication and deletion races do not poison snapshots
The system SHALL tolerate a final file being atomically replaced or deleted during inspection. It SHALL compare bounded pre/post filesystem observations and retry a changed candidate at most once; it SHALL return a safe transient classification when a stable observation cannot be obtained, and SHALL never retain a handle after the entry inspection.

#### Scenario: Worker publishes while inventory scans
- **WHEN** atomic cache publication changes the final candidate during an inventory read
- **THEN** the returned entry is based on one stable old-or-new observation or is safely classified as transient, never a mixture of old metadata and new filesystem facts

#### Scenario: Deletion removes a candidate during inventory read
- **WHEN** coordinated deletion removes a final file before inventory completes
- **THEN** inventory retries once and returns the resulting absent/in-progress state or a safe transient result without failing unrelated entries

### Requirement: Authoritative mutation invalidates cached inventory
After finalized changes 51 and 52, the system SHALL use change-53-owned adapters to mark inventory dirty only from their existing explicit outcomes: change 51's successful cache-mutation completion after process/session finality, and each actual `Deleted` item in change 52's per-cache or delete-all result. Change 52 SHALL remain independent of inventory and its current explicit page reload SHALL remain unchanged; it SHALL NOT bind to a change-53 invalidator interface. Invalidation SHALL be generation-safe and idempotent when no current-generation scan can publish. Once a scan starts, every later authoritative mutation invalidation, including one for a key already dirty when the scan began, SHALL fence that scan from reusable publication, and the next inventory access SHALL observe storage again.

#### Scenario: Worker mutation completes successfully
- **WHEN** change 51 reports an authoritative successful cache publication or already-ready completion after finalization
- **THEN** the relevant source/ISO3 inventory generation is invalidated and the next access reflects actual storage

#### Scenario: Delete-all partially succeeds
- **WHEN** finalized change 52 returns an explicit delete-all result containing actual `Deleted` items and failures
- **THEN** change 53's adapter invalidates only the deleted keys, preserves the reported deletion failures and change 52's existing explicit reload behavior, and rereads storage on the next inventory access

#### Scenario: Operation does not succeed
- **WHEN** a mutation is busy, unavailable, refused, failed, cancelled, crashed, or has not reached authoritative finality
- **THEN** the system does not fabricate successful invalidation or availability and a requested explicit reread remains free to observe actual disk state

#### Scenario: Invalidation races with a scan
- **WHEN** a successful mutation invalidates generation N while a generation-N scan is in flight
- **THEN** that scan cannot clear the dirty marker or become the reusable generation-N-plus-one snapshot

#### Scenario: Repeated mutation races with a dirty-key scan
- **WHEN** a scan starts while a key is dirty and another successful mutation invalidates that same key before the scan publishes
- **THEN** the later mutation advances the publication fence and the older observation cannot clear or replace the newer dirty evidence

### Requirement: Data UI and deployment modes use only the inventory read path
In modes that host the Web Data experience, the system SHALL register one lazy singleton inventory and use it for initial Data summary and GeoBoundaries reads. The summary SHALL count `Available` caches; the table SHALL preserve source/ISO filtering and sorting while presenting ISO3, status, optional version/release, size, modification/download time, and safe diagnostics. A source-specific Delete All action SHALL be enabled and accepted by its server-side page handler only when that source snapshot is `Ready`; incomplete or missing source state SHALL fail closed without creating a deletion confirmation, while a complete peer source remains usable. Worker-only and run-once composition SHALL NOT register or initialize the Web inventory merely to execute jobs.

#### Scenario: Web-only mode opens Data
- **WHEN** a user opens Data in Web-only mode
- **THEN** cache inventory renders from storage metadata without resolving a heavy geodata service or launching a child worker

#### Scenario: Standard mode opens GeoBoundaries
- **WHEN** a user opens GeoBoundaries in Standard mode
- **THEN** it uses the same inventory DTOs and stable `Overture`/`GADM` labels, shows non-available states explicitly, and preserves GADM attribution and license copy independently of technical diagnostics

#### Scenario: Incomplete source blocks Delete All
- **WHEN** one source inventory is truncated, unreadable, unsafe, missing, or not yet loaded while the other source is complete
- **THEN** the incomplete source's Delete All action is disabled and rejected before confirmation, a reported incomplete source shows recovery guidance, and the complete source retains its normal confirmation flow

#### Scenario: Worker or run-once host starts
- **WHEN** a non-Web role starts without a Data UI
- **THEN** inventory performs no startup work and is not required by worker job composition
