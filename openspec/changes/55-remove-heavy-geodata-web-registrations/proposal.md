## Why

After blocks 47–53 move Lookup, cache mutation, deletion, and inventory behind control-plane seams, the long-lived Web process still has a composition and static-reference path to Overture/GADM resolvers, native geodata libraries, cache mutation, and the bundled country spatial index. Block 55 completes the memory boundary: Standard and Web-only must remain useful control planes while heavy work is constructible only in worker or Run-once composition.

## What Changes

- Reconcile the landed common/Web/worker registration slices and remove every heavy geodata, geometry, DuckDB, cache-mutation/export, country-index, administrative-resolution, and in-process execution descriptor or factory closure from Standard and Web-only composition.
- Migrate all remaining Web component and service constructor paths—including Lookup, Data/Administrative Areas, processing controls, startup initialization, and post-operation reloads—to the worker-job clients, shared arbitration/control services, and block-53 lightweight cache inventory.
- Retain only explicitly reviewed lightweight data dependencies in Web: configuration and identity DTOs/catalogs, a geometry-free country-code identity service if moved behind a non-geodata boundary, Npgsql data source/repositories and scheduled-work detector, skipped/inventory SQLite metadata access, UI state, and control-plane launch/status/maintenance services.
- Move shared transport/identity contracts out of Overture/GADM assemblies where required, remove Web project/package/global-namespace references to Overture, GADM, DuckDB, NetTopologySuite, and GeoJSON when the compiled boundary permits, and keep Microsoft.Data.Sqlite solely for bounded inventory/control-plane metadata stores.
- Add production-composition descriptor/assembly/static-dependency guards plus runtime constructor, factory, country-index-load, and eager-worker-launch sentinels for both Standard and Web-only.
- Preserve worker and Run-once ownership of heavy services without starting a worker, scanning inventory, opening geodata, or connecting to PostgreSQL merely because a Web host/provider starts.

## Capabilities

### New Capabilities
- `web-control-plane-composition`: Defines the post-cutover Standard/Web-only dependency and startup boundary that keeps heavy geodata memory in disposable worker or Run-once processes while preserving Web features.

### Modified Capabilities
- None.

## Impact

The landed role/mode registration roots and startup dispatcher, Web components and control services, shared transport/identity placement, `ImmichReverseGeo.Web.csproj`, global Razor imports, and Standard/Web-only composition tests are affected. Blocks 47–53 are consumed as finalized prerequisites; parallel-owned block 54 is only verified as landed and is not edited. Block 56 remains the follow-on regression-enforcement owner.
