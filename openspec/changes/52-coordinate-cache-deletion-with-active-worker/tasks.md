## 1. Bind prerequisites and shared ownership

- [x] 1.1 Re-read the applied block 50 coordinator/resource-owner/admission/shutdown and block 51 worker finality/storage contracts; stop rather than create parallel gates, identities, or lifecycle owners, and do not consume block 53.
- [x] 1.2 Extend the existing `ExclusiveHeavyWorker` owner boundary with a closed non-worker Cache maintenance reservation and Reserved/Busy/Unavailable results while preserving all worker JobId/kind invariants.
- [x] 1.3 Implement exact-once maintenance-handle release, worker-versus-maintenance safe busy projection, and shutdown tracking without queueing, waiting, retries, cancellation, process launch, or worker exits.

## 2. Implement confined lightweight deletion

- [x] 2.1 Add a page-independent typed deletion command for one Overture/GADM ISO3 target and source-specific Delete All, with validation before admission.
- [x] 2.2 Before admission, validate exact uppercase known ASCII ISO3, source mapping, source mismatch, and duplicates (first unique eligible; later duplicates Invalid); after reservation, derive only the configured final cache path, prove canonical containment, and reject caller paths, traversal, and linked/reparse roots or targets.
- [x] 2.3 Replace silent void deletion in the Web path with Deleted/Missing/Invalid/Failed results, require explicit filesystem absence rather than `File.Exists(false)`, and return bounded safe permission/read-only/in-use/I/O errors plus idempotent missing-file semantics.
- [x] 2.4 Delete final `.db` files only; after confirming no other production caller, remove obsolete source-service public `DeleteFile` entry points while preserving internal cleanup and Ensure/Refresh behavior; use no database open, global SQLite pool clearing, geodata initialization, or `Task.Run` fallback.
- [x] 2.5 Return actual-empty zero counts and invalid-only ordered counts without admission/storage; for mixed batches preserve Invalid preflight results, report eligible/unattempted count on Busy/Unavailable, or execute unique valid targets in deterministic ISO3 order under one reservation and return truthful ordered aggregate outcomes while continuing ordinary failures.

## 3. Route Data-page lifecycle and finalized results

- [x] 3.1 Route only per-cache Delete and source-specific Delete All through the deletion command; leave block 51 Ensure/Refresh/Re-download worker behavior unchanged.
- [x] 3.2 Add source-named per-cache and Delete All confirmation before admission; disable conflicting cache controls through the existing page-owned post-operation reload, present fail-fast Busy/Unavailable and complete/idempotent/partial/failure outcomes, add no in-operation Delete Cancel action, and suppress stale/disposed circuit renders without abandoning the admitted operation.
- [x] 3.3 Finalize explicit single-target or ordered batch Deleted/Missing/Invalid/Failed results, release ownership exactly once, then return those results; preserve finalized deletion data separately from later reload failures, reload after any Completed outcome and after release when reserved (never on Busy/Unavailable), introduce no inventory cache/invalidation contract, and preserve the existing Data-page explicit reload.
- [x] 3.4 Update concise operator documentation for fail-fast busy behavior, no Delete cancellation, safe retry after finality, and the single-interactive-Web requirement for strict shared-volume exclusion.

## 4. Verify races, handles, and boundaries

- [x] 4.1 Add deterministic barrier tests proving first-wins deletion/worker admission, no check-to-delete gap, worker Busy during maintenance, no file touch/child on rejection, exact-once release, and subsequent reuse.
- [x] 4.2 Cover shutdown-before-admission, shutdown-after-maintenance-admission, navigation/disposal, stale callbacks, result-finalization/release ordering, and the page reload after completion without sleeps or early release.
- [ ] 4.3 Cover both sources for canonical/unknown ISO3, total mapping of every current bundled known identity including the GADM alias, source mismatch, duplicates, traversal/path injection, canonical containment, symlink/reparse refusal, missing files, permissions/read-only/in-use/I/O errors, and no temporary-candidate deletion; include achieved real Windows sharing/read-only and Unix permission/link faults where applicable, with explicit platform skips only for inherently inapplicable cases.
- [x] 4.4 Cover successful and idempotent per-file deletion plus empty, complete, and partial Delete All with deterministic finalized ordered outcomes and the existing page-owned post-operation status reload.
- [x] 4.5 Add composition/negative-dependency tests proving Web deletion resolves no heavy cache/resolver/export service, opens no pooled database handle, calls no global pool clear, emits no worker/protocol/exit/ProcessingState activity, leaves block 51 ownership intact, and introduces no block 53 dependency or inventory invalidation seam.
- [ ] 4.6 Add Change52 to the existing three-OS worker build-and-published CI filters without changing their jobs/categories/adapters; run focused deletion/coordinator/page/source/platform tests, `npm run test` with normal exclusions, `npm run docs:build`, `openspec validate 52-coordinate-cache-deletion-with-active-worker --strict`, final status, and a scope review proving block 51 worker semantics and block 53 artifacts/implementation were not changed.
