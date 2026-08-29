## Context

See `proposal.md` for motivation and `specs/web-control-plane-composition/spec.md` for the boundary. The checkout before the numbered migrations has one broad Web graph: global Razor imports expose Overture/GADM namespaces; `Lookup.razor` directly injects five heavy source services; `Data.razor` and `GeoBoundaries.razor` inject both cache services; `AdministrativeAreaResolverService` closes over all source resolvers/caches; and `ProcessingBackgroundService` is both scheduler and in-process executor. `Program.cs` factories capture country mapping delegates and storage roots while registering Overture/GADM services in the Web provider. `OvertureDivisionsService` lazily builds the process-lifetime STRtree/prepared-geometry country index on first lookup.

Blocks 47–53 replace these callers with typed worker sessions, shared admission, a cache-mutation controller, lightweight coordinated deletion, and a lazy storage-only inventory. Block 19 supplies registration slices and blocks 38–45 supply child-only processing and Standard/Web-only mode behavior. Apply must bind to the exact landed symbols rather than create parallel roots. Parallel-owned block 54 is a prerequisite only: verify its reset control-plane surface, but do not edit its artifacts or implementation. Block 56 owns the durable future enforcement expansion.

## Goals / Non-Goals

**Goals:**

- Make the absence of worker-only geodata from both Standard and Web-only a descriptor, constructor-graph, startup, and compiled-dependency invariant.
- Preserve every Web surface through finalized control-plane contracts while making worker process exit the reclamation boundary for country indexes, native DuckDB/geometry state, and source caches.
- Remove obsolete Web project/package/import edges and closure-captured heavy types after shared DTO and country-identity ownership is made lightweight.
- Keep startup lazy: building and starting a Web host performs no worker launch, inventory scan, geodata open, country-index load, or eager database connection.

**Non-Goals:**

- Change worker protocol, resolver precedence, cache publication/deletion/inventory semantics, arbitration, reset semantics, deployment-mode behavior, or public UI workflows owned by blocks 47–54.
- Edit parallel-owned block 54 or implement block 56's ongoing architecture policy framework.
- Remove heavy dependencies from worker or Run-once composition, combine those roles with Web, or launch a worker to validate Web startup.
- Use a fragile RSS threshold as proof; the invariant is absence of heavy construction/static edges, with worker termination providing memory reclamation.

## Decisions

### 1. Reconcile the landed composition roots with a strict ownership matrix

At apply start, enumerate every production `ServiceDescriptor`, alias, hosted-service alias, implementation factory, constructor dependency, page injection, and startup initializer after blocks 47–54. Classify each registration as common lightweight, Web control plane, worker heavy, Run-once heavy, or role-specific host infrastructure. Modify only the landed registration slices and startup dispatcher; do not assemble a test-only root that can drift from production.

The Web allowlist includes Razor/Data Protection, validated storage/configuration, UI state and mode/status projections, worker command/session clients, arbitration and maintenance controllers, block-53 cache inventory, the finalized reset control service, one lazy Npgsql data source plus repositories/work detector, skipped-assets SQLite control-plane storage, and bounded identity/profile DTO catalogs. Everything else is denied unless its transitive constructor and assembly closure is proven lightweight.

Worker and Run-once roots retain administrative resolution, Overture/GADM source services, cache ensure/refresh/export, DuckDB bootstrap, geometry readers/indexes, processing executor/handlers, and their source-specific packages. Standard and Web-only MUST NOT call the heavy registration slice merely because their launcher can create a child; the child enters the worker startup branch in a new process.

Alternative: leave heavy descriptors registered but trust pages not to resolve them. Rejected because a future constructor, factory validation, or hosted initializer can repopulate the long-lived process. Alternative: create an entirely new composition root. Rejected because it duplicates the role/mode work and weakens production fidelity.

### 2. Remove every remaining page and service closure into heavy geodata

Bind `Lookup.razor` and its page-independent controller only to the finalized CoordinateLookup control-plane client, settings/request mapper, and presentation state. Remove global Overture/GADM model/service imports and page-private resolver/cache/index construction. Bind `Data.razor` and `GeoBoundaries.razor` reads only to block-53 immutable inventory; retain refresh through the cache-mutation controller and delete through block-52's lightweight command. Post-operation adapters invalidate/reread inventory only through block 53's landed contract.

Processing controls retain coordinator/scheduler state and the child launcher. Standard's scheduled eligibility path may use only the finalized Npgsql work detector/repository before admission; Web-only registers no internal scheduler. Reset UI consumes only block 54's landed lightweight maintenance contract. Dashboard, Settings, City Resolver, logs, mode/status, and skipped-asset counts are reviewed for constructor edges even when they never called geodata directly.

Delete `AdministrativeAreaResolverService` and the in-process execution graph from Web composition. If the landed scheduler type still combines scheduling with execution dependencies, split or consume its already-planned lightweight coordinator facade rather than preserve the heavy constructor. Preserve hosted/concrete alias identity and `MarkPending`/schedule behavior from prerequisite blocks; this change does not redesign them.

Alternative: keep source status methods as read-only dependencies. Rejected because their owning cache services also close over mutation/native source infrastructure and block 53 supplies the deliberate metadata-only replacement.

### 3. Retain country-code identity only after severing the Overture assembly edge

Current `CountryCodeService` is behaviorally lightweight but constructs an Overture `CountryIdentityCatalog` from bundled `iso3166.json`. Move the identity record/catalog contract and resource ownership into Core or another package-free shared/control-plane location, or replace the wrapper with an equivalent bounded geometry-free catalog. Preserve established alpha2/alpha3 and territory aliases required by request validation, deletion, city profiles, and display; do not silently substitute framework region data if semantics differ.

The retained service must perform only bounded resource parsing/lookup and must not reference Overture/GADM models, SQLite geodata, network clients, geometry, spatial indexes, or mutation services. Constructor parsing may remain bounded, but lazy immutable publication is preferred if it avoids startup work without reintroducing synchronization hazards. Resolver-profile identity/configuration may remain under the same lightweight rule.

Alternative: remove all country identity from Web. Rejected because canonical request/deletion validation and UI mapping need it. Alternative: retain the Overture project reference for one catalog type. Rejected because it defeats static dependency removal and permits accidental heavy imports.

### 4. Make project and package removal part of this cutover, with a compile-backed fallback

After DTO/identity relocation, remove Web `ProjectReference`s to GADM and Overture and direct packages `DuckDB.NET.Data.Full`, `NetTopologySuite`, and `GeoJSON4STJ` when `dotnet` restore/build confirms no generated or transitive compile need. Remove corresponding global Razor imports and production `using` directives. Keep Core plus control-plane contract references, `Npgsql`, `Cronos`, and `Microsoft.Data.Sqlite`; SQLite is explicitly permitted for skipped-assets storage and block-53 bounded `_meta`/schema inventory reads, not geodata queries or cache mutation.

If a finalized transport DTO is still housed in a heavy project, move the DTO to Core/a dependency-light contracts assembly and update both Web and worker consumers before removing the reference. Do not solve the issue by copying DTOs or keeping a façade project that transitively references native packages. Any reference that genuinely cannot be removed is a stop condition to document with its exact symbol/path, not a reason to weaken runtime non-registration.

Alternative: defer every reference removal to block 56. Rejected because the known redundant Web packages and Overture/GADM import surface are part of the memory/dependency cutover; block 56 should enforce the clean state, not create it.

### 5. Guard factories, startup, and memory boundaries explicitly

No Web factory may capture a heavy implementation type, mapper delegate owned by a heavy assembly, bundled geodata path, DuckDB/geometry option, or callback that can reach a country-index loader. Shared factories may capture only immutable composition context and approved lightweight contracts. Provider validation must not eagerly materialize service graphs with external side effects.

The Web startup path constructs the provider, maps the Web UI, and starts only required Web hosted services. It does not proactively launch a child for readiness, resolve worker handlers, scan inventory, initialize skipped storage beyond its established control-plane lifecycle, open geodata, connect to PostgreSQL, or warm country identity/index data. Standard schedules launch only after the existing lightweight eligibility/admission rules; Web-only never schedules. Inventory remains lazy until explicit Data access.

The memory boundary is qualitative and architectural: the long-lived Web process cannot own the large country STRtree/prepared geometries, DuckDB extensions/native buffers, GADM/Overture query/export state, or cache-mutation working sets. Those objects live in worker or Run-once composition and are reclaimed when that process exits. Measure memory separately only as diagnostic evidence, not as a flaky acceptance threshold.

### 6. Use layered production-composition guards

Descriptor guards inspect the exact Standard and Web-only production registration outputs before provider build. They deny forbidden service/implementation types, aliases, hosted registrations, open generics, and implementation factories by an explicit heavy-category catalog; they also verify the allowlisted Npgsql, inventory SQLite, identity, and control-plane descriptors retain intended lifetimes and aliases.

Constructor/dependency-graph guards start from every Web component, controller, hosted service, middleware-owned application service, and control-plane registration, walking implementation and factory-declared dependencies. They report the root-to-forbidden path. Factory registrations that cannot be analyzed statically must expose minimal registration metadata or be covered by throwing sentinels; do not invoke arbitrary factories merely to classify them.

Static guards inspect the Web project file/assets and production Web source/compiled assembly references. They reject Overture, GADM, DuckDB, NetTopologySuite/geometry, GeoJSON, geodata cache/resolver/index namespaces and direct project/package dependencies, while explicitly allowing Microsoft.Data.Sqlite only in inventory/skipped metadata implementations. Shared contracts must be checked transitively so a nominally lightweight assembly cannot pull native geodata packages back into Web.

Runtime tests replace every forbidden constructor/factory and the existing pre-country-index-load observer with deterministic throwing/counting sentinels. Build and start Standard and Web-only from the production root, resolve/instantiate representative page and controller graphs, and exercise Lookup submission, Data inventory refresh, cache controls, manual processing, scheduled detector-positive/empty paths where applicable, settings/status, and the finalized reset facade with fake external boundaries. Any hidden lazy activation fails before file/network/native work.

Record worker launcher calls separately: startup and page rendering must produce zero, rejected/unavailable actions produce zero, and only an admitted explicit/scheduled operation may produce exactly one. Tests use fake sessions and minimal SQLite metadata fixtures; they launch no eager/real worker and use no live PostgreSQL or geodata downloads.

Alternative: rely only on source-text bans. Rejected because factories/reflection can hide runtime reachability. Alternative: rely only on constructor sentinels. Rejected because dead registrations and static native references can still regress the boundary.

## Risks / Trade-offs

- [Landed prerequisite names or ownership differ] → Re-read applied 19, 38–45, and 47–54 contracts at apply start; bind to exact symbols and stop instead of creating duplicate roots, DTOs, or controllers.
- [Country identity relocation changes territory aliases] → Characterize current catalog behavior first and require byte/semantic parity for all canonical and special mappings used by Lookup/cache/delete/profile flows.
- [A package is transitively retained through shared contracts] → Inspect restore assets and compiled assembly references, then split the contract assembly rather than accepting a heavy transitive edge.
- [Descriptor graph analysis misses factories] → Require registration metadata for application-owned factories and pair static inspection with throwing runtime sentinels.
- [Provider validation itself causes side effects] → Validate descriptors before materialization and use fake external boundaries for startup tests; preserve lazy inventory, database, and identity behavior.
- [Standard/Web-only behavior drifts] → Run the same control-plane contract suite over both production roots, varying only schedule policy.
- [Block 54 changes concurrently] → Treat its landed reset facade as an opaque prerequisite and edit neither its change nor implementation; report an incompatible surface as an apply blocker.

## Migration Plan

1. Verify blocks 19, 38–45, and 47–53 are applied; wait for parallel block 54 and record its finalized reset facade without editing it. Generate the post-migration component/service/descriptor/closure/reference ownership matrix.
2. Characterize country identity mappings, then relocate only the bounded identity/catalog contract and any shared worker transport DTOs out of Overture/GADM dependencies.
3. Migrate every remaining Web component/service to finalized job, inventory, deletion, reset, repository/detector, and status contracts; remove global heavy imports and source-status fallbacks.
4. Narrow Standard and Web-only registration roots, factories, aliases, and hosted services; keep worker/Run-once heavy roots intact and verify Web startup launches nothing eagerly.
5. Remove obsolete Web project/package references and prove restore/build succeeds with only approved lightweight dependencies.
6. Add descriptor, graph, assembly/static-reference guards and runtime constructor/index/native/launcher sentinels over exact production Standard and Web-only composition. Run only deterministic Web/control-plane paths—no eager worker, live geodata, or database requirement.
7. Run focused Standard/Web-only tests, the normal default-exclusion suite, strict OpenSpec validation/status, and a block-55-only diff review. Then hand the established allow/deny catalog and fixtures to block 56 for ongoing enforcement.

Rollback must revert the component migration, registration narrowing, and project-reference changes together; a partial rollback that points Web at missing heavy registrations or reintroduces local fallback is invalid. Cache files, inventory metadata, settings, and PostgreSQL schema require no migration.
