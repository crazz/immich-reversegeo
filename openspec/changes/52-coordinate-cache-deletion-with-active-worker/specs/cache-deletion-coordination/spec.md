## Purpose

Prevents Administrative Areas cache deletion from racing local heavy geodata workers while keeping deletion lightweight, storage-confined, and truthful about partial filesystem outcomes.

## ADDED Requirements

### Requirement: Atomic exclusive deletion reservation
The system SHALL atomically reserve the same process-local exclusive heavy-geodata resource used by processing, lookup, and cache-mutation workers before deleting any requested cache file. It SHALL hold that reservation through finalization of all requested filesystem outcomes and SHALL release it exactly once.

#### Scenario: Heavy worker already owns the resource
- **WHEN** a user requests per-cache deletion or Delete All while any exclusive heavy-geodata worker owns the local resource
- **THEN** the request fails fast as busy, starts no child process, deletes no requested file, receives no owner cancellation capability, and is not queued or retried automatically

#### Scenario: Deletion and worker admission race
- **WHEN** deletion and a heavy worker concurrently attempt the idle resource
- **THEN** exactly one request acquires it and the losing request performs no cache mutation or worker launch

#### Scenario: Worker requests admission during deletion
- **WHEN** a deletion operation already holds the resource and a heavy worker requests admission
- **THEN** the worker receives safe Cache maintenance busy information, starts no process, and cannot release or cancel the deletion owner

### Requirement: Lightweight Web deletion lifecycle
An admitted deletion SHALL run as a lightweight Web control-plane operation and MUST NOT create a worker job identity, launch a child, use worker protocol or exit codes, initialize geodata, or update processing state. The operation SHALL expose no user Cancel action and SHALL keep conflicting cache controls disabled until reservation release and the existing page-owned post-operation reload.

#### Scenario: Idle resource admits deletion
- **WHEN** a validated deletion request wins admission while the Web host accepts work
- **THEN** Web performs the bounded filesystem operation directly, finalizes and reports its typed outcome, releases the reservation, and lets the existing page perform its explicit status reload without launching a worker

#### Scenario: User leaves during deletion
- **WHEN** navigation, circuit disposal, or a stale page callback occurs after deletion is admitted
- **THEN** the page-independent operation retains ownership through completion and cleanup, stale UI updates are suppressed, and the reservation is not released early

#### Scenario: Host shutdown races admission
- **WHEN** shutdown fencing and a deletion request race
- **THEN** either deletion is rejected as unavailable before touching storage or shutdown observes the admitted lightweight owner and waits for its bounded completion/release without starting or killing a child

### Requirement: Canonical source, country, and path confinement
Every per-cache target SHALL contain only a closed source of Overture or GADM and an exact three-letter uppercase ASCII ISO3 code known to the bundled country catalog and mappable for that source. The system SHALL derive only the matching final path beneath the configured source directory and MUST NOT accept a caller-supplied path, delete a temporary candidate, follow a symbolic link/reparse point, or allow traversal outside configured data storage.

#### Scenario: Canonical target is requested
- **WHEN** the source and ISO3 are valid and the configured source root and final entry are ordinary non-link filesystem objects
- **THEN** deletion addresses only `<DataDir>/overture-divisions/{ISO3}.db` or `<DataDir>/gadm-divisions/{ISO3}.db` for that source

#### Scenario: Invalid or mismatched target is requested
- **WHEN** the source is unknown, the code is padded, lowercase, non-ASCII, malformed, unknown, source-unmappable, or request data contains a path
- **THEN** the request is rejected before admission, directory creation, or filesystem mutation

#### Scenario: Link or escaped storage is encountered
- **WHEN** canonical containment fails or the configured source path or selected cache entry is a symbolic link/reparse point
- **THEN** deletion fails safely without following or removing the linked target and reports no host path

### Requirement: Closed handles and precise cache state
The system SHALL acquire deletion reservation only after prior local worker ownership has reached its established process, stream, protocol, and bridge finality. Prior worker code SHALL close readers, writers, and transactions before release, and the current Data-page status read SHALL complete and close its short-lived handles before invoking deletion. Deletion MUST NOT use process-wide SQLite pool clearing or delete block 51 operation-owned temporary candidates.

#### Scenario: Local worker used the cache before deletion
- **WHEN** a local worker closes and releases the exclusive resource and deletion is subsequently admitted
- **THEN** no local worker-owned cache handle remains by contract and the final cache file can be deleted without global pool clearing

#### Scenario: Filesystem reports an in-use or permission failure
- **WHEN** deletion cannot remove a final file because of an open external handle, permissions, read-only storage, or another I/O error
- **THEN** that target is reported failed with bounded safe copy, no success is fabricated, and unrelated cache files remain eligible for truthful Delete All processing

### Requirement: Idempotent and truthful deletion outcomes
Per-cache deletion SHALL return exactly Deleted, Missing, Invalid, or Failed for the requested target. Missing SHALL be an idempotent non-error. Source-specific Delete All SHALL take one reservation for its whole immutable validated target snapshot, attempt each target independently, and return ordered per-target outcomes plus Deleted, Missing, Invalid, and Failed counts.

#### Scenario: Per-cache target is already absent
- **WHEN** a valid cache target does not exist at deletion time
- **THEN** the operation reports Missing without creating a directory or file and presents the desired absent state as already satisfied

#### Scenario: Delete All partially fails
- **WHEN** one or more validated targets cannot be deleted while other targets are deleted or already missing
- **THEN** the operation continues through the snapshot, reports actual per-target outcomes and aggregate counts, and does not claim that all files were deleted

#### Scenario: Delete All target set is empty
- **WHEN** the page-supplied source target snapshot contains no valid cache targets
- **THEN** the operation completes as a no-op with zero counts and does not fabricate a deletion

### Requirement: Finalized results and existing page reload
The deletion operation SHALL return finalized explicit Deleted, Missing, Invalid, or Failed result data and MUST NOT require or introduce an inventory cache, invalidation contract, snapshot, or reload service. After operation completion and reservation release, the existing Data page SHALL perform its current explicit status reload and present deletion outcomes separately from that subsequent read.

#### Scenario: Deletion completes
- **WHEN** per-cache deletion or Delete All has finalized every requested target outcome
- **THEN** the operation returns those immutable results, releases ownership exactly once, and invokes no inventory invalidation contract

#### Scenario: Existing page refreshes after deletion
- **WHEN** the deletion task has completed with Deleted, Missing, Invalid, or Failed results
- **THEN** the Data page performs its existing explicit status reload after completion and distinguishes complete success, idempotent absence, partial success, and complete failure from the finalized result data

#### Scenario: Later inventory support observes deletion
- **WHEN** a later inventory snapshot implementation needs mutation awareness
- **THEN** it may observe or adapt the finalized deletion results to invalidate its own snapshot without becoming a prerequisite or dependency of deletion

### Requirement: Process-local coordination limitation
Deletion coordination SHALL be documented as local to one Web process. It MUST NOT claim protection against independently started internal workers, direct filesystem writers, or another container using the same data volume.

#### Scenario: Multiple Web containers share data storage
- **WHEN** two Web containers use the same cache volume
- **THEN** each coordinator can protect only its own workers and deletion, and operators are told to use one interactive Web control plane for strict exclusion until distributed cache locking exists
