## Why

Immich ReverseGeo currently mutates Immich location columns and its skipped-asset database directly from two Data-page flows, so those resets can race locally launched processing and other admitted maintenance. The change must coordinate the exact existing reset scope without misclassifying cache maintenance, adding an Immich schema change, or pulling database-only work into a geodata worker.

## What Changes

- Route the three existing Reset Immich Geo Data mutations and the Data-page **Clear Skip List** mutation through one page-independent, lightweight Web maintenance command that contends for the block-50 exclusive resource.
- Preserve the exact existing reset semantics: only Immich `asset_exif.city`, `asset_exif.state`, and `asset_exif.country` are cleared; Immich rows/tables and Overture/GADM databases are not reset or migrated.
- Preserve the existing confirmation boundary: **Reset All Data...** keeps its explicit confirmation, while **Reset Selected Items**, **Reset Matching City/State/Country**, and **Clear Skip List** remain immediate validated actions.
- Define fail-fast Busy/Unavailable behavior, no queue or automatic retry, non-cancellable admitted maintenance ownership, shutdown fencing, truthful PostgreSQL/SQLite partial outcomes, and post-result UI reloads.
- Keep reads lightweight in Web: location-option queries, skipped count, cache inventory, and Settings **Test Connection** do not reserve the resource. Keep cache **Re-download** as block 51's typed `CacheMutation` worker job and cache **Delete**/**Delete All** as block 52's lightweight reserved maintenance; block 54 does not reimplement them.
- State that block-50 admission is process-local. This change adds no distributed lock and does not broaden the processing-only PostgreSQL advisory lock, so strict coordination across containers requires one interactive Web control plane.

## Capabilities

### New Capabilities
- `database-maintenance-coordination`: Safe admission, execution, outcome reporting, and reload behavior for the existing Immich location and skipped-asset reset operations.

### Modified Capabilities
- None.

## Impact

Planning affects the Reset Immich Geo Data page, the Data-page Clear Skip List flow, a new lightweight maintenance orchestration seam over existing Immich and skipped-asset repositories, block-50 maintenance-owner compatibility, UI/component tests, repository/integration tests, and operator documentation of process-local limits. It does not change Settings reset behavior (none exists), cache maintenance ownership from blocks 51–53, worker protocol/job kinds, Immich schema, block 55, or public database credentials.
