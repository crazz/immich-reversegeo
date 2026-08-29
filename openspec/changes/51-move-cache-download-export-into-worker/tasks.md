## 1. Prerequisite reconciliation and characterization

- [ ] 1.1 Re-read the applied block 47 v2 job kind, JobId, typed codec/event/result/terminal, handler registry, descriptor, launcher/session/cancel/classifier, exit, and process-fixture symbols; record the exact APIs consumed and stop if `CacheMutation` or finality semantics differ from the finalized artifacts.
- [ ] 1.2 Re-read finalized/applied block 50 source immediately before implementation; bind to its exact landed `ExclusiveHeavyGeodata`, Admitted/Busy/Unavailable union, owner handle, safe active snapshot/category/origin, monotonic lifecycle, shutdown fence, and classifier/process-stream-final exact-once release symbols without editing block 50 or introducing parallel names, DTOs, gates, identities, or ownership.
- [ ] 1.3 Characterize current Overture/GADM ensure, in-flight sharing, release/version metadata, temporary paths, schema validation, publication, pooling, cancellation, and status-reader behavior with focused tests before extraction.
- [ ] 1.4 Inventory `Data.razor` and `GeoBoundaries.razor` controls and lock scope to the existing per-row **Re-download** mutation; record **Delete**/**Delete All** as block 52 and status/inventory extraction as block 53.

## 2. Typed CacheMutation protocol

- [ ] 2.1 Add the concrete v2 `CacheMutation` request with closed `Overture`/`Gadm` source and `Ensure`/`Refresh` operation discriminators and no path, URL, release override, arbitrary options, delete, or second identity.
- [ ] 2.2 Add semantic validation for exact uppercase known ISO3 plus source mapping, kind/payload agreement, unexpected fields, result/progress bounds, and validation-before-handler/heavy-DI/filesystem/network behavior.
- [ ] 2.3 Add the closed discrete cache progress steps, `AlreadyReady`/`Published` completed result metadata, and stable GADM dataset/version/license URL/non-commercial-use attribution.
- [ ] 2.4 Add the Cache maintenance/cache-UI-origin, cancellable/heavy/geodata-bearing descriptor using finalized block 50's exact landed `ExclusiveHeavyGeodata` metadata and typed handler registration, advertising `CacheMutation` in ready only when registry startup validation succeeds.
- [ ] 2.5 Add canonical positive/negative NDJSON goldens for both sources/operations, every progress/result variant, GADM attribution, malformed ISO3/discriminators/properties, kind mismatch, bounds, completed/failed/cancelled terminals, and ready advertisement; prove all v1 and existing v2 goldens remain byte-for-byte unchanged.

## 3. Worker-only source mutation core

- [ ] 3.1 Extract one worker-only cache mutation abstraction used by CacheMutation, ProcessAssets, and CoordinateLookup, with typed source outcomes, reporter, and cancellation token but no launcher, admission, terminal emission, `ProcessingState`, or Web dependency.
- [ ] 3.2 Implement Overture Ensure/Refresh using the existing centralized DuckDB HTTP/Azure/spatial bootstrap and release fallback, with canonical alpha2 mapping and actual release metadata.
- [ ] 3.3 Implement GADM Ensure/Refresh using the mapped country GeoPackage and exporter, with stable dataset/version/license attribution and active-token-aware streaming.
- [ ] 3.4 Preserve in-worker per-country in-flight pooling for concurrent callers while removing completed/faulted/cancelled entries and proving no stale task prevents a later explicit retry.
- [ ] 3.5 Adapt ProcessAssets and finalized CoordinateLookup cache ensuring to call the core inside their already-admitted worker and project progress through the owning job; add negative coverage proving neither launches a nested CacheMutation worker.

## 4. Atomic publication, cleanup, and permissions

- [ ] 4.1 Derive final and unique same-directory temporary paths only from configured `DataDir`, validated source, and canonical ISO3; add safe directory/temp writability errors without returning/logging host paths.
- [ ] 4.2 Build candidates without touching the final cache, validate expected source schema/table, nonzero rows, required metadata, source code/release/version, and readable SQLite, then re-observe final metadata after publication.
- [ ] 4.3 Replace delete-then-move refresh with a supported same-directory atomic replacement that preserves the prior verified cache on download/export/validation/publication failure and refuses a non-atomic fallback.
- [ ] 4.4 Dispose DuckDB, HTTP, GeoPackage, SQLite readers/writers/transactions before publication; use `Pooling=false` or connection-specific `ClearPool` and remove mutation-path `ClearAllPools`; prove later readers can open the replacement.
- [ ] 4.5 Clean only operation-owned and conservatively attributable stale `.tmp`/`.gpkg.download` artifacts in structured unwind on success/failure/cancellation while never deleting another live operation's candidate.
- [ ] 4.6 Add cancellation checkpoints/propagation around every token-aware and phase boundary; prove pre-publication cancellation preserves old data, post-publication cancellation retains the valid replacement without success result, and force-kill recovery leaves no reported partial cache.
- [ ] 4.7 Keep full-operation retry explicit rather than automatic: after final cleanup/admission release, a new JobId rechecks actual cache state and can succeed without stale in-flight state or pooled handles.

## 5. Progress, logging, result, and exit lifecycle

- [ ] 5.1 Report bounded discrete checking/preparing/downloading/exporting/validating/publishing/completed steps without invented percentages; bracket long work in balanced unique common activities.
- [ ] 5.2 Emit secret-safe bounded logs and stable errors that omit local paths, stack traces, raw request data, stderr, credentials, and sensitive URL/query details while retaining source/operation/ISO3 diagnostics.
- [ ] 5.3 Return authoritative completed metadata only after no-op observation or validated publication; ensure failure/cancellation/crash/protocol/transport paths cannot fabricate ready/success from transient progress.
- [ ] 5.4 Preserve block 47 terminal and exit rules for exits 0/2/4/5/6/130, one host-owned terminal only after acceptance, controller classification otherwise, and no cache/local-busy use of exit 3 or processing PostgreSQL advisory lock.

## 6. Web cache mutation controller and page routing

- [ ] 6.1 Add a page-independent cache mutation controller/state seam that snapshots source/ISO3/Refresh, creates the sole JobId plus page operation generation, and consumes finalized block 50's atomic first-wins admission: bind one session only for Admitted(owner handle), and return Busy(safe active snapshot) or Unavailable(safe pre-launch reason) with no worker, fallback, queue, retry, preemption, or cancellation/release capability.
- [ ] 6.2 Correlate every callback/update/release by exact v2 kind, JobId, page generation, and owner handle; advance only the matching landed Admitted/Starting/Running/Stopping/Finalizing lifecycle, set PID only after process creation, reject stale/non-owner mutations, and release exactly once only after classifier plus process/stdout/stderr/protocol/bridge finality for completion, startup failure, failure, cancellation, crash, protocol/transport failure, forced stop, or disposal.
- [ ] 6.3 Route only `GeoBoundaries.razor` per-row **Re-download** actions to one admitted `Refresh` worker with no direct cache deletion, download/export call, `Task.Run` fallback, nested worker, or `ProcessingState` update.
- [ ] 6.4 Disable conflicting cache mutation controls from admission through authoritative finality, expose one idempotent Cancel/Cancelling action only to the admitted owner, render safe Busy category/origin/lifecycle and Unavailable reason separately from failed/crashed/protocol/cancelled outcomes, and derive success/no-op copy only from the typed result.
- [ ] 6.5 Keep GADM's academic/other non-commercial warning and official `https://gadm.org/license.html` link visible for available, active, completed, failed, and cancelled GADM mutation states, separate from technical error text.
- [ ] 6.6 Explicitly reload actual cache status after final cleanup and publish a narrow mutation-completed/invalidation notification for block 53; do not implement block 53 inventory DTOs/scanning/caching or change block 52 deletion semantics.
- [ ] 6.7 Add Standard/Web-only/run-once composition tests proving interactive mutations are equivalent in Web modes, absent in run-once, and the Web mutation graph resolves no DuckDB, remote geodata client, GADM exporter, resolver, or in-process mutation fallback.

## 7. Source, controller, and process verification

- [ ] 7.1 Extend `OvertureDivisionCacheServiceTests.cs` for no-op ensure, refresh, release metadata, invalid mapping/schema/zero rows, old-cache retention, candidate cleanup, pool release, permissions, cancellation boundaries, and retry with a new identity using deterministic no-network seams.
- [ ] 7.2 Extend `GadmDivisionCacheServiceTests.cs` and exporter tests for mapped packages, download/export/version/license metadata, source/output pooling, invalid GeoPackage/schema/zero rows, old-cache retention, cleanup, permissions, cancellation boundaries, and retry without live GADM access.
- [ ] 7.3 Add cache controller/page-state tests for immutable requests; atomic Admitted/Busy/Unavailable and first-wins races; safe active snapshot; no worker/exit/fallback/cancel/release on rejection; progress/activity/log projection; completed/no-op metadata; monotonic lifecycle/PID timing; every classified failure; cancel-before-start and cancel/terminal races; shutdown fencing; repeated cancel; stale/non-owner callbacks/releases; finality-before-release; disposal; status reload; no `ProcessingState`; and subsequent reuse.
- [ ] 7.4 Extend the real child-worker fixture with checked-in tiny source/SQLite/GeoPackage data and controlled faults/gates; assert ready advertisement, one identity, validation-before-heavy-DI, event order, balanced activities, terminal uniqueness, atomic publication, temp/pool cleanup, permission failure, cancellation, retry, process/stream finality, and exits 0/2/4/5/6/130 with no exit 3.
- [ ] 7.5 Add interaction tests proving ProcessAssets, CoordinateLookup, and CacheMutation contend equally in one process-local `ExclusiveHeavyGeodata` slot; Busy/Unavailable starts no second process or file touch; multiple Web coordinators are not distributed exclusion; and in-worker processing/Lookup Ensure semantics publish data consumable by later readers without nested launch.
- [ ] 7.6 Keep block 52 deletion tests/artifacts and block 53 inventory artifacts untouched; add only boundary assertions that CacheMutation rejects deletion and emits the completion/reload seam later inventory invalidation consumes.

## 8. Documentation and strict completion

- [ ] 8.1 Update concise public Data/using-the-app and data-source guidance for worker-backed Re-download, progress/cancellation/failure retention, safe retry, and the unchanged GADM non-commercial-use limitation; do not document the private worker protocol or promise cross-container cache locking.
- [ ] 8.2 Run focused protocol/cache/controller/composition/process tests, `npm run test`, relevant explicit integration tests with deterministic fixtures, and `npm run docs:build`; record any environment-gated suite rather than using live downloads in normal tests.
- [ ] 8.3 Run `openspec validate 51-move-cache-download-export-into-worker --strict`, inspect `openspec status --change 51-move-cache-download-export-into-worker`, and perform a block-51-only diff review proving no block 50 implementation/artifact, block 52 deletion, block 53 inventory, unrelated MASTERPLAN block, or project code outside implementation scope was combined.
