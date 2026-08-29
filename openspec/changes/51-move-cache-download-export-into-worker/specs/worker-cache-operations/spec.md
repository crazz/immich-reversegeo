## Purpose

Runs Overture and GADM administrative cache ensure and refresh operations in temporary workers while preserving verified cache files, licensing visibility, and responsive Web control-plane behavior.

## ADDED Requirements

### Requirement: Closed typed CacheMutation contract
The v2 system SHALL register exactly one `CacheMutation` job kind whose concrete request contains one canonical job identity, a closed source value of `Overture` or `Gadm`, a closed operation value of `Ensure` or `Refresh`, and one canonical ISO 3166-1 alpha-3 country code. It MUST NOT accept a path, URL, release override, arbitrary options, untyped payload, or deletion operation.

#### Scenario: Valid cache mutation is accepted
- **WHEN** an admitted request contains a supported source, supported operation, and known canonical ISO3 code
- **THEN** the registered typed handler executes exactly that source-operation pair using the envelope job identity

#### Scenario: Kind, source, operation, or payload is unknown
- **WHEN** a request uses an unknown discriminator, unexpected property, mismatched payload type, arbitrary path or URL, or unregistered `CacheMutation` handler
- **THEN** the worker rejects it before acceptance, cache-service resolution, filesystem mutation, network access, or heavy initialization and emits no job terminal

### Requirement: Canonical country validation and confined storage
The system SHALL require ISO3 input to be exactly three uppercase ASCII letters and present in the bundled country identity catalog, and SHALL verify the source-specific mapping needed by Overture or GADM before cache work. The worker SHALL derive all final and temporary paths from configured `DataDir` and the validated source/code; request data MUST NOT control a filesystem path.

#### Scenario: Malformed, unknown, or unmappable country is requested
- **WHEN** ISO3 is lowercase, whitespace-padded, non-ASCII, not exactly three letters, unknown to the bundled catalog, or cannot map for the selected source
- **THEN** the request receives the established invalid-input outcome before acceptance, directory creation, network access, or geodata initialization

#### Scenario: Worker storage is writable
- **WHEN** an Overture or GADM operation is accepted
- **THEN** it uses only `<DataDir>/overture-divisions/{ISO3}.db` or `<DataDir>/gadm-divisions/{ISO3}.db` and source-specific unique temporary siblings created under the same directory

#### Scenario: Data storage is missing or not writable
- **WHEN** the worker cannot create or write the configured source directory or a same-directory temporary file
- **THEN** it fails with a bounded safe storage error that exposes no host path and does not alter a verified final cache

### Requirement: Ensure and refresh semantics
The system SHALL treat `Ensure` as “publish a valid cache only when one is not already valid” and `Refresh` as “build and validate a replacement regardless of an existing valid cache.” The existing Administrative Areas **Re-download** action SHALL submit `Refresh`; this change MUST NOT add a separate user-facing download action.

#### Scenario: Ensure finds a valid cache
- **WHEN** `Ensure` observes a source-valid nonempty final cache for the requested country
- **THEN** it performs no network, export, temporary publication, or timestamp rewrite and completes with an `AlreadyReady` disposition and observed metadata

#### Scenario: Ensure finds no valid cache
- **WHEN** `Ensure` observes no source-valid final cache
- **THEN** it downloads/exports, validates, and publishes one replacement before reporting a `Published` disposition

#### Scenario: User re-downloads an existing row
- **WHEN** the Administrative Areas page submits its existing **Re-download** action for an Overture or GADM row
- **THEN** one admitted `Refresh` job builds a replacement without first deleting the visible cache and reports one authoritative final outcome

### Requirement: Atomic validated publication and cleanup
The worker SHALL build each candidate in unique same-directory temporary files, verify the source schema, canonical ISO3 where encoded, nonzero row count, required metadata, and readable SQLite content, close readers/writers, release relevant SQLite pools, and atomically replace the final path only after validation. Failure or cancellation before publication SHALL preserve a previously verified cache and SHALL remove owned temporary database/download artifacts.

#### Scenario: Refresh succeeds
- **WHEN** a refresh candidate passes source-specific validation and can be atomically published
- **THEN** readers observe either the previous complete cache or the complete replacement, never an intentionally deleted gap or partial candidate

#### Scenario: Download or export fails before publication
- **WHEN** remote access, Overture export, GADM GeoPackage export, candidate validation, or publication preparation fails
- **THEN** owned temporary files and relevant pooled handles are cleaned, the prior verified cache remains usable, and no unverified replacement is reported ready

#### Scenario: Publication itself cannot complete
- **WHEN** the platform/filesystem cannot perform the required same-directory atomic replacement
- **THEN** the job fails safely, retains the previous cache where it existed, and does not fall back to delete-then-move publication

#### Scenario: Cancellation arrives after publication
- **WHEN** a validated replacement has already been atomically published before cancellation is observed
- **THEN** the published cache remains usable, no success result is fabricated for the cancelled job, and the next inventory/read observes actual on-disk state

### Requirement: Source behavior and licensing
The worker SHALL preserve current source behavior: Overture obtains the selected release through the centralized DuckDB Azure bootstrap and exports country `division_area` rows; GADM downloads the mapped country GeoPackage and exports its administrative layers. Every GADM progress/result surface SHALL retain stable dataset/version attribution, the official GADM license URL, and the plain-language academic and other non-commercial-use limitation.

#### Scenario: Overture cache is built
- **WHEN** an Overture ensure or refresh requires a replacement
- **THEN** Azure/spatial setup, remote release selection/fallback, export, and validation occur only in the worker and the completed metadata identifies the actual release

#### Scenario: GADM cache is built
- **WHEN** a GADM ensure or refresh requires a replacement
- **THEN** download/export occurs only in the worker and the completed metadata identifies the GADM dataset/version and licensing attribution

#### Scenario: GADM mutation is presented in Web
- **WHEN** a GADM re-download is available, active, completed, failed, or cancelled
- **THEN** the UI keeps the non-commercial-use warning and official license link visible separately from technical status or error copy

### Requirement: Bounded progress, logs, activities, and result
An accepted cache mutation SHALL emit closed discrete progress steps for checking existing state, source preparation/download, export, candidate validation, publication, and completion as applicable, plus bounded safe common logs and balanced scoped activities. It MUST NOT invent percentages. A completed terminal SHALL carry one typed result with source, operation, ISO3, disposition, row count, downloaded timestamp, file size, release/version, and GADM attribution when applicable.

#### Scenario: Operation completes
- **WHEN** a cache mutation completes as `AlreadyReady` or `Published`
- **THEN** all started activities are ended and the completed terminal result is the sole authoritative cache outcome used by Web

#### Scenario: Operation fails
- **WHEN** an accepted operation encounters a domain failure
- **THEN** all started activities are ended, safe logs omit URLs with sensitive query data, local paths, stacks, and secrets, and one failed terminal carries a stable bounded error with no success result

#### Scenario: Progress cannot be measured reliably
- **WHEN** source download or export total work is unknown
- **THEN** the worker reports the current discrete step and bounded status text without a fabricated byte or percentage completion value

### Requirement: Cancellation and retry lifecycle
`CacheMutation` SHALL be cancellable through the block 47 session contract, propagate the active token through token-aware download/export work, observe all started work during unwind, and retain one host-owned terminal. The handler SHALL NOT automatically replay a failed or cancelled mutation; a later user retry SHALL be a newly admitted job with a new identity after cleanup and admission release.

#### Scenario: User cancels active mutation
- **WHEN** Web requests cancellation after execute flush
- **THEN** at most one cancel frame targets the exact job identity, controls remain in Cancelling until authoritative session cleanup, and the worker completes with the established cancelled outcome if cooperative cleanup succeeds

#### Scenario: User retries after failure or cancellation
- **WHEN** a prior mutation has reached final cleanup and Web submits the same source/operation/country again
- **THEN** the coordinator may admit a new job identity that starts from actual final-cache state with no stale in-flight entry or owned temporary artifact from the prior attempt

#### Scenario: Request is repeated while still active
- **WHEN** the same or another heavy operation is requested before the current cache job releases admission
- **THEN** no retry worker starts, no reservation is queued, and Web receives finalized block 50's Busy safe active snapshot

### Requirement: Exclusive heavy arbitration and worker composition
The `CacheMutation` descriptor SHALL declare friendly category Cache maintenance, cache-UI origin, cancellable=true, heavy=true, geodata-bearing=true, and finalized block 50's `ExclusiveHeavyGeodata` resource class. Web SHALL create the sole JobId before atomically requesting admission and SHALL receive exactly `Admitted(owner handle)`, `Busy(safe active snapshot)`, or `Unavailable(safe pre-launch reason)`. Admission SHALL be first-successful-request-wins with no waiting, queue, coordinator retry, preemption, priority promotion, fairness, or starvation guarantee. Only an admitted owner MAY launch/cancel/release; Busy and Unavailable SHALL start no process and fabricate no worker exit. The cache worker MUST NOT acquire the processing-only PostgreSQL advisory lock, use exit 3 for local contention, or write Immich asset, EXIF, skipped-asset, configuration, or schema data.

#### Scenario: Processing, Lookup, or another cache job is active
- **WHEN** Web requests a CacheMutation while any `ExclusiveHeavyGeodata` owner is active in that Web process
- **THEN** it receives Busy with the safe exact active kind/category/origin/lifecycle snapshot, starts no process, touches no cache file, cannot cancel/release the owner, and is not automatically retried

#### Scenario: Coordinator cannot accept pre-launch work
- **WHEN** shutdown fencing or unavailable launch capability prevents admission
- **THEN** CacheMutation receives Unavailable with a bounded safe reason and starts no worker or Web fallback

#### Scenario: Cache mutation is admitted
- **WHEN** CacheMutation receives Admitted
- **THEN** its owner handle binds exactly one session with the preselected JobId and advances only its matching active snapshot monotonically through Admitted, Starting, Running, Stopping, and Finalizing as applicable

#### Scenario: Cache mutation reaches outcome evidence
- **WHEN** launch failure, terminal, cancellation, crash, protocol/transport failure, forced stop, or disposal occurs
- **THEN** the owner/classifier finalizes one outcome and releases exactly once only after process exit plus stdout/stderr/protocol/bridge finality when a process exists; stale, wrong-kind, wrong-identity, duplicate, Busy, or Unavailable callers cannot clear the slot

#### Scenario: Shutdown races cache admission
- **WHEN** CacheMutation admission races finalized block 50's permanent shutdown fence
- **THEN** either admission wins and shutdown stops/drains that exact owner through its bound stop path, or the request receives Unavailable and launches nothing

#### Scenario: Private or another Web worker bypasses local coordination
- **WHEN** a cache worker is started through unsupported private invocation or another Web process has its own idle coordinator
- **THEN** no distributed cache lock or reuse of advisory-lock exit 3 is implied; atomic publication remains the file-safety boundary and status does not claim cross-container exclusion

### Requirement: Interaction with processing, Lookup, and readers
`ProcessAssets` and `CoordinateLookup` SHALL ensure caches through the same worker-only source mutation core inside their already-admitted worker; they MUST NOT launch a nested `CacheMutation` process. Successful publication SHALL become visible to later processing, Lookup, and lightweight inventory/readers through on-disk validation rather than Web-process ready-cache state.

#### Scenario: Processing needs a missing cache
- **WHEN** an admitted ProcessAssets worker needs Overture or requested GADM data
- **THEN** it performs `Ensure` semantics in that worker, reports through its owning job lifecycle, and launches no nested child

#### Scenario: CoordinateLookup needs a missing cache
- **WHEN** an admitted CoordinateLookup worker needs a cache
- **THEN** it uses the same atomic ensure core under its existing job/admission and preserves block 48 cache progress, result, and cancellation semantics without a nested worker

#### Scenario: A later reader opens the cache
- **WHEN** mutation publication and worker cleanup have completed
- **THEN** later readers open the final database with released writer handles/pools and observe only a validated cache

### Requirement: Web request and result behavior
The Administrative Areas page SHALL route only its existing Overture/GADM **Re-download** mutations through a page-independent cache-mutation controller with one job identity and operation generation. It SHALL disable conflicting mutation controls from admission through process/stream finality, show discrete progress and one Cancel action while cancellable, correlate every callback, and render only an authoritative result or classified safe failure. It MUST NOT fall back to Web download/export or project transient frames into `ProcessingState`.

#### Scenario: Mutation is admitted
- **WHEN** the user selects **Re-download** and finalized block 50 returns Admitted with the sole owner handle
- **THEN** the page starts one v2 `Refresh` session, shows progress/cancel state, and performs no DuckDB, remote geodata, GeoPackage export, or native geodata work in Web

#### Scenario: Mutation is busy or unavailable
- **WHEN** admission returns Busy with a safe active snapshot or Unavailable with a safe pre-launch reason
- **THEN** the page shows a safe actionable message, starts no local fallback, and leaves the currently inventoried cache readiness unchanged

#### Scenario: Worker completes or no-ops
- **WHEN** an authoritative completed result arrives and session finality is reached
- **THEN** the page reports the result disposition/metadata and explicitly reloads cache status; after block 53 that reload uses and invalidates the lightweight inventory service rather than a heavy cache service

#### Scenario: Worker fails, crashes, or is cancelled
- **WHEN** terminal or controller classification reports failure, crash, protocol/transport error, missing terminal, forced stop, or cancellation
- **THEN** the page distinguishes the outcome, never claims re-download success from progress, and reloads actual cache status after cleanup

#### Scenario: Page is disposed or receives stale callbacks
- **WHEN** navigation/circuit disposal occurs or a callback has the wrong job kind, identity, or operation generation
- **THEN** the controller suppresses stale rendering, uses the owner-bound bounded session stop/dispose path, and releases the matching handle exactly once only after classifier and process/stream/protocol/bridge finality

### Requirement: Deletion and inventory ownership remain separate
This change SHALL NOT route per-cache deletion or delete-all through `CacheMutation`, alter their semantics, or create block 53's read-only inventory service. It SHALL provide an authoritative mutation-completed signal/reload seam that later inventory invalidation can consume.

#### Scenario: User selects Delete or Delete All
- **WHEN** a deletion action is invoked
- **THEN** behavior remains owned by block 52 and is not encoded as an Ensure or Refresh worker operation in this change

#### Scenario: Cache metadata is inspected
- **WHEN** Data or Administrative Areas loads cache counts/status before block 53 is applied
- **THEN** inspection remains a lightweight read and does not trigger repair, download, export, geodata resolution, or a cache worker

### Requirement: Protocol and process verification
The cache-mutation contract SHALL have deterministic no-network codec goldens, source-operation fixtures, and real child-worker coverage while preserving every v1 and existing v2 ProcessAssets/CoordinateLookup golden byte-for-byte.

#### Scenario: Canonical protocol fixtures run
- **WHEN** CacheMutation request, progress, completed/no-op result, GADM attribution, failed/cancelled terminal, bounds, mismatch, and malformed fixtures are encoded and decoded
- **THEN** canonical bytes and fail-closed behavior match the v2 contract without changing prior goldens

#### Scenario: Real worker fixture runs
- **WHEN** checked-in or controlled source fixtures exercise ensure, refresh, cancellation, failure, retry, and publication
- **THEN** tests prove registered-kind advertisement, one identity, validation-before-heavy-DI, balanced events, terminal uniqueness, cleanup/pooling, process/stream finality, no Web/native geodata initialization, and managed exits 0/2/4/5/6/130 with no cache-local exit 3
