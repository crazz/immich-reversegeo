## Why

The Lookup page still resolves coordinates and ensures geodata caches inside the Blazor Server process, so a diagnostic lookup can load heavy native/geospatial state into the Web control plane and cannot be cancelled safely. Block 48 supplies the typed v2 `CoordinateLookup` worker job; this change routes the existing page through that contract without losing validation, progress, result, or diagnostic usability.

## What Changes

- Replace page-owned geodata/cache resolution with a Web control-plane lookup client that launches one typed v2 `CoordinateLookup` worker job and never falls back to in-process resolution.
- Keep coordinate parsing and finite/range validation responsive in the circuit before admission or worker launch; snapshot block 48's exact three source choices and typed city-profile overrides; then map its closed discrete progress, common activity/log, completed transport result/diagnostics, safe failure, cancellation, and busy outcomes into explicit page states without invented percentages.
- Correlate every callback and terminal outcome to one page operation and worker `jobId`; cancel and dispose the owned session on user cancellation, supersession, navigation, or circuit disposal, and ignore stale callbacks.
- Introduce a narrow admission seam that can use a temporary lookup-only gate in this block and be replaced by block 50's shared coordinator without changing the page or lookup client contract. Contention remains fail-fast and never queues or starts a worker.
- Support Lookup in Standard and Web-only Web hosts, with no Web-side geodata/cache dependency, no `ProcessingState` projection, and no asset-database writes.
- Preserve current result/diagnostic presentation and make GADM's non-commercial restriction and source-specific failures visible.
- Add controller/state tests and real-worker/composition fixtures, then update Lookup, data-source, and troubleshooting guidance.

## Capabilities

### New Capabilities
- `web-lookup-worker-routing`: WebUI coordinate validation, isolated worker orchestration, lifecycle presentation, cancellation, correlation, and deployment-mode behavior.

### Modified Capabilities
- None.

## Impact

The change affects `Lookup.razor`, a new lightweight Web lookup orchestration/state seam, v2 worker launcher/session consumption, Web DI composition for Standard and Web-only modes, shared test fixtures, and `docs/website/using-the-app.md`, `docs/website/troubleshooting.md`, and `docs/website/data-sources.md`. It depends on blocks 47 and 48, binds at apply start to finalized block 48's exact request fields, city-profile override graph, closed progress discriminator, transport-owned source/result/attribution/license DTOs, cancellation semantics, and exclusive-heavy-geodata descriptor, deliberately leaves those semantics untouched, provides the handoff seam for block 50 arbitration, and must land before block 55 removes heavy Web registrations.
