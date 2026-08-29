## Why

Cancellation must remain a terminal control signal before geodata work is moved to worker processes. The active pipeline currently forwards tokens but can swallow caller cancellation per asset, misclassify an unrelated `OperationCanceledException` as run cancellation, or convert cancellation and critical memory failure into ordinary diagnostics, fallbacks, cache misses, or geometry misses.

## What Changes

- Make token-bearing active Web, Overture, and GADM geodata operations observe caller cancellation before returning cached, bundled, diagnostic, fallback, null, miss, or success results, and rethrow active-token cancellation at broad catch boundaries.
- Preserve block 5's first-owner shared-cache-task contract: a non-owner cancels only its own wait, while cancellation of an owner task is ordinary source unavailability to a live waiter whose own token is not cancelled.
- Distinguish caller cancellation from unrelated `OperationCanceledException`; only the former reaches the current run's cancellation path, while the latter remains a failure.
- Prevent `OutOfMemoryException` from being normalized by active geodata geometry, lookup, release, cache probe/validation/status, metadata, resolver, or UI-helper catch boundaries.
- Preserve intended non-critical diagnostic behavior: ordinary network/database/I/O failures retain current source diagnostics and territory/release/cache fallbacks, malformed Overture candidate geometry remains a local non-match only at tolerant containment sites, malformed GADM cached geometry retains existing bounding-box fallback/ranking, and malformed source artifacts still fail loading or cache construction.
- Add deterministic controlled-throw and token-checkpoint coverage without real network timing, sleeps, native interruption, or real memory exhaustion.

## Capabilities

### New Capabilities
- `geodata/cancellation-preservation`: Caller-token cancellation, critical-memory-failure propagation, controlled diagnostic fallbacks, and malformed-data boundaries across active geodata operations.

### Modified Capabilities
- None.

## Impact

- Active runtime only: `ProcessingBackgroundService`, `AdministrativeAreaResolverService`, Lookup cache helpers, Overture and GADM lookup/cache/export/geometry paths, and their Web/Overture/GADM test projects; `tests/ImmichReverseGeo.Tests/ProcessingBackgroundServiceTests.cs` owns the processing cancellation cases; `ImmichReverseGeo.Legacy` is excluded.
- Reuses the deterministic exporter/source-operation seams and exact-value in-flight cleanup established by blocks 4–5; those dependencies remain unchanged.
- No API, configuration, storage-schema, source-ordering, or UI workflow change. Lookup and GeoBoundaries currently supply non-cancellable/default tokens; adding component-owned cancellation is outside this block.
- Cancellation is cooperative: synchronous DuckDB/SQLite, filesystem, and NetTopologySuite work already executing cannot be preempted.
