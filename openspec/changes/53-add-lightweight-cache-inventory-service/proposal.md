## Why

The Data pages currently obtain cache status from services that also own downloads, exports, ready maps, and geodata dependencies, and their row-count query can touch the cache data itself. Before the Web host can drop those heavy registrations, it needs a deliberately storage-only inventory that remains safe under corrupt files and concurrent worker publication or deletion.

## What Changes

- Add a lazy, read-only cache inventory for the two fixed administrative-cache stores, backed only by storage configuration, filesystem facts, and narrowly bounded SQLite schema/metadata reads.
- Define immutable Web DTOs with a closed source and status model plus ISO3, size, modification time, optional downloaded time and dataset version/release, temporary-artifact indication, and safe diagnostic state. Deliberately stop deriving area counts because `COUNT(*)` is not lightweight metadata.
- Constrain inspection to canonical immediate child names under the configured source directories; reject links and unsafe paths, distinguish absent/in-progress/invalid/unreadable states, and never repair or delete files.
- Bound enumeration and metadata work, coalesce concurrent scans, cache only immutable snapshots, and use generation-based invalidation so publication/deletion races cannot republish a known-stale snapshot.
- Migrate the Data summary and GeoBoundaries table to the inventory, including status-aware display and explicit rereads, without starting a worker or resolving a cache, exporter, DuckDB, geometry, or country index.
- After finalized changes 51 and 52, add change-53-owned adapters that consume their existing explicit completion/deletion results and invalidate inventory only for authoritative successful mutation or actual `Deleted` outcomes; neither prerequisite depends on the inventory or binds to a change-53 interface.

## Capabilities

### New Capabilities
- `cache-inventory`: Bounded, read-only, Web-safe discovery and presentation of administrative-cache storage metadata.

### Modified Capabilities
- None.

## Impact

The change affects the Web Data/GeoBoundaries read models, Web composition and DI, storage-only DTOs/services, the existing source status tests that become extraction coverage, and change-53-owned adapters over the finalized change-51 completion result and change-52 explicit deletion result(s). It removes the Data UI's dependency on cache-service `GetStatus()` and its area-count display, but changes neither prerequisite and does not remove heavy registrations itself; change 55 owns that cleanup.
