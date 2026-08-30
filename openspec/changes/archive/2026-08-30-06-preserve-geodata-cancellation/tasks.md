## 1. Dependency baseline and deterministic seams

- [x] 1.1 Confirm blocks 4–5 are applied and retain their Overture exporter/source-operation seam, GADM source-operation seam, first-owner token behavior, and exact-value in-flight task cleanup without changing blocks 1–5.
- [x] 1.2 Add failing controlled-throw/checkpoint tests first, reusing those seams and adding only narrow internal geometry/lookup and post-export/pre-publication delegates or gates; keep public constructors and production DI unchanged.
- [x] 1.3 Use `TaskCompletionSource` gates with asynchronous continuations and controlled OCE/OOM/ordinary exceptions; do not use live downloads, sleeps, native timing, memory pressure, or real OOM.

## 2. Web processing and resolver boundaries

- [x] 2.1 Create or extend `tests/ImmichReverseGeo.Tests/ProcessingBackgroundServiceTests.cs` and add coverage proving active run-token cancellation from per-asset geodata work reaches the existing `Run cancelled.` path and terminal cleanup, with no ordinary/per-asset error, skipped record, or location write.
- [x] 2.2 Use `ProcessingBackgroundServiceTests.cs` for the paired unrelated-OCE case proving an unrequested run token is classified as failure rather than run cancellation, then narrow the per-asset and run-level catches accordingly without introducing block 7 result models.
- [x] 2.3 In `AdministrativeAreaResolverService`, test and implement active-token OCE and OOM propagation while preserving ordinary GADM territory/cache unavailability and next-territory fallback.
- [x] 2.4 In active Lookup cache helpers, add OOM propagation and ordinary cache-unavailable regression coverage; keep current default/non-cancellable Lookup and GeoBoundaries token flow unchanged.

## 3. Overture propagation and malformed-data boundaries

- [x] 3.1 Cover pre-cancelled token observation for bundled country/infrastructure, cached division, warm release, and other successful/diagnostic returns before adding entry/return checkpoints.
- [x] 3.2 At place, infrastructure, and division lookup catches, prove active-token OCE and controlled OOM escape while an ordinary source/query exception still returns its existing diagnostic or null behavior, then narrow/order catches.
- [x] 3.3 At documented-release, cache status/readiness/validation/deletion, and source-metadata catches, prove OOM is not converted to fallback/false/zero/empty while ordinary discovery, corruption, and cleanup behavior remains unchanged.
- [x] 3.4 Narrow tolerant candidate containment to demonstrated malformed WKB/topology exceptions; prove cached-division and bundled-infrastructure malformed candidates yield geometry false, controlled OOM escapes, and malformed bundled-country artifacts still fail index loading.
- [x] 3.5 Add cooperative checkpoints before/after synchronous DuckDB/SQLite/filesystem/geometry regions, between practical managed rows/candidates, and before cache publication; prove cancellation after export cleans the temporary file and does not move a new cache into place.

## 4. GADM propagation and malformed-data boundaries

- [x] 4.1 Cover pre-cancelled public lookup/cache returns and thread/check the token at entry, around synchronous SQLite/geometry work, between managed candidates/layers/rows—including the GADM export loops—and before publication.
- [x] 4.2 At single- and multi-cache lookup catches, prove active-token OCE and controlled OOM escape while ordinary SQLite/schema/source faults retain existing diagnostics and candidate/fallback behavior, then narrow/order catches.
- [x] 4.3 At cache status/readiness/validation/deletion boundaries, prove OOM escapes without changing ordinary false/zero/cleanup behavior; preserve the existing download/export cleanup and bare rethrow.
- [x] 4.4 Prove malformed cached-candidate WKB sets geometry containment false while preserving bounding-box fallback and ranking, and controlled OOM escapes the same geometry boundary.
- [x] 4.5 Prove malformed source GeoPackage header/schema/WKB fails export, removes temporary/download artifacts, and leaves an existing published cache untouched; add a post-export/pre-publication cancellation gate with the same no-publication guarantee.

## 5. Shared-task taxonomy and verification

- [x] 5.1 Using block 5's lifecycle seams, cover all three cache cases for Overture and GADM: owner-token cancellation releases exact ownership and is retryable; non-owner waiter cancellation affects only its wait; owner-task cancellation is ordinary source unavailability to a live waiter whose token is not cancelled.
- [x] 5.2 Run `dotnet test --project tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj --filter "FullyQualifiedName~ProcessingBackgroundServiceTests|FullyQualifiedName~AdministrativeAreaResolverTerritoryTests|FullyQualifiedName~OvertureDivisionCacheServiceTests"`.
- [x] 5.3 Run `dotnet test --project tests/ImmichReverseGeo.Overture.Tests/ImmichReverseGeo.Overture.Tests.csproj --filter "FullyQualifiedName~Overture&TestCategory!=Integration&TestCategory!=Performance"` and `dotnet test --project tests/ImmichReverseGeo.Gadm.Tests/ImmichReverseGeo.Gadm.Tests.csproj --filter "FullyQualifiedName~Gadm&TestCategory!=Integration&TestCategory!=Performance"`.
- [x] 5.4 Run `npm run test` with default Integration/Performance exclusions and require it to pass after the final implementation edit. Because this change touches integration-covered Overture/GADM plumbing but does not change live query semantics, also execute bounded source-specific live verification and record every positive assertion honestly. A live failure may be dispositioned outside change-06 completion only when bounded evidence proves it is pre-existing and unrelated to cancellation; do not relabel it as passing, weaken the assertion, or treat zero candidates as inconclusive.
