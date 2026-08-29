## Why

The Administrative Areas page can delete cache files while an admitted worker is reading or atomically replacing them, and a read-only “is active” check would leave a check-to-delete race. Deletion must take the same process-local exclusive resource as heavy geodata jobs without moving lightweight file removal into a child process.

## What Changes

- Add an atomic, fail-fast lightweight cache-maintenance reservation on block 50's existing `ExclusiveHeavyGeodata` ownership boundary; hold it for the complete per-cache or source-specific Delete All operation.
- Keep deletion in Web with no worker launch, protocol request, worker exit, queue, wait, retry, preemption, or user cancellation path.
- Accept only a closed source and exact known uppercase ISO3 identity, derive the final cache path from configured storage, and reject paths, traversal, symlinks/reparse points, unknown codes, and source-unmappable codes.
- Replace silent deletion helpers with typed per-target outcomes, idempotent missing-file behavior, safe permission/I/O failures, and truthful partial Delete All summaries.
- Return finalized deletion results to the existing Data page, which retains its current explicit post-operation status reload; introduce no inventory cache or invalidation contract.
- Define handle/pool closure, shutdown/disposal behavior, process-local and multiple-container limits, accessible busy/error UX, and deterministic admission/filesystem race coverage.

## Capabilities

### New Capabilities
- `cache-deletion-coordination`: Safe, storage-confined cache deletion coordinated with exclusive heavy geodata ownership.

### Modified Capabilities
- None.

## Impact

The shared Web worker-job resource coordinator, a new lightweight page-independent cache-deletion command/storage seam, Administrative Areas deletion controls, source cache deletion characterization, the existing Data-page explicit reload/presentation path, operator documentation, and deterministic control-plane/filesystem tests are affected. Block 51's worker Ensure/Refresh contract remains unchanged; block 53 later observes/adapts the finalized deletion results for its own inventory snapshot and is not a block 52 dependency.
