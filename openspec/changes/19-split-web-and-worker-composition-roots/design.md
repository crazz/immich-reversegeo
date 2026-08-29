## Context

`src/ImmichReverseGeo.Web/Program.cs` currently creates a `WebApplicationBuilder` immediately and then registers every custom service inline. All custom registrations are singletons: storage options; configuration; country and city-profile catalogs; one `NpgsqlDataSource`; Overture/GADM resolvers and cache services; repositories; `ProcessingState`; and `ProcessingBackgroundService`. The scheduler is deliberately registered first as its concrete singleton and then as an `IHostedService` factory alias.

The current Web UI directly consumes both lightweight control-plane services and heavy transitional services. Lookup resolves Overture/GADM resolvers and cache mutators; Data resolves cache services and skipped storage; reset/settings/dashboard surfaces resolve the database and skipped repositories; processing still needs the executor/geodata graph in-process until later migration blocks. Therefore `heavy` does not yet mean `worker-exclusive`: block 55 removes it from Web only after Lookup/Data and processing are routed across the worker boundary.

Path behavior is part of compatibility: development defaults data and configuration to `./localdata` and bundled data to `./bundled-data`; production defaults are `/data`, `/config`, and `/app/bundled-data`; `DATA_DIR` and `CONFIG_DIR` override the mutable roots. `ConfigService` stores `settings.json` under the config root but reads database credentials only from environment variables. The Npgsql factory creates one data source consumed by all database repositories.

The current bundled-country index is held for the service lifetime and is initialized only when country lookup first needs it; finalized prerequisite source may supply an asynchronous initialization task/await point. This refactor must preserve the applied behavior and must never perform blocking country-index work in a DI factory. Overture's DuckDB HTTP/Azure/spatial setup, including Linux `azure_transport_option_type='curl'`, is centralized in `OvertureDataAccess` and is not composition-root logic.

Block 18 is a hard prerequisite. Before block 19 is applied, verify the checkout contains its applied, tested selector: exact sole `--internal-worker` selects `InternalWorker`, absence of reserved syntax defaults to `Web` while preserving ASP.NET arguments, and `RunOnce` remains only a reserved typed boundary. Do not assume any block is landed from planning text; inventory the checkout's actual blocks 7–17 contracts, concrete types, and aliases and bind only those that exist rather than recreating them. Block 20, not this change, creates the internal Generic Host and worker lifecycle.

## Goals / Non-Goals

**Goals:**
- Make shared/core, Web control-plane, and internal-worker registration boundaries explicit and independently testable.
- Select the registration path only after block 18 parsing and before creating/building the corresponding host.
- Preserve exact custom-service lifetimes, alias identity, initialization ordering, configuration secrecy, storage paths, and Npgsql reuse.
- Document the temporary heavy Web overlap without weakening the worker's no-Web boundary.

**Non-Goals:**
- Do not implement the Generic Host branch, worker stdin loop, execution lifecycle, protocol emission, cancellation loop, or exit mapping owned by blocks 20–23.
- Do not remove heavy geodata/cache/executor registrations from Web; processing isolation and final removal remain blocks 38–55.
- Do not redesign configuration, connection strings, persistence, cache synchronization, geodata resolution, country-index loading, or DuckDB bootstrap.
- Do not change the block 18 parser API or the finalized Phase 2 executor/coordinator/reporter contracts.

## Decisions

### Select a role before constructing its builder

`Program.cs` first invokes the finalized pure block 18 selector over the immutable argument sequence and typed public-role candidate; the selector itself does not read environment configuration. Invalid selection exits through block 18's bounded stderr/exit-2 path. A `Web` result may then create a `WebApplicationBuilder` with the unchanged arguments and apply shared plus Web registrations. An `InternalWorker` result is handed to block 20; block 19 exposes a builder-neutral `IServiceCollection` worker root so composition tests can validate it now without creating a Web builder or inventing the Generic Host loop. The reserved `RunOnce` value remains an unavailable non-Web boundary and initializes neither root in this block.

Alternative: create `WebApplicationBuilder` and then inspect the role. Rejected because Web defaults, Kestrel, and ASP.NET facilities would already be initialized for the internal role. Alternative: implement the Generic Host switch now. Rejected because block 20 owns that lifecycle.

### Pass one resolved composition context to all registration roots

Resolve environment name/content root and the three effective roots once after role parsing. Carry the values in one immutable composition context (exact type/name is implementation-local): configuration root, data root, bundled-data root, and environment classification. Register one `StorageOptions` singleton from that context and construct `ConfigService` from the same configuration root. Web alone derives and creates the Data Protection key directory below the configuration root.

This prevents factories from independently rereading paths and drifting between roles. It does not snapshot database secrets into the context: `ConfigService.GetDbSettings()` retains environment-backed credentials and no composition log includes a connection string or password.

Alternative: let each factory recompute environment paths. Rejected because tests cannot prove graph-wide consistency and later roots can diverge. Alternative: add the config root to `StorageOptions` in this block. Allowed only if prerequisite APIs already require it; otherwise unnecessary model churn.

### Keep shared/core small and non-Web

The shared/core root contains dependencies genuinely required by both graphs: logging availability/typed loggers, HTTP-client infrastructure if retained by applied consumers, the immutable path options, `ConfigService`, lightweight country identity and city-profile catalogs, one `NpgsqlDataSource` singleton, and repository/storage aliases needed in both roles. The data source is registered once per host and repositories/executor seams factory-alias the same objects rather than constructing copies.

Logging configuration remains owned by the chosen host; registration helpers consume `ILogger<T>` and must not replace providers or route worker output. Block 21 later owns worker stdout/stderr policy.

Alternative: put all non-Razor services in common. Rejected because that makes the worker inherit scheduler/UI state and makes boundary tests meaningless. Alternative: put database/config in worker only. Rejected because current Settings, Dashboard, reset, Data, and configuration UI still use them.

### Separate the Web control plane from reusable heavy execution registration

The Web root owns Razor interactive server components, Web-only Data Protection, UI/state services, the finalized reporter-to-state adapter, scheduler, Dashboard/manual control contracts, and coordinator/host-lifecycle aliases. It preserves the exact Phase 2 registration topology, especially:
- one concrete `ProcessingBackgroundService` singleton factory-aliased into `IHostedService`;
- one concrete coordinator singleton factory-aliased to every finalized Dashboard, scheduler-start, reporter/control-plane, and hosted-lifecycle contract;
- one identity for every stateful reporter adapter and existing singleton collaborator.

A private/reusable execution-and-geodata registration module may be invoked by both Web and worker roots during the transition. It is not classified as common: the worker root may later evolve independently, and block 55 must be able to remove the Web invocation cleanly. It registers the finalized singleton executor and its exact production seam aliases, administrative resolver, Overture Places/divisions/cache services, GADM divisions/cache services, and any DI-backed protocol collaborators that the finalized execution API actually requires; dependency-free block 15/17 codecs are not registered merely to satisfy a category list. Web continues to invoke it for in-process processing and current Lookup/Data pages. Internal worker invokes it for execution.

Alternative: have Web call `AddWorkerServices` directly. Rejected because later worker-host/protocol registrations must not leak into Web. Alternative: remove heavy Web services now. Rejected because it breaks existing pages and advances block 55.

### Preserve factories, initialization, and disposal semantics

Copy existing factory behavior rather than replacing it with type activation where constructor choice or delegates matter. In particular, preserve mapping delegates for Overture services, `StorageOptions`-derived paths, one disposable Npgsql singleton, cache-service singleton state, and singleton executor/reporter/repository aliases. Provider/host disposal remains responsible for Npgsql and other disposable singletons.

No registration factory calls country lookup, waits on a country-index task synchronously, initializes skipped SQLite storage, opens a database connection, or boots DuckDB extensions. Preserve the finalized asynchronous country-index creation/await point if prerequisites introduced one; against the checkout truth at application time, preserve the existing lazy first-use construction rather than making it eager. Keep skipped-store initialization at the finalized hosted/execution lifecycle point. Keep every DuckDB extension call inside `OvertureDataAccess` so Linux curl transport cannot be bypassed.

Alternative: eagerly resolve the graph at startup as a validation step. Rejected because it can load the large country index, touch files/network/native extensions, or block Blazor startup.

### Test descriptors, resolution, identity, and negative boundaries

Add focused composition fixtures that invoke roots against a fresh service collection with a deterministic composition context and safe test doubles/overrides for external resources. Tests cover four layers:
1. descriptor presence/absence for shared, Web, and worker services;
2. provider validation and selected safe resolution of the executor and UI/control-plane surfaces;
3. reference equality for concrete/interface/hosted aliases, Npgsql data-source reuse, caches, repositories, coordinator, reporter adapter, and executor seams where applicable;
4. negative guards proving worker composition has no ASP.NET server, Razor/Blazor, Data Protection, scheduler, coordinator, or `ProcessingState` descriptors.

Use factories/counters or non-materializing fakes to prove provider construction does not start the country index, DuckDB, downloads, PostgreSQL connections, or skipped-store initialization. Dispose every provider/host fixture. Role-order coverage asserts the block 18 parser is invoked before any builder factory. Do not bind a port or run stdin/stdout; those are block 20+ integration concerns.

Alternative: only assert `GetService` returns null for forbidden types. Rejected because open-generic/framework registrations and accidental descriptors are easier to diagnose at descriptor level. Alternative: launch the executable. Rejected because it crosses into worker-host lifecycle and process-fixture blocks.

## Risks / Trade-offs

- [The applied block 18 parser has different role/result names] → Re-read its source and tests immediately before implementation and use that exact API; do not create a second parser or edit block 18.
- [Finalized Phase 2 types or aliases differ from planning names] → Inventory applied constructors, interfaces, and DI tests and preserve exact identities; stop rather than invent duplicate seams.
- [Heavy services are mistaken for shared/core because Web still needs them] → Keep them in a reusable heavy module called by both role roots and mark Web use explicitly temporary until block 55.
- [Provider validation constructs expensive services] → Separate descriptor tests from safe resolution tests and use overrides/counters; never invoke country lookup or live external work.
- [A refactor duplicates singleton state behind an interface or hosted alias] → Register concrete singletons once and factory-alias all contracts; assert reference equality.
- [Path extraction changes secret or volume behavior] → Test production/development/override roots and verify database credentials remain environment-only and absent from settings/logs.
- [DuckDB Linux reliability regresses] → Leave extension bootstrap centralized and add a structural assertion/review that composition does not inline or bypass it.

## Migration Plan

1. Apply and re-read block 18's exact parser API and the finalized blocks 7–14 source registrations/tests. Stop if the executor/coordinator prerequisites are absent.
2. Characterize the current `Program.cs` graph in a registration matrix: service/contract, concrete owner, lifetime, aliases, constructor/factory inputs, path/config/environment inputs, initialization side effects, and Web UI consumers.
3. Add the immutable composition context and shared/core registration root without changing runtime selection; add path, secret-boundary, Npgsql, and common identity tests.
4. Extract the Web root, preserving Razor/Data Protection, all current page dependencies, scheduler/coordinator/state registrations, initialization ordering, and concrete/hosted alias identities; run Web composition tests.
5. Add the internal-worker registration root and reusable heavy execution module. Validate executor/geodata/cache/database/protocol resolution and explicit absence of Web/control-plane descriptors. Do not create or run a worker host.
6. Wire the parsed Web path to shared plus Web roots. Leave the parsed internal-worker host handoff for block 20 if no host branch exists yet.
7. Run focused composition tests, the normal default-exclusion suite, strict OpenSpec validation, final status, and a scope diff limited to block 19.

Rollback restores the inline Web registrations. No data, settings, database, cache, or protocol migration is required.
