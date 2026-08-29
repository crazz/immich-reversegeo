## 1. Bind prerequisites and shared ownership

- [ ] 1.1 Re-read the applied block 50 coordinator/resource-owner/admission/shutdown and block 51 worker finality/storage contracts; stop rather than create parallel gates, identities, or lifecycle owners, and do not consume block 53.
- [ ] 1.2 Extend the existing `ExclusiveHeavyGeodata` owner boundary with a closed non-worker Cache maintenance reservation and Reserved/Busy/Unavailable results while preserving all worker JobId/kind invariants.
- [ ] 1.3 Implement exact-once maintenance-handle release, worker-versus-maintenance safe busy projection, and shutdown tracking without queueing, waiting, retries, cancellation, process launch, or worker exits.

## 2. Implement confined lightweight deletion

- [ ] 2.1 Add a page-independent typed deletion command for one Overture/GADM ISO3 target and source-specific Delete All, with validation before admission.
- [ ] 2.2 Validate exact uppercase known ASCII ISO3 and source mapping, derive only the configured final cache path, prove canonical containment, and reject caller paths, traversal, linked/reparse roots or targets, and mismatched identities.
- [ ] 2.3 Replace silent void deletion in the Web path with Deleted/Missing/Invalid/Failed results, bounded safe permission/in-use/I/O errors, and idempotent missing-file semantics.
- [ ] 2.4 Delete final `.db` files only; remove broad temporary-candidate cleanup from the user deletion path and use no database open, global SQLite pool clearing, geodata initialization, or `Task.Run` fallback.
- [ ] 2.5 Execute Delete All in deterministic ISO3 order under one reservation, continue after ordinary per-target failures, and return ordered results plus truthful aggregate counts including an empty no-op.

## 3. Route Data-page lifecycle and finalized results

- [ ] 3.1 Route only per-cache Delete and source-specific Delete All through the deletion command; leave block 51 Ensure/Refresh/Re-download worker behavior unchanged.
- [ ] 3.2 Disable conflicting cache controls through the existing page-owned post-operation reload, present fail-fast Busy/Unavailable and complete/idempotent/partial/failure outcomes, add no Delete Cancel action, and suppress stale/disposed circuit renders without abandoning the admitted operation.
- [ ] 3.3 Finalize explicit single-target or ordered batch Deleted/Missing/Invalid/Failed results, release ownership exactly once, then return those results; introduce no inventory cache/invalidation contract, and preserve the existing Data-page explicit reload after completion.
- [ ] 3.4 Update concise operator documentation for fail-fast busy behavior, no Delete cancellation, safe retry after finality, and the single-interactive-Web requirement for strict shared-volume exclusion.

## 4. Verify races, handles, and boundaries

- [ ] 4.1 Add deterministic barrier tests proving first-wins deletion/worker admission, no check-to-delete gap, worker Busy during maintenance, no file touch/child on rejection, exact-once release, and subsequent reuse.
- [ ] 4.2 Cover shutdown-before-admission, shutdown-after-maintenance-admission, navigation/disposal, stale callbacks, result-finalization/release ordering, and the page reload after completion without sleeps or early release.
- [ ] 4.3 Cover both sources for canonical/unknown/unmappable ISO3, source mismatch, traversal/path injection, canonical containment, symlink/reparse refusal, missing files, permissions/read-only/in-use/I/O errors, and no temporary-candidate deletion.
- [ ] 4.4 Cover successful and idempotent per-file deletion plus empty, complete, and partial Delete All with deterministic finalized ordered outcomes and the existing page-owned post-operation status reload.
- [ ] 4.5 Add composition/negative-dependency tests proving Web deletion resolves no heavy cache/resolver/export service, opens no pooled database handle, calls no global pool clear, emits no worker/protocol/exit/ProcessingState activity, leaves block 51 ownership intact, and introduces no block 53 dependency or inventory invalidation seam.
- [ ] 4.6 Run focused deletion/coordinator/page tests, `npm run test` with normal exclusions, `npm run docs:build`, `openspec validate 52-coordinate-cache-deletion-with-active-worker --strict`, final status, and a block-52-only diff/scope review proving block 51 and 53 artifacts/implementation were not changed.
