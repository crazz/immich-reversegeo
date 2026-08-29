## Context

See `proposal.md` for motivation and `specs/cache-inventory/spec.md` for behavior. Today `Data.razor` and `GeoBoundaries.razor` synchronously call `GetStatus()` on `OvertureDivisionCacheService` and `GadmDivisionCacheService`. Those methods enumerate `*.db`, execute `COUNT(*)` against `division_area` or `gadm_area`, read `_meta`, and collapse every per-file exception into a zero-row record. GeoBoundaries displays Source, ISO3, Area Count, Version/Release, Size, and Downloaded At; absent files and operation temporaries have no rows.

Storage paths are `<DataDir>/overture-divisions/{ISO3}.db` and `<DataDir>/gadm-divisions/{ISO3}.db`. Current producers use same-directory GUID temporaries, and finalized change 51 may refine those names and publication rules. Finalized change 51's explicit completion result and finalized change 52's explicit per-cache/delete-all result(s) are prerequisites. Change 52 remains independent of inventory and keeps its current explicit page reload; it neither receives nor binds to a change-53 invalidator. Change 53 alone adapts both existing result surfaces after they have landed.

## Goals / Non-Goals

**Goals:**
- Make Web cache inspection lazy, storage-only, bounded, race-tolerant, and safe to register as a singleton.
- Give Data UI a closed, honest state model and immutable snapshots.
- Preserve source/ISO filtering, useful filesystem/metadata columns, actions, and GADM licensing while removing the expensive area-count read.
- Consume successful finalized outcomes from 51 and 52 without coupling inventory to their worker/deletion implementations.

**Non-Goals:**
- Validate geodata semantics, count areas, run `integrity_check`, open geometry blobs, or prove that every row is usable.
- Download, refresh, repair, delete, quarantine, clean orphan temporaries, or own mutation admission.
- Watch the filesystem, promise a multi-file transactional view, or coordinate independent Web containers.
- Remove all heavy Web registrations; change 55 owns that after consumers migrate.

## Decisions

### 1. Use a Web-local, lazy singleton with storage-only dependencies

Register a single inventory implementation only in Web-capable composition. Its constructor receives validated `StorageOptions` plus narrow internal clock/filesystem/SQLite-opening seams needed for deterministic tests; it does not receive either cache service, `CountryCodeService`, a resolver, exporter, DuckDB connection, worker launcher, processing state, or hosted-service dependency. Construction only canonicalizes configuration and validates fixed internal limits. No directory is created and no scan starts until an API call.

A singleton is preferable to scoped page instances because it can coalesce concurrent circuits and accept centralized invalidation. A hosted poller or watcher was rejected because explicit Data access is sufficient and background observation adds lifecycle and platform-specific failure modes.

### 2. Publish explicit immutable DTOs and remove area count

Use conceptual DTOs equivalent to:
- `CacheInventorySnapshot(Generation, ObservedAtUtc, Sources)`;
- `CacheSourceInventory(Source, State, IsTruncated, SafeDiagnosticCode, Entries)`;
- `CacheInventoryEntry(Source, Iso3, Status, FileSizeBytes?, LastModifiedUtc?, DownloadedAtUtc?, DatasetVersion?, HasTemporaryArtifacts, SafeDiagnosticCode?)`.

The source and status fields are closed enums. Status precedence is: unsafe exact candidate; stable final file (`Available`, `Invalid`, or `Unreadable`); recognized temporary without a final (`InProgress`); otherwise exact lookup `Absent`. A valid final remains authoritative when a refresh temporary coexists, while `HasTemporaryArtifacts` makes the transient work visible. Listings contain discovered canonical finals/temporaries only; exact lookup can return `Absent` without loading a country catalog.

`RowCount` is intentionally not in the DTO. Existing tables do not store a cheap authoritative count in `_meta`, and `COUNT(*)` can scan data/index pages. GeoBoundaries replaces Area Count with Status and adds Last Modified; nullable downloaded time and normalized dataset version preserve the useful existing metadata. Alternative compatibility via a nullable row count was rejected because it invites later content scans and makes the lightweight contract ambiguous.

### 3. Restrict paths before any open

At construction, canonicalize `DataDir` as the trust root and derive exactly two immediate child directories. Do not accept source paths, filenames, URLs, or globs from callers. Exact lookup accepts a closed source and strict `^[A-Z]{3}$` ASCII ISO3, then derives the basename itself. Listing accepts only immediate entries matching `{ISO3}.db` or the finalized change-51 operation-owned temporary patterns; unrelated and lowercase names are ignored.

The configured trust root itself may be a container mount, but source-directory descendants and candidate files that are symlinks/reparse points are never followed. Confirm containment after canonical combination and again before open. An exact canonical candidate that is a link is `Unsafe`; a linked source directory is a source-level failure. This is stricter than following links and checking the eventual target, avoiding TOCTOU escapes and host-file disclosure.

### 4. Inspect only filesystem facts and bounded SQLite metadata

For each stable final candidate, capture size and UTC mtime, then open SQLite with read-only mode, `Pooling=false`, private/non-shared cache behavior, a short bounded busy timeout, and deterministic disposal. Query `sqlite_schema` only to verify the source's expected table and read a fixed allowlist of `_meta` keys (`downloadedAt` and `release` or `version`) with bounded result sizes. Do not select from `division_area`/`gadm_area`, count rows, run integrity checks, attach databases, or retain a connection/reader after mapping the DTO.

Zero length, non-SQLite content, malformed bounded metadata, or a missing expected table is `Invalid`; missing optional keys leave nullable fields. Access denied and transient I/O/locking errors are `Unreadable` with safe closed diagnostics. Directory absence is an empty source rather than an exception. Directory permission failure is source-level `Unreadable`. Logs may include an operator-safe exception category but UI DTOs never contain absolute paths or raw exception text.

### 5. Bound scans and use immutable generation-aware caching

Use validated internal options (not user-facing AppConfig) for maximum immediate candidates per source, maximum recognized temporaries per ISO, metadata value length, SQLite busy timeout, and at most one retry for a changed candidate. Enumerate deterministically by canonical basename, stop at the candidate bound, and mark the source truncated. Inspect sequentially or with a small fixed concurrency; never create one unbounded task per file.

The singleton holds at most the latest immutable snapshot and one in-flight scan per generation. Normal internal consumers may reuse a clean snapshot, while page initialization and post-operation reread use an explicit refresh API. There is no indefinite TTL promise: invalidation marks relevant keys/source or the whole snapshot dirty, and the next ordinary access scans. This balances repeated Blazor renders against the master plan's explicit-access semantics.

### 6. Make file races and invalidation races monotonic

Record final-file size/mtime before and after SQLite reads. If the file changes or disappears, close all handles and retry once from path derivation. A second change returns a safe transient `Unreadable` classification. Thus an atomic publication yields a stable old or new entry, not mixed facts; a deletion becomes absent/in-progress after retry when observable. Multi-file snapshots remain point-in-time approximations and carry `ObservedAtUtc`.

Each invalidation atomically increments a generation. A scan records its starting generation and may become the reusable snapshot only if the generation is unchanged when it completes. If invalidation happens mid-scan, callers may finish with their immutable pre-invalidation observation, but it cannot clear dirty state or overwrite the next generation. This is simpler and safer than locks spanning filesystem I/O or mutation operations.

### 7. Own adapters over finalized 51/52 results at the Web boundary

Change 53 adds adapters at the existing page/controller consumption boundary; it does not add protocol events or require either prerequisite to call an inventory interface. After finalized change 51 exposes an authoritative successful typed result and complete process/session finality, the change-53 adapter invalidates that source/ISO before inventory refresh. `AlreadyReady` may invalidate harmlessly because explicit storage remains authoritative. Failed/cancelled/busy/unavailable/protocol/transport outcomes do not signal success.

After finalized change 52 returns its explicit per-cache or delete-all result(s), the change-53 adapter invalidates only entries whose result is actually `Deleted`; a partial delete-all retains every failure and does not pretend failed keys disappeared. Change 52's current explicit page reload remains its own behavior and has no inventory dependency. Bind the adapters to exact landed result symbols at apply time and stop if semantics differ; do not edit 51/52 or create replacement completion/deletion contracts.

### 8. Migrate Data consumers without moving mutation ownership

`Data.razor` counts only `Available` entries for its source totals and displays a safe source-level warning/truncation state when needed. `GeoBoundaries.razor` projects immutable entries to its page row model, preserves exact `Overture`/`GADM` labels, case-insensitive ISO substring filtering, source filtering, and column sorting with deterministic source+ISO tie breakers. Replace Area Count with Status, add Last Modified, retain Version/Release, Size, Downloaded At, and existing actions. Non-available rows display safe state rather than zero rows/nulls. GADM attribution/license copy remains independent.

Page initialization explicitly refreshes inventory. After finalized 51/52 operations return, change-53-owned adapters inspect their explicit results, invalidate qualifying keys, and then let the migrated Data consumer reread inventory; block 52's preexisting explicit page reload is not a dependency on inventory. Existing page operation generations still suppress stale/disposed renders; the inventory does not own UI concurrency. Standard and Web-only use the same service; worker/run-once hosts do not initialize it for jobs.

## Risks / Trade-offs

- [Schema presence cannot prove semantic geodata validity] → Name the state `Available`, not `Valid`, and leave deep validation to worker mutation paths.
- [Strict no-link policy can reject operator-created linked cache files] → Document the safe diagnostic and require real files beneath configured source directories.
- [Short read-only opens can briefly overlap deletion on platforms with restrictive sharing] → Disable pooling, dispose per entry, retry boundedly, and let change 52 report actual deletion failures.
- [A huge or hostile directory can hide later entries behind the bound] → Deterministically mark the source truncated; never present the result as complete.
- [Removing Area Count changes visible UI] → Replace it with explicit status/mtime and cover the projection; retaining the count would violate the lightweight boundary.
- [Finalized 51/52 result symbols may differ from current planning names] → Preserve numbered order, apply 51 then 52, and bind change-53-owned adapters only after inspecting their landed explicit results; stop rather than invent parallel outcomes.

## Migration Plan

1. Apply/finalize change 51 and then change 52; at change-53 apply start, verify their landed temporary-name policy, source/ISO validation, completion finality, explicit deletion result fields, and existing page reload behavior.
2. Add storage-only DTOs, safe path policy, bounded scanner, SQLite metadata reader, immutable snapshot cache, and internal invalidation operations with focused tests.
3. Register the lazy singleton in Web-capable composition and add sentinel tests proving no heavy factories resolve or startup scan occurs.
4. Migrate Data and GeoBoundaries reads/presentation, preserving filters/actions/licensing and replacing Area Count with Status/Last Modified.
5. Add change-53-owned adapters over the exact finalized 51/52 result surfaces, invalidate only authoritative completion or actual `Deleted` keys before inventory reread, and test success, partial success, failure, finality, and generation races without modifying either prerequisite.
6. Keep the old source `GetStatus()` APIs until all non-Data consumers are checked; change 55 may then remove Web-heavy registrations. Rollback returns Data reads to those APIs without changing cache files or mutation protocol.
