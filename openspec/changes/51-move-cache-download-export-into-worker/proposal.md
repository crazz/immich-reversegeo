## Why

Overture and GADM cache mutation currently runs behind Web-resolved services and can retain DuckDB, GeoPackage, SQLite, and native geodata memory in the long-lived Web process. Block 47 now defines a typed v2 `CacheMutation` extension point, so block 51 must make download/export/refresh a temporary-worker capability before heavy Web registrations can be removed.

## What Changes

- Add the concrete v2 `CacheMutation` request, progress, result, validation, descriptor, and worker handler for closed `Overture`/`Gadm` sources and `Ensure`/`Refresh` operations.
- Route the Administrative Areas page's existing **Re-download** action through finalized block 50's atomic first-wins `Admitted(owner handle)` / `Busy(safe active snapshot)` / `Unavailable(safe pre-launch reason)` boundary and one cancellable `ExclusiveHeavyGeodata` worker session; do not add a new download button or move deletion into this change.
- Preserve a verified existing cache during refresh, publish only a validated replacement, clean temporary artifacts and SQLite pools on every outcome, and define safe retry/no-op/cancellation behavior.
- Keep `ProcessAssets` and `CoordinateLookup` cache ensuring inside their already-admitted workers through one shared worker-only mutation core; never launch a nested cache worker.
- Return bounded typed progress, safe logs/activities, and authoritative terminal cache metadata, including stable GADM attribution and non-commercial-use licensing information.
- Keep Web mutation code free of DuckDB, remote geodata download/export, and cache-path input; coordinate later status/inventory extraction with block 53.

## Capabilities

### New Capabilities
- `worker-cache-operations`: Typed, isolated, atomic Overture and GADM cache ensure/refresh jobs and their Web request/result behavior.

### Modified Capabilities
- None.

## Impact

The v2 worker protocol/codec and goldens from block 47, worker-only composition and cache mutation services in the Overture/GADM projects, finalized block 50's one-JobId owner handle, safe active snapshot, monotonic lifecycle, shutdown fence, and classifier/process-stream-final release contract, `GeoBoundaries.razor`, source cache tests and process fixtures, and public GADM licensing copy. Block 52 retains all per-cache and delete-all behavior; block 53 retains the read-only inventory service.
