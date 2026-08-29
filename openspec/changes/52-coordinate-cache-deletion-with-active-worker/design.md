## Context

See `proposal.md` for motivation and `specs/cache-deletion-coordination/spec.md` for behavior. The current page calls heavy source services directly; both `DeleteFile` implementations derive paths from unchecked strings, silently swallow deletion failures, and remove every `{ISO3}.*.tmp` sibling. Status reads use `Pooling=false` and dispose SQLite objects, but an active worker can still own readers/writers or atomic-publication candidates. Finalized block 50 provides one process-local first-wins `ExclusiveHeavyGeodata` slot and shutdown fence; block 51 closes worker handles before finality and explicitly excludes Delete from its worker protocol. The existing Data page explicitly reloads status after its operations; block 53 later owns any inventory snapshot and invalidation adaptation.

## Goals / Non-Goals

**Goals:**

- Linearize lightweight Web deletion against every local exclusive heavy-geodata worker without a check-to-delete gap.
- Make source/country/path validation, symlink refusal, idempotency, partial outcomes, finalized results, and lifecycle behavior independently testable.
- Remove deletion's Web dependence on heavy cache services and preserve precise worker/file-handle ownership.

**Non-Goals:**

- Add Delete to block 51's `CacheMutation` worker contract, launch a child, or change Ensure/Refresh/download/export behavior.
- Introduce an inventory cache, invalidation interface/event, snapshot policy, DTO, scanner, or unreadable-state behavior; those remain later block 53 ownership.
- Add a distributed/file/database lease, protect unsupported independently launched workers, or make a shared volume safe across multiple Web containers.
- Delete worker-owned temporary candidates, recursively clean source directories, repair caches, or add a filesystem watcher.

## Decisions

### 1. Extend block 50's one resource gate with a lightweight maintenance reservation

At apply start, bind to the exact landed block 50 coordinator/resource gate. Extend that same atomic `ExclusiveHeavyGeodata` owner boundary with a non-worker Cache maintenance reservation; do not query the active snapshot and then delete, wrap the coordinator in a second lock, or create a parallel semaphore. Acquisition returns a closed Reserved(owner handle), Busy(safe active owner), or Unavailable(safe shutdown/configuration reason) result. First atomic winner owns the slot; losers fail immediately with no wait, queue, retry, preemption, priority, fairness, or starvation promise.

A maintenance owner has safe category Cache maintenance and cache-UI origin so later worker requests can receive truthful Busy metadata. It has no `WorkerJobKind`, JobId, PID, process lifecycle, cancellation capability, terminal, protocol frame, or exit code. Preserve block 50's canonical JobId and worker snapshot invariants for worker owners by representing the resource owner as a closed worker-or-maintenance shape rather than inventing a fake CacheMutation job. The opaque admitted handle remains the exact-once release capability.

Alternative: read `IsActive` before deleting. Rejected because worker admission can win immediately afterward. Alternative: model Delete as a block 51 worker operation. Rejected because it changes a finalized closed request, adds avoidable process cost, and contradicts the lightweight boundary. Alternative: add an outer maintenance lock. Rejected because all worker paths would need nested ordering and two authorities could diverge or deadlock.

### 2. Put deletion in a page-independent Web command service

The Razor page submits typed per-cache or source-specific Delete All commands to one lightweight service. The service validates before admission, acquires once, snapshots the validated Delete All targets, performs synchronous/bounded filesystem calls without `Task.Run`, records and finalizes typed outcomes, and releases in `finally`. Razor owns only confirmation/presentation and operation generation so stale callbacks cannot overwrite newer state.

Controls for Delete, Delete All, and Re-download are disabled while the page's mutation is unresolved. Busy and Unavailable return immediately with actionable safe copy. There is deliberately no Cancel button: `File.Delete` is not cooperatively cancellable, per-file deletion is short, and releasing on circuit cancellation could admit a worker while deletion continues. Navigation/circuit disposal suppresses rendering but does not abandon an admitted service operation. Block 51 retains Cancel for long-running worker Refresh.

Alternative: inline admission/filesystem logic in Razor. Rejected because navigation/disposal and race tests need lifecycle ownership independent of a circuit. Alternative: expose Cancel between Delete All files. Rejected because user cancellation adds another partial-outcome mode without interrupting the current file call; ordinary per-target failures already produce explicit partial results.

### 3. Validate identity before deriving a confined final path

Use a closed Overture/GADM source plus exact `^[A-Z]{3}$` ASCII input, the lightweight bundled country identity catalog, and source mapping (including the established GADM alias policy). Do not normalize user input. Only after validation derive the fixed source root and `{ISO3}.db`; accept no path, URL, or file name from Razor or any status row.

Canonicalize DataDir, source root, and target, require target containment using platform-appropriate path comparison, and inspect every existing component/target for link/reparse metadata immediately before deletion. Reject a linked configured root or final entry and never recursively traverse. The storage root is operator-controlled; this does not claim to defeat a hostile process swapping filesystem entries between checks, which belongs with a future OS-level/distributed storage lock.

Delete only the final database. Do not preserve the current broad `{ISO3}.*.tmp` deletion: block 51 owns unique candidates and cleanup, and another container may own a matching live candidate despite the local reservation.

Alternative: trust page/status paths. Rejected because display data and stale rows must not become filesystem authority. Alternative: sanitize arbitrary strings. Rejected because allowlisting a known canonical identity is simpler and testable.

### 4. Make outcomes explicit and Delete All best-effort under one reservation

Replace void/silent `DeleteFile` behavior in the Web path with typed `Deleted | Missing | Invalid | Failed` outcomes. Missing is idempotent success of the desired absent state. Map expected permission, read-only, sharing/in-use, directory, and I/O errors to bounded codes/copy that omit host paths and raw exceptions; log structured source/ISO3 plus exception internally under existing redaction policy. Unexpected programmer/cancellation failures still unwind through `finally` and release.

Delete All is source-specific and takes one immutable list of typed source/ISO identities from the current page state, validates every identity, acquires one reservation for the complete batch, and attempts each eligible target independently in deterministic ISO3 order. Invalid entries are not deleted. Return ordered target results and aggregate counts; never stop at the first ordinary filesystem failure or report the pre-operation count as deleted. An empty list is a truthful no-op.

Alternative: reserve once per file. Rejected because a worker could enter between files and make Delete All nondeterministic. Alternative: fail the entire batch on one error. Rejected because independent final files can safely produce more useful best-effort results when all outcomes remain truthful.

### 5. Rely on established finality and precise handle ownership

Block 51 must close SQLite/DuckDB/GeoPackage readers, writers, transactions, publication handles, and relevant connection-scoped pools before the worker owner releases after process/stream/protocol/bridge finality. The current Data-page status read uses short-lived `Pooling=false` connections and completes/materializes before invoking deletion, never passing a reader or connection. The deletion service itself opens no database connection and MUST NOT call `SqliteConnection.ClearAllPools` or retain a file stream.

Thus successful local reservation proves no coordinator-owned worker is still using a cache. A sharing violation/open-handle error from an uncoordinated process is a normal Failed outcome, not a reason to clear global pools or retry. Apply must re-read landed 50/51 and stop if their release/finality contracts do not provide these boundaries rather than create duplicate lifecycle owners.

Alternative: clear all SQLite pools before every delete. Rejected because it disrupts unrelated consumers, masks leaks, and cannot close another process's handle.

### 6. Finalize results, then let the existing page reload

The deletion command completes every requested filesystem attempt under the reservation, freezes the single-target or ordered batch `Deleted | Missing | Invalid | Failed` result, then releases its owner handle in `finally`. It neither knows about nor calls an inventory service, cache, invalidation event, snapshot, or reload seam. Finalized results are the stable later integration point; block 53 may observe/adapt them to invalidate its own snapshot without changing deletion semantics.

After the deletion task completes and ownership is released, the existing Data-page flow performs its current explicit status reload and derives presentation from the finalized result plus newly read disk state. The page keeps conflicting controls disabled through that reload. A page reload failure is page/read-path behavior, not a deletion result, and cannot rewrite a Deleted outcome or restore a file.

Alternative: add an invalidation contract now. Rejected because it reverses numbered dependencies and makes block 52 consume block 53. Alternative: hold resource ownership through page reload/rendering. Rejected because deletion safety ends once finalized filesystem work is complete; page reads are lightweight and should not extend exclusive ownership.

### 7. Fence shutdown without pretending lightweight deletion is cancellable

The block 50 shutdown fence linearizes with maintenance admission. If shutdown wins, deletion returns Unavailable and touches nothing. If deletion wins, shutdown observes that exact maintenance owner and awaits its completion/release through a bounded tracked task; it does not send worker cancellation or kill a process. Repeated shutdown joins the same fence path. Each file call is finite by platform contract but not cancellable; timeout diagnostics must not release the resource while code is still mutating.

Normal page disposal likewise cannot release the owner. All acquire/release, shutdown/admission, disposal/completion, result-finalization/release, and Delete All per-file race tests use barriers/fake filesystem operations rather than sleeps.

Alternative: release at the shutdown timeout. Rejected because a worker or disposal path could then overlap ongoing deletion. A host that cannot finish must fail shutdown diagnostics rather than fabricate safe release.

### 8. Keep the process-local limitation and UX explicit

The reservation protects only work admitted through one Standard/Web-only Web process. Multiple containers sharing `/data`, a manually invoked private worker, or direct host writes can overlap. Preserve block 50's guidance: strict exclusion currently requires one interactive Web control plane. Do not reuse the ProcessAssets PostgreSQL advisory lock or assign local contention worker exit 3.

Use existing alert/button patterns, distinguish Busy, Unavailable, complete success, already absent, partial success, and complete failure, and announce the final message accessibly without exposing paths or exceptions. Confirmation copy for Delete All states its source and best-effort partial-result semantics. No new GADM licensing text is needed for deletion; block 51 retains source attribution ownership and block 53 later owns inventory presentation.

## Risks / Trade-offs

- [Maintenance owner requires a safe non-worker busy projection] → Extend the landed resource-owner union narrowly; preserve every worker-specific JobId/kind invariant and add cross-owner compatibility tests.
- [Filesystem link checks have a trusted-root TOCTOU limit] → Derive from allowlisted identities, reject existing link/reparse components immediately before deletion, never recurse, document operator-controlled storage, and leave hostile/shared-volume locking to a distributed change.
- [The existing page reload fails after successful deletion] → Keep the finalized deletion result authoritative; treat the subsequent read failure separately and retry only through the page's existing explicit access path.
- [Another process/container holds or replaces the file] → Return safe per-target failure and state the process-local limitation; never clear all pools or claim distributed safety.
- [Apply prerequisites differ from planning names] → Bind to exact landed blocks 50/51 seams and stop rather than introduce a second gate or lifecycle owner; do not wait for or consume block 53.

## Migration Plan

1. Re-read applied blocks 50 and 51; bind to their exact resource owner/admission/shutdown, worker finality, and storage identity seams. Stop on incompatible prerequisites; do not consume block 53.
2. Add the non-worker Cache maintenance owner/reservation to the existing resource gate and cover worker-maintenance admission, release, and shutdown races before routing production deletion.
3. Add the lightweight validated storage deletion command with typed per-target/batch outcomes, final-only deletion, safe errors, and deterministic fake-filesystem tests.
4. Route only per-cache Delete and source-specific Delete All through the command service; keep block 51 Re-download on its worker controller and remove direct Web heavy-cache deletion calls.
5. Preserve the existing page-owned explicit reload, add operation-generation and accessible finalized-outcome UX, then add composition tests proving no heavy Web service, worker launch, protocol, processing state, global pool clear, or temporary cleanup.
6. Run focused tests, normal tests, docs build, strict OpenSpec validation/status, and a block-52-only scope review. Rollback restores the prior page call path and removes the maintenance owner extension together; no data/config migration is required.
