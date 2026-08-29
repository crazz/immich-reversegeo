## Why

Each active cache service can retain a faulted or cancelled per-country lazy when its task fails before the current cleanup boundary. Direct resolver callers then keep receiving the stale terminal task, preventing repaired Overture or GADM work from starting.

## What Changes

- Give each source-specific in-flight map sole ownership of its winning lazy for the complete task lifetime, including the inner readiness preflight and setup.
- Remove terminal entries on success, fault, or cancellation by exact key/value identity so stale cleanup cannot evict newer work.
- Preserve ready-cache short circuits, same-country sharing, first-owner-token semantics, direct resolver behavior, and each source's existing temporary-artifact cleanup.
- Add deterministic Overture and GADM lifecycle coverage for preflight/operation failure, repair and retry, cancellation, success, ready caches, concurrent callers, waiter cancellation, and stale removal.

## Capabilities

### New Capabilities
- `geodata/cache-download-retry-cleanup`: Retry-safe, race-safe lifecycle coordination for Overture and GADM administrative cache tasks.

### Modified Capabilities
- None.

## Impact

- `src/ImmichReverseGeo.Overture/Services/OvertureDivisionCacheService.cs` and `src/ImmichReverseGeo.Gadm/Services/GadmDivisionCacheService.cs`: task-map ownership, full-operation cleanup, exact-value removal, and narrow internal test seams.
- `src/ImmichReverseGeo.Web/Services/AdministrativeAreaResolverService.cs`: direct call paths remain behaviorally compatible and rely on service-owned cleanup.
- `tests/ImmichReverseGeo.Tests/OvertureDivisionCacheServiceTests.cs` and `tests/ImmichReverseGeo.Gadm.Tests/GadmDivisionCacheServiceTests.cs`: deterministic lifecycle and race regression coverage.
- No public API, cache schema, source precedence, network endpoint, dependency, configuration, or persisted-data migration change.
