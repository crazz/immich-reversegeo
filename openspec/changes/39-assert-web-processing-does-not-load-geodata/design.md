## Context

See proposal.md for motivation and specs/architecture/web-processing-geodata-boundary/spec.md for the contract. In the current source, Program.cs registers the complete Overture/GADM graph and ProcessingBackgroundService directly injects AdministrativeAreaResolverService and OverturePlacesService. AdministrativeAreaResolverService.ResolveAsync reaches the country lookup, Overture cache/divisions, GADM cache/divisions, and airport path; OvertureDivisionsService constructs its in-memory bundled-country STRtree lazily on the first FindBundledCountryAsync call.

Once blocks 19–20 and 33–38 have actually been applied and verified, bind this test to their landed shared/Web/worker roots, coordinator, scheduled detector, child adapter, empty-detector behavior, and child-only selection. Stop if those prerequisites are absent or differ rather than assuming planning APIs landed. Lookup, Data, and GeoBoundaries still require heavy Web descriptors at this point, so a whole-provider or assembly-reference ban would be false until block 55.

## Goals / Non-Goals

**Goals:**
- Prove the real production Web processing graph cannot reach worker-only execution or geodata before or after child dispatch.
- Cover accepted manual, detector-positive scheduled, and detector-empty scheduled routes with deterministic resolution, construction, method-call, and lazy-index-load evidence.
- Make failures identify the processing root and forbidden dependency edge or sentinel that was reached.

**Non-Goals:**
- No edits to block 38, no replacement coordinator/detector/launcher/protocol, and no behavior, state, result, configuration, or scheduling redesign.
- No blanket removal of heavy Web registrations, Overture/GADM project references, or Lookup/Data dependencies; those remain block 55 work.
- No real child process, worker protocol stream, PostgreSQL, SQLite, DuckDB, filesystem geodata, download/export, or geometry operation.
- No duplication of block 36's detailed zero-work lifecycle/log/counter assertions; reuse that fixture and add only the composition-boundary evidence needed here.

## Decisions

### 1. Test the finalized production registration root, not a parallel test graph

Invoke the exact role-specific Web registration method established by block 19 and the child-only production processing registration left by block 38. Tests may replace descriptors after those methods run, but they must not recreate registrations by hand. Use the project's finalized test visibility convention; if none exists, add the narrowest InternalsVisibleTo or internal fixture entry needed to call the registration root.

This catches drift in production composition. Testing top-level Program.cs would force host/filesystem startup, while a separately assembled ServiceCollection could pass even when production wiring regresses.

### 2. Add a processing-root service-graph guard instead of banning Web assemblies or descriptors

Build a reusable test helper over the production ServiceDescriptor set. Starting from the finalized manual-control/coordinator surface, scheduled-control surface, scheduled detector adapter, and child-dispatch adapter, follow concrete implementation types, implementation factories recorded by the registration fixture, constructor parameters, and aliases. Report the shortest path from a processing root to any forbidden type/category.

The forbidden set is reconciled to applied names and includes:
- the processing run executor and any production in-process executor/adapter;
- AdministrativeAreaResolverService or its finalized resolver contract;
- OvertureDivisionsService, OverturePlacesService, OvertureDivisionCacheService, and the bundled-country index loader;
- GadmDivisionsService, GadmDivisionCacheService, and any GADM exporter/resolver reachable from processing;
- any airport/infrastructure resolver separate from Overture Places.

The repository-backed detector, lightweight CountryCodeService, and CityResolverProfileCatalogService are allowed. Child launch/session/protocol collaborators are also allowed, but graph traversal does not stop merely because a child adapter is found: its constructor closure must still contain no executor or geodata dependency. A broad ImmichReverseGeo.Web to Overture/GADM assembly-reference assertion is rejected because Lookup/Data legitimately require those references until block 55.

### 3. Back structural evidence with route-specific fail-fast sentinels

Compose a fresh provider for each route and replace every forbidden production descriptor with a sentinel factory that records the service type and throws immediately on resolution. Where an applied contract permits a callable fake, also record constructor and method-call counts. Replace the child process boundary with one recording fake that accepts the exact request/token and returns a deterministic terminal result without command construction, process start, protocol I/O, or worker execution.

Exercise this matrix:

| Route | Detector/repository | Child boundary | Forbidden resolution/construction/calls |
|---|---:|---:|---:|
| accepted manual | 0 | exactly 1 | 0 |
| accepted scheduled, detector true | exactly 1 detector decision; repository access allowed by the applied adapter | exactly 1, after detector | 0 |
| accepted scheduled, detector false | exactly 1 detector decision; repository access allowed by the applied adapter | 0 | 0 |

Use a repository spy behind the real applied detector adapter so true and false cases prove the only permitted predispatch data access without opening PostgreSQL. Assert no fallback, second backend, command builder, launcher, worker session, or worker event is reached beyond the substituted child boundary. Manual routing must not call the detector. Dispose every scope/provider and assert sentinel counters after final cleanup.

### 4. Instrument the single lazy bundled-country-index transition

The current country index is private and lazy, so constructor counters alone cannot distinguish an accidentally preconstructed Overture service from a later country lookup. Add a minimal internal load observer/test hook at the single transition immediately before LoadBundledCountryIndex opens SQLite and builds prepared geometries. Production uses a no-op observer; tests install a counting observer that throws on the first load attempt, before any file access. Preserve the current lock, one-time publication, lazy first-use semantics, and public lookup behavior.

The route tests assert both zero OvertureDivisionsService resolution/construction and zero observer calls. This gives independent evidence that neither DI activation nor an already-available instance can load the index. An assertion based only on absent files, memory deltas, or missing logs is rejected as nondeterministic and unable to identify the dependency edge.

### 5. Keep block 39 transitional and define block 55's stronger successor

Block 39 protects only processing roots. It deliberately allows heavy descriptors and project references whose only consumers are Lookup/Data/GeoBoundaries. After those routes move to worker jobs, block 55 must strengthen the invariant for Standard and Web-only composition: no heavy geodata, cache-mutation/exporter, country-index loader, or processing-executor descriptor may exist or resolve anywhere in the Web host; page/control-plane constructors must use approved worker-job and lightweight inventory contracts; and direct Web project/package references should be removed when DTO boundaries permit. Block 56 can then extend source/dependency checks across every Web component and control service.

## Risks / Trade-offs

- [Factory registrations obscure implementation types] → Have the role-specific registration fixture record explicit implementation metadata for architecture tests, and pair graph inspection with runtime sentinels.
- [A forbidden service is constructed before test overrides] → Apply production registrations to an unbuilt collection, replace descriptors, then build once; assert construction counters remain zero.
- [Parallel tests make lazy-load instrumentation flaky] → Use an instance-scoped observer supplied by the test-only construction seam, never a mutable static counter.
- [Applied block names differ] → Re-read source and bind the forbidden categories and roots to exact finalized contracts; do not create duplicate coordinator, detector, or backend abstractions.
- [The test accidentally promises whole-Web isolation early] → Keep assertions rooted at processing and document block 55 as the descriptor/assembly-removal gate.

## Migration Plan

1. Confirm blocks 19–20 and 33–38 are applied and passing; inventory exact production registration roots, processing entry points, detector adapter/repository seam, child boundary, executor, and geodata types. Do not modify block 38.
2. Add the processing-root descriptor graph helper and the instance-scoped bundled-country-index load observer while preserving lazy behavior.
3. Add production-composition fixtures with descriptor overrides, forbidden sentinels, a repository spy, and a recording child-boundary fake.
4. Exercise manual, detector-positive scheduled, and detector-empty scheduled routes; reuse block 36's empty fixture/state assertions rather than duplicating them.
5. Run focused tests, npm run test with default exclusions, strict OpenSpec validation/status, and a scope review limited to block 39.

Rollback removes the tests and testability instrumentation only. It does not restore an in-process production backend or change persistent data.

## Audit Reconciliation

The test substitutes and proves the finalized child-dispatch boundary contract, not a real child process. Assertions about coordinator/detector/boundary names, registration roots, and available test seams are conditional on their landed forms after prerequisite application; bind to those exact contracts and do not claim process startup, protocol, or real worker execution occurred.

