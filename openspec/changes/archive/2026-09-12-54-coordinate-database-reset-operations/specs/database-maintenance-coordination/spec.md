## Purpose

Coordinates destructive Immich location and skipped-asset reset operations with locally admitted work while preserving their exact data scope, safeguards, truthful outcomes, and lightweight Web execution.

## ADDED Requirements

### Requirement: Exact database-reset scope
The system SHALL support exactly these block-54 mutations: clear all Immich location values plus the entire skipped-asset list; clear Immich location values for validated selected asset IDs plus those IDs from the skipped list; clear Immich location values for assets matching one selected city, state, or country value plus the returned asset IDs from the skipped list; and clear the entire skipped-asset list from the Data page. Immich mutation MUST be limited to setting `asset_exif.city`, `asset_exif.state`, and `asset_exif.country` to null and MUST NOT delete assets, alter other Immich data, change schema, or mutate geodata cache databases.

#### Scenario: Reset all is confirmed
- **WHEN** the user confirms **Reset All Data...**
- **THEN** only the three location columns are cleared for all applicable `asset_exif` rows and the skipped-asset list is cleared

#### Scenario: Selected asset IDs are submitted
- **WHEN** the user submits **Reset Selected Items** with one or more valid asset IDs
- **THEN** duplicate IDs are collapsed, only matching assets have all three location columns cleared, and only the requested IDs are removed from skipped-asset tracking

#### Scenario: A location value is selected
- **WHEN** the user submits **Reset Matching City**, **Reset Matching State**, or **Reset Matching Country** with a valid current value
- **THEN** all three location columns are cleared for matching non-deleted assets and only the database-returned asset IDs are removed from skipped-asset tracking

#### Scenario: Clear Skip List is selected
- **WHEN** the user activates **Clear Skip List**
- **THEN** skipped-asset tracking is cleared without changing Immich or cache database content

### Requirement: Validation and existing confirmation boundaries
The system SHALL validate every request again in the command boundary before admission or mutation. Reset-all SHALL require the existing explicit confirmation; selected-ID reset SHALL reject an empty valid-ID set, deduplicate valid IDs, ignore and report malformed tokens when at least one valid ID remains, and mutate only for the valid set; matching-value reset SHALL require a closed city/state/country scope and a nonblank value submitted from the current options UI while letting the atomic database statement determine whether it still matches. The other three existing actions MUST NOT gain an implicit confirmation or bypass their server-side validation.

#### Scenario: Reset-all confirmation is cancelled
- **WHEN** the reset-all confirmation is cancelled or never completed
- **THEN** no admission is attempted and neither database is changed

#### Scenario: Selected IDs contain invalid input
- **WHEN** selected-ID input contains malformed tokens but at least one valid asset identifier
- **THEN** malformed tokens are ignored and reported, valid identifiers are deduplicated, and only the valid set proceeds to admission and mutation

#### Scenario: Selected IDs contain no valid input
- **WHEN** selected-ID input contains no valid asset identifier
- **THEN** the request reports validation failure before admission and neither database is changed

#### Scenario: Matching input is invalid
- **WHEN** the matching scope is unknown or the selected value is blank or invalid at execution
- **THEN** the request is rejected before mutation without treating client controls as authorization

### Requirement: Atomic fail-fast local admission
Every valid confirmed block-54 mutation SHALL atomically contend for the same process-local exclusive resource used by processing, lookup, cache-mutation workers, and compatible lightweight maintenance. Admission SHALL return Admitted, Busy with bounded safe owner category, or Unavailable with a bounded safe reason. Busy and Unavailable requests MUST perform no mutation, launch no worker, receive no cancellation/release capability, enter no queue, and retry neither automatically nor by reservation handoff.

#### Scenario: Active worker owns the resource
- **WHEN** a reset request races with or follows an active locally coordinated worker
- **THEN** exactly one owner wins and a losing reset reports Busy without opening a mutating repository operation

#### Scenario: Maintenance owns the resource
- **WHEN** processing, lookup, cache work, or another reset requests admission while database maintenance is active
- **THEN** the new request receives safe Database maintenance busy information and cannot cancel or release that owner

#### Scenario: Host is no longer accepting work
- **WHEN** a reset request reaches admission after the shutdown fence
- **THEN** it reports Unavailable and performs no mutation

### Requirement: Lightweight non-worker maintenance lifecycle
An admitted block-54 reset SHALL execute as page-independent lightweight Web maintenance and MUST NOT create a worker JobId, launch a child, use worker protocol or worker exit codes, initialize geodata, or update processing state. The maintenance operation SHALL expose no user Cancel action, SHALL retain ownership despite navigation or circuit disposal, and SHALL release its owner handle exactly once only after repository work and result finalization have ended.

#### Scenario: Idle resource admits a reset
- **WHEN** a validated reset wins admission
- **THEN** Web performs only the bounded database commands, finalizes a typed result, releases ownership in a finalization path, and starts no worker

#### Scenario: User leaves during an admitted reset
- **WHEN** navigation, circuit disposal, or a stale callback occurs after admission
- **THEN** the page-independent operation continues to completion, stale UI updates are suppressed, and ownership is not released early

#### Scenario: Admission and shutdown race
- **WHEN** shutdown fencing and reset admission race
- **THEN** either the reset is rejected before mutation or shutdown observes the exact maintenance owner and waits for its tracked completion/release without fabricating cancellation or starting/killing a child

### Requirement: Per-store atomicity and truthful multi-store outcomes
Each PostgreSQL reset statement SHALL be atomic in PostgreSQL, and each skipped-asset delete SHALL use its own SQLite statement or transaction. The system MUST NOT claim a distributed transaction across Immich PostgreSQL and `skipped.db`. Multi-store operations SHALL preserve the exact IDs returned or requested for skipped cleanup, distinguish NotStarted, Succeeded, and Failed per store, stop before SQLite when PostgreSQL fails, and report partial completion when PostgreSQL commits but skipped cleanup fails.

#### Scenario: PostgreSQL fails before committing
- **WHEN** an Immich reset statement fails or is cancelled before commit
- **THEN** skipped cleanup is not attempted, the reservation remains owned through finalization, and the UI reports that neither requested stage completed

#### Scenario: Skipped cleanup fails after PostgreSQL commit
- **WHEN** Immich location reset commits but the corresponding skipped-asset operation fails
- **THEN** the UI reports partial completion without claiming rollback, retains the exact cleanup target internally for an explicit safe retry path, and does not rerun the Immich reset automatically

#### Scenario: Both stores complete
- **WHEN** PostgreSQL commits and skipped cleanup completes
- **THEN** the result reports actual affected and removed counts separately

#### Scenario: Standalone skip-list clear fails
- **WHEN** `skipped.db` cannot be opened or written
- **THEN** the result reports a safe failure and the displayed count is not forced to zero

### Requirement: Handles, permissions, and bounded safe errors
Repository commands SHALL dispose their own connections, readers, commands, and transactions before maintenance release and MUST NOT clear process-wide database pools to mask an open handle. Operations SHALL use only configured Immich credentials and the configured `skipped.db` path, accept no caller-supplied connection string or filesystem path, and report permission, timeout, connection, read-only, and I/O failures with bounded safe copy that excludes credentials, connection strings, host paths, SQL text, stack traces, and raw exception details.

#### Scenario: Storage handle is still open
- **WHEN** an external process or leaked handle prevents SQLite maintenance
- **THEN** the operation fails truthfully without clearing global pools, releasing ownership early, or claiming success

#### Scenario: Configured credentials lack update permission
- **WHEN** PostgreSQL rejects a reset because the application database identity lacks permission
- **THEN** the result reports a safe Immich database failure, skipped cleanup is not attempted, and no alternate credentials or privilege escalation are used

#### Scenario: Skipped database is read-only
- **WHEN** the configured data volume does not permit the skipped-asset delete
- **THEN** the SQLite stage reports failure without exposing the configured host path

### Requirement: Authoritative UI result and lightweight reload
The UI SHALL disable conflicting reset controls for the owning operation, distinguish validation, Busy, Unavailable, complete success, PostgreSQL failure, skipped-database failure, and partial success, and announce final messages accessibly. After finalization and release, Reset Immich Geo Data SHALL reload location-value options and Data SHALL reload the skipped count from storage; a reload failure SHALL be reported separately and MUST NOT rewrite the finalized mutation result. No cache-inventory invalidation SHALL be emitted because block-54 operations do not mutate cache inventory.

#### Scenario: Reset finalizes successfully
- **WHEN** a Reset Immich Geo Data mutation completes and releases ownership
- **THEN** the page reloads current location values and renders counts from the typed result rather than optimistic client state

#### Scenario: Clear Skip List finalizes
- **WHEN** the Data-page operation completes and releases ownership
- **THEN** Data re-reads the skipped count and does not force the displayed value to zero if the operation or reload failed

#### Scenario: Reload fails after committed mutation
- **WHEN** storage mutation has finalized but the subsequent read fails
- **THEN** the UI preserves the mutation outcome, reports stale or unavailable display data separately, and permits an explicit reload

### Requirement: Read and adjacent-operation classification
Location-option reads, skipped-count reads, cache inventory reads, and Settings **Test Connection** SHALL remain lightweight Web reads that do not reserve the exclusive resource. Cache **Re-download** SHALL remain a typed worker cache-mutation operation owned by block 51, and cache **Delete**/**Delete All** SHALL remain lightweight reserved maintenance owned by block 52. Block 54 MUST NOT add a DatabaseMaintenance worker kind or change those adjacent operations.

#### Scenario: Lightweight read overlaps active work
- **WHEN** a user loads reset options, Data counts, cache inventory, or selects Settings **Test Connection** while admitted work is active
- **THEN** the read follows its existing bounded read behavior without acquiring or releasing the exclusive maintenance resource

#### Scenario: Cache control is selected
- **WHEN** a user selects Re-download, Delete, or Delete All on Administrative Areas
- **THEN** the operation follows its block-51 or block-52 owner and is not routed through database-reset orchestration

### Requirement: Process-local coordination limitation
Database-reset admission SHALL be described as local to one Standard or Web-only Web process. It MUST NOT claim exclusion against another Web container, a run-once process, manually invoked private worker, direct PostgreSQL client, or direct `skipped.db` writer, and MUST NOT broaden or reuse the processing-only PostgreSQL advisory lock.

#### Scenario: Multiple containers share databases or data storage
- **WHEN** more than one Web or run-once container can access the same Immich database or data volume
- **THEN** each local coordinator protects only its own admitted work and operators are told that strict reset/processing exclusion currently requires one interactive Web control plane and no independently launched conflicting writer
