## Why

`Lookup.razor` currently performs country, administrative-area, airport, and optional live-place resolution in the Web process and can download Overture or GADM caches there. Block 47 now provides the typed v2 worker-job foundation, so coordinate Lookup needs its own isolated job while retaining the current diagnostic and final-location behavior.

## What Changes

- Implement the exact v2 job kind `CoordinateLookup` with a strongly typed, bounded request for finite latitude/longitude values, the three source choices currently exposed by Lookup, and a stable city-resolver override snapshot.
- Add a worker handler that preserves the current bundled-country, Overture/GADM administrative, airport, live-Places, fallback, cache-ensure, and source-failure behavior without reading or writing Immich asset rows.
- Return a typed final location with per-field source attribution, typed country/source/cache diagnostics and candidates, trace data, release/version metadata, and visible GADM non-commercial licensing metadata.
- Emit lookup progress, common scoped cache activity, and safe logs through block 47's v2 event contract; support cooperative cancellation and preserve already-completed cache-file side effects.
- Classify `CoordinateLookup` as an exclusive heavy geodata job for the later Web arbitration coordinator, but do not acquire the processing-only PostgreSQL advisory lock or implement admission/UI routing here.
- Add deterministic handler/parity tests, v2 protocol goldens, and real child-worker fixture coverage for validation, success/no-match, source degradation, cancellation, terminal uniqueness, and managed exits.

## Capabilities

### New Capabilities
- `coordinate-lookup-worker-job`: Typed, cancellable coordinate resolution in a temporary worker with parity diagnostics, attribution, cache behavior, and GADM license visibility.

### Modified Capabilities
- None.

## Impact

Block 47's v2 job DTO/codec/registry and worker composition; a reusable Lookup operation extracted from the current page-owned logic; Overture/GADM country, cache, division, place, infrastructure, and diagnostic seams; worker protocol goldens and process fixtures. `Lookup.razor` routing and presentation remain block 49, shared admission policy remains block 50, and standalone cache-mutation jobs remain block 51.
