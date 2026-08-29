## Context

See `proposal.md` for motivation and `specs/worker-cache-operations/spec.md` for behavior. Block 47 finalizes v2 around one `JobId`, case-sensitive closed `jobKind`, concrete request/event/result variants, common log/activity/terminal frames, a startup-validated typed handler registry, one host-owned terminal after acceptance, reusable session/cancellation/classification, and immutable arbitration metadata. It reserves `CacheMutation` but deliberately supplies no payload or handler. Finalized block 50 provides one singleton process-local coordinator and `ExclusiveHeavyGeodata` slot, exact first-wins `Admitted(owner handle)` / `Busy(safe active snapshot)` / `Unavailable(safe pre-launch reason)` outcomes, safe Cache maintenance category and cache-UI origin metadata, monotonic Admitted/Starting/Running/Stopping/Finalizing lifecycle, owner-only cancellation/release, permanent shutdown fencing, and release after classifier plus process/stdout/stderr/protocol/bridge finality. It adds no queue, retry, preemption, priority, fairness, starvation guarantee, distributed lock, or CacheMutation operation. Exit 3 remains exclusive to the processing PostgreSQL advisory lock.

Current source behavior is asymmetric but follows the same pipeline. Overture maps ISO3 to alpha2, discovers/falls back to an Overture release, loads centralized DuckDB HTTP/Azure/spatial support, exports `division_area` into a unique `overture-divisions/{ISO3}.*.tmp`, validates, and overwrites `{ISO3}.db`. GADM maps ISO3 to its country package, downloads a GeoPackage to `gadm-divisions/{ISO3}.*.gpkg.download`, exports layers to a unique temporary SQLite database, validates, and overwrites `{ISO3}.db`. Both have per-process in-flight maps and ready-cache maps. SQLite inspection uses `Pooling=false`; exporters close and clear output pools, while GADM publication currently calls the overly broad `ClearAllPools`.

`GeoBoundaries.razor` currently exposes only per-row **Delete** and **Re-download**, plus source-specific **Delete All**. Re-download is delete-then-`EnsureDataAsync`, so a failed refresh destroys a known-good cache. `Data.razor` and the page inspect `GetStatus()` through the same service types that also expose heavy mutation. Block 52 owns all deletion and block 53 owns extraction of read-only inventory. Processing and current Lookup call `GetOrStartDownload`/`EnsureDataAsync`; block 48 requires CoordinateLookup to keep cache ensure and publication inside its worker and never nest workers.

GADM carries an academic and other non-commercial-use restriction; the official project license URL is `https://gadm.org/license.html`. Process isolation must not make that attribution disappear.

## Goals / Non-Goals

**Goals:**

- Fill block 47's reserved `CacheMutation` v2 variant with source/operation-specific, fail-closed transport and handler behavior.
- Make the current Data/Administrative Areas re-download flow a cancellable, admitted worker operation with authoritative result projection.
- Reuse one worker-only mutation core from standalone cache jobs, ProcessAssets, and CoordinateLookup without nested workers or semantic drift.
- Improve refresh safety to preserve the prior verified database until a complete replacement is validated and atomically published.
- Make cleanup, pool release, cancellation boundaries, retry eligibility, licensing, paths, permissions, and reader visibility explicit and testable.

**Non-Goals:**

- Add a user-facing “download missing country” picker/button, accept arbitrary release/URL/path input, or redesign cache schema/layout.
- Move **Delete**, **Delete All**, or stale-file administrative cleanup into the worker; block 52 coordinates deletion.
- Implement the block 53 inventory service or block 55's final removal of every heavy Web registration.
- Change processing/Lookup resolution preference, territory expansion, diagnostics, or GADM license terms.
- Add a durable queue, automatic job replay, cross-container cache lease, or broaden the processing advisory lock.

## Decisions

### 1. Use one CacheMutation job with closed source and operation discriminators

Add one concrete v2 request equivalent to `CacheMutationRequest(Source, Operation, Iso3)`, selected by envelope kind `CacheMutation`. `Source` is exactly `Overture` or `Gadm`; `Operation` is exactly `Ensure` or `Refresh`. Job identity remains only envelope-level. The descriptor declares friendly Cache maintenance category, cache-UI origin, cancellable=true, heavy=true, geodata-bearing=true, and finalized block 50's `ExclusiveHeavyGeodata` resource class. At apply time, reuse the exact landed descriptor/origin/category symbols rather than introducing parallel source names.

`Ensure` is retained even though the current page submits only `Refresh`: it gives the shared worker-only mutation core one named semantic for processing/Lookup and lets protocol/handler tests prove no-op behavior. The Administrative Areas **Re-download** button always maps to `Refresh`; no new UI control exposes `Ensure` in block 51.

Deletion is intentionally absent from the union. This keeps cache build/export lifetime with a temporary heavy worker while block 52 can leave small filesystem deletion in Web under a compatible coordinator lease.

Alternative: define four or six top-level job kinds. Rejected because block 47 reserves one cache capability and source/operation are a small closed product. Alternative: one free-form cache command. Rejected because it permits incompatible fields and unsafe path/URL input. Alternative: encode deletion as a third operation. Rejected because it combines parallel-owned block 52 and changes its selected lightweight-Web design.

### 2. Validate canonical source identity before handler resolution or side effects

The v2 semantic validator requires exact uppercase ASCII `[A-Z]{3}`, then resolves the code against the bundled country identity catalog. Overture additionally requires a known alpha2 mapping; GADM requires its mapper to produce a supported canonical package code. Lowercase and whitespace are rejected rather than normalized on the wire so canonical bytes, logs, paths, and identity remain deterministic. The page already receives ISO3 from inventoried rows and sends canonical values.

The request has no path, URL, desired release, overwrite flag, or options bag. Source paths are derived only after validation from `StorageOptions.DataDir`: `overture-divisions/{ISO3}.db` or `gadm-divisions/{ISO3}.db`. Same-directory GUID-named candidates avoid cross-device moves and path traversal. Directory/temp creation failure maps to a stable safe storage error; logs/results omit the local path.

Alternative: normalize arbitrary text in the worker. Rejected because it weakens strict protocol goldens and can hide malformed callers. Alternative: trust three letters without catalog lookup. Rejected because unsupported values reach network/path work and Overture mapping currently fails late.

### 3. Extract a worker-only mutation core and keep job ownership outside it

Refactor source cache services so filesystem/network/export mechanics are callable through a worker-only operation abstraction with typed source outcomes. It accepts canonical source/code, `Ensure` or `Refresh`, an event/reporting abstraction, and a cancellation token. It does not emit protocol terminal frames, acquire admission, launch processes, or update `ProcessingState`.

The standalone `CacheMutation` handler adapts this core to CacheMutation progress/result. ProcessAssets and CoordinateLookup call the same core directly within their existing admitted worker and project source progress into their own owning job contracts. They never invoke the launcher. This preserves one process, one JobId, one admission owner, one terminal, and block 48's lookup-specific diagnostics.

Per-country in-flight maps remain useful inside a worker for concurrent assets/territory candidates. Entries are removed after completion/failure/cancellation. They are not treated as cross-process coordination; block 50 prevents local Web-launched heavy overlap, and no new cross-container lock is introduced.

Alternative: have processing/Lookup start a CacheMutation child. Rejected because it nests temporary workers, creates two identities/terminals, deadlocks exclusive admission, and breaks cancellation ownership. Alternative: duplicate source code in each handler. Rejected because publication, validation, and licensing would drift.

### 4. Define atomic refresh as candidate-first validation and same-directory replacement

`Ensure` first performs source validation of the final cache. A valid nonempty cache returns `AlreadyReady` with observed metadata and does not touch timestamps/network/temp files. Otherwise it follows the candidate pipeline. `Refresh` always follows the candidate pipeline and never deletes the final cache first.

The source pipeline is:

1. create the configured source directory and uniquely named same-directory owned temporaries;
2. clean only stale artifacts that are safely attributable to the same canonical source/code and are not this operation's files (with conservative age/ownership rules rather than deleting every matching live candidate);
3. download/export into candidates using the centralized source bootstrap;
4. validate readable SQLite, expected table/schema, nonzero rows, required metadata, source release/version, and encoded ISO3 where present;
5. dispose readers, transactions, DuckDB/HTTP/GeoPackage handles, close writers, and clear only relevant SQLite pools/connections;
6. atomically replace the final file on supported filesystems, then re-open/observe final metadata for the result;
7. clean owned download/temp files in structured `finally` paths.

Do not use delete-then-move and do not use process-global `SqliteConnection.ClearAllPools`; it can disrupt unrelated worker readers. Prefer `Pooling=false` for candidate/final validation and `ClearPool(connection)` where an output connection used pooling. Publication occurs only after every candidate handle is closed. If the platform cannot satisfy same-directory atomic replacement while preserving the old file, fail and retain the old file.

Cancellation is checked at source phase boundaries and propagated into HTTP/copy and token-aware work. Synchronous DuckDB/SQLite export receives cooperative checkpoints where practical; otherwise the controller's existing bounded cancellation escalation terminates the worker. Cancellation before publication leaves the old final and cleans candidates. Cancellation after publication never rolls back a valid shared cache; the cancelled terminal carries no success result and Web re-reads actual storage.

Alternative: preserve current delete-then-ensure. Rejected because a transient failure turns refresh into data loss. Alternative: overwrite the final while exporting. Rejected because readers can see partial schema/data. Alternative: clear all SQLite pools before move. Rejected because it has process-wide side effects and hides ownership bugs.

### 5. Keep source-specific network/export policy while making retry semantics explicit

Overture continues to use the centralized DuckDB bootstrap, including Linux Azure curl transport, and its documented release fallback. GADM continues to stream one mapped GeoPackage and export its feature layers. `HttpClient` ownership should move to the worker DI/source client seam rather than creating an unmanaged client per call, but this change does not invent a broad new HTTP policy.

The job itself performs no automatic full-operation replay: repeated export/publication can be expensive and cancellation must not silently restart work. A failed/cancelled outcome removes source in-flight state, then the admitted owner releases finalized block 50 admission only after classifier and process/stream/protocol/bridge finality; an explicit later click gets a new JobId and re-evaluates the actual final cache. If an existing project-wide source client later supplies bounded idempotent transport retries before bytes are committed, those remain internal source behavior and must honor cancellation; protocol progress/logs identify a retry safely without changing the one job identity.

Alternative: automatically rerun every failed mutation. Rejected because it obscures terminal latency, can multiply remote/native work, and makes cancellation unreliable. Alternative: make the user delete before retry. Rejected because refresh must retain known-good data.

### 6. Use closed discrete progress and authoritative terminal metadata

Add CacheMutation progress steps equivalent to `CheckingExisting`, `PreparingSource`, `Downloading`, `Exporting`, `ValidatingCandidate`, `Publishing`, and `Completed`. Not every source emits every step. Status text, source, operation, and ISO3 are bounded and canonical. No percentage is emitted because Overture remote scan/export and GADM conversion do not have stable comparable totals.

Common activity frames bracket long download/export phases with unique IDs and always end during unwind. Common safe logs record transitions, no-op, publication, cleanup warnings, and safe source degradation/failure without local paths, stack traces, secrets, or raw signed/query URLs.

A completed typed result contains source, operation, ISO3, `AlreadyReady` or `Published`, row count, UTC downloaded timestamp, file size, and source release/version. GADM adds stable dataset name, official license URL, and the established academic/other non-commercial-use notice. The host remains sole terminal owner. Failed/cancelled terminals contain no cache result; transient events are never assembled into partial success by Web.

Alternative: reuse only strings/logs. Rejected because the page and tests need stable state and result fields. Alternative: stream row/byte percentages. Rejected because they are unavailable or misleading across sources.

### 7. Route Web through a page-independent controller and block 50 admission

Create a cache-mutation controller/state seam patterned after the generalized launcher consumer, not inline Razor protocol handling. It snapshots `Refresh` plus source/ISO3 from the selected row, creates the sole JobId and a page-only operation generation, and atomically requests finalized block 50 admission. `Admitted(owner handle)` binds exactly one v2 session with that JobId; `Busy(safe active snapshot)` and `Unavailable(safe pre-launch reason)` return immediately with no process, Web fallback, cancellation/release capability, queue, or automatic retry. The controller validates kind/identity/generation on every event and owns capability-specific cancellation/disposal/final projection without copying cache progress/results into the generic coordinator snapshot or `ProcessingState`.

From admission until authoritative cleanup the page disables Re-download and conflicting mutation controls and shows one Cancel action only for the admitted cancellable owner. The controller advances only the matching owner through the landed monotonic lifecycle and records PID only after process creation. Startup/configuration failure, worker failure, crash, protocol/transport/missing-terminal/forced-stop, shutdown, and cancellation are distinct. Success/no-op copy comes only from the typed completed result. Normal cancellation/navigation/circuit disposal uses the owner-bound bounded stop/dispose path; shutdown uses finalized block 50's permanent fence and bound owner stop path. The classifier finalizes first and release occurs exactly once only after process exit and stdout/stderr/protocol/bridge finality when launched; stale, wrong-kind, wrong-identity, duplicate, Busy, or Unavailable callers cannot alter or clear the owner.

Block 51 replaces direct page mutation calls only. Existing status calls can remain temporarily as lightweight reads until block 53 extracts them, but no page/controller code may invoke download/export, DuckDB, GeoPackage conversion, geodata resolution, or local fallback. After every authoritative finalization the page explicitly reloads on-disk status. Define a narrow mutation-completed notification/invalidation seam so block 53 can attach its inventory invalidation without changing the protocol/controller.

Deletion controls remain block 52-owned. Until block 52 lands, their current behavior is not silently folded into this controller; sequencing requires 51 before 52 and both before the final heavy-Web cleanup in 55.

Alternative: keep mutation calls inline and merely wrap them in `Task.Run`. Rejected because native memory remains in Web and lifecycle is not supervised. Alternative: update `ProcessingState`. Rejected because cache jobs are a separate capability and processing projections would be false. Alternative: implement inventory now. Rejected because block 53 owns its DTOs, unreadable/partial behavior, composition, and invalidation policy.

### 8. Preserve licensing and Web-only control-plane boundaries

GADM attribution is transport-owned so every GADM progress/result can render the same stable warning and `https://gadm.org/license.html`, including no-op, failure, and cancellation UI. Technical failure copy remains distinct from the legal restriction; block 51 does not add a new acceptance toggle or alter licensing terms.

The Web composition may resolve protocol DTOs/codecs, the block 50 coordinator, launcher/session client, cache controller, and temporary lightweight status path. It must not construct DuckDB, remote Overture/GADM clients, GADM exporter, source mutation handlers, or geodata resolvers for a re-download. Worker composition registers the concrete CacheMutation handler and source dependencies. Ready advertises `CacheMutation` only when startup validation succeeds.

Run-once has no interactive Data page, while Standard and Web-only use the same controller/worker behavior. The worker runs under the packaged application identity with the same configured `/data` mount; tests verify directory/file writability and safe permission failures. Block 55 later removes remaining heavy registrations once Lookup, mutation, deletion, inventory, and reset seams are complete.

Alternative: put license text only in Razor. Rejected because worker results and future consumers could lose attribution. Alternative: let Web compose source services but promise not to call heavy methods. Rejected for the mutation path because dependency construction makes the isolation boundary unenforceable.

### 9. Lock protocol compatibility and filesystem behavior with layered tests

Transport tests add canonical CacheMutation request/progress/result/terminal goldens, every discriminator, GADM metadata, bounds, unexpected fields, kind/payload mismatch, and invalid ISO3. They rerun all v1 and v2 ProcessAssets/CoordinateLookup goldens byte-for-byte.

Operation tests use deterministic injected source/download/export seams—never live Azure/GADM—to cover no-op ensure, missing ensure, refresh, old-cache retention, zero rows, invalid schema/meta/code, publication fault, permissions, cancellation at each boundary, post-publication cancellation, stale/owned temp cleanup, pool release, and explicit retry with a new identity. Existing `OvertureDivisionCacheServiceTests.cs` and `GadmDivisionCacheServiceTests.cs` remain characterization seams; deletion assertions are not expanded into block 52 policy.

Controller tests fake finalized block 50 admission and sessions to cover first-wins races; Admitted owner binding; Busy safe snapshots; Unavailable shutdown/launch reasons; no process/exit/fallback/cancel/release for rejected work; immutable snapshots; monotonic lifecycle and PID timing; progress; cancel/shutdown/terminal races; stale/non-owner updates/releases; classifier and process/stream/protocol/bridge finality before exact release; final status reload; disposal; subsequent reuse; process-local—not distributed—scope; and no `ProcessingState`. The real child fixture uses checked-in tiny SQLite/GeoPackage/source seams and asserts validation before heavy DI, ready advertisement, event/activity order, terminal uniqueness, exits 0/2/4/5/6/130, no exit 3, temp/pool cleanup, writer release, and stream/process finality. Composition tests prove the Web mutation graph resolves no DuckDB/geodata/export service and performs no Immich database/config/skipped writes.

## Risks / Trade-offs

- [Applied block 50 source names may differ from finalized planning labels] → At apply start bind to the exact landed coordinator, `ExclusiveHeavyGeodata`, admission union, owner handle, active snapshot, lifecycle, shutdown, and finality symbols; stop rather than edit block 50 or create parallel names, DTOs, gates, identities, or release ownership.
- [Atomic replace semantics differ by filesystem/platform] → Use same-directory candidates, characterize every supported platform/filesystem primitive, and fail before old-cache removal when atomic preservation cannot be guaranteed.
- [A reader still holds a SQLite handle] → Exclusive heavy admission prevents local processing/Lookup/cache overlap; close/clear only owned writer pools before publication and prove later readers can open the replacement. Do not mask ownership with global pool clearing.
- [Private or another-container workers bypass the Web coordinator] → Keep publication atomic and document that no new cross-container cache lock exists; an explicit future lock-domain change is required if deployment evidence demands one.
- [Cancellation cannot interrupt a native synchronous call immediately] → Add cooperative phase checkpoints and retain block 47's bounded process-tree escalation; candidates are isolated and cleaned on next safe startup if force-killed.
- [Block 53 inventory races planning ownership] → Expose only a completion/reload seam here; leave inventory DTOs, scanning, unreadable-file states, caching, and invalidation implementation to 53.
- [GADM isolation hides legal terms] → Carry stable attribution/license metadata in transport and assert UI copy on every GADM outcome.

## Migration Plan

1. Re-read applied block 47 and finalized/applied block 50 source. Bind to the exact landed `CacheMutation` reserved kind, JobId/event/result/terminal adapters, `ExclusiveHeavyGeodata` descriptor metadata, Admitted/Busy/Unavailable union, owner handle, safe active snapshot, monotonic lifecycle, shutdown fence, and classifier/process-stream-final release symbols; stop on incompatible prerequisites rather than forking or renaming them.
2. Add CacheMutation DTOs, validators, codecs, descriptor, canonical goldens, and registration-negative tests while keeping the kind unadvertised until its handler is complete.
3. Characterize current source cache behavior, then extract the shared worker-only Ensure/Refresh core with candidate-first validation, precise pool/handle cleanup, permission-safe errors, source fixtures, and GADM attribution.
4. Register the worker handler; adapt ProcessAssets/CoordinateLookup cache ensuring to the same in-worker core without nested launch and rerun their parity suites.
5. Add the Web cache controller and route only **Re-download** to admitted `Refresh`; retain deletion ownership in 52 and temporary lightweight status reads pending 53.
6. Run protocol, source, controller, composition, real-process, normal, and explicit integration suites, then strict OpenSpec validation and a block-51-only diff review.

Rollback unregisters `CacheMutation` and restores the prior direct re-download call path only as a code rollback; no cache or configuration migration is required. Existing valid cache databases remain compatible. Do not release a partial rollback that leaves Web pointing at an unregistered job.
