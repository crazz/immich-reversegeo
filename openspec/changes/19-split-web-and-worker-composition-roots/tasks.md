## 1. Prerequisite reconciliation and registration inventory

- [ ] 1.1 Re-read the applied block 18 parser source/tests and finalized blocks 7–14 executor, reporter, scheduler, coordinator, and DI APIs; record exact role/result and alias types, and stop rather than invent missing prerequisites.
- [ ] 1.2 Build a registration matrix for every current `Program.cs` descriptor: role ownership, concrete type/contracts, singleton/hosted identity, constructor/factory inputs, configuration/environment/path inputs, initialization side effects, disposal, and current UI/executor consumers.
- [ ] 1.3 Classify registrations as shared/core, Web control-plane, reusable heavy execution/geodata, or internal-worker-only, explicitly marking heavy Web overlap as temporary until block 55.

## 2. Shared composition inputs and services

- [ ] 2.1 Add an immutable role-composition context that resolves development/production and `DATA_DIR`/`CONFIG_DIR`/bundled-data paths once after role parsing without capturing or logging database secrets.
- [ ] 2.2 Extract the shared/core registration root for logging consumers, HTTP-client infrastructure where required, storage/configuration, lightweight country/profile identity, one singleton `NpgsqlDataSource`, and shared repository/storage aliases.
- [ ] 2.3 Preserve exact Npgsql factory settings and verify all database consumers in one provider reuse the same disposable singleton while credentials remain environment-backed and absent from settings/log output.

## 3. Web control-plane composition

- [ ] 3.1 Extract the Web root with interactive Razor/Blazor, Web-only Data Protection/key storage, configuration/UI services, `ProcessingState`, and every current Lookup, Data, reset, Settings, Dashboard, and city-profile dependency.
- [ ] 3.2 Bind the finalized scheduler, coordinator, reporter/state adapter, Dashboard, scheduler-start, and host-lifecycle contracts by factory alias to their one concrete singleton owner.
- [ ] 3.3 Preserve `ProcessingBackgroundService` as one concrete singleton and register its `IHostedService` alias through that same instance; retain skipped-storage startup ordering and current Web behavior.
- [ ] 3.4 Keep the reusable executor/geodata/cache module on Web for current in-process processing and Lookup/Data needs; add an explicit code comment/test marker that removal belongs to block 55.

## 4. Internal-worker composition boundary

- [ ] 4.1 Extract reusable heavy execution registrations using the finalized singleton executor and exact seam aliases, administrative resolver, Overture/GADM services and caches, database/skipped repositories, lightweight identity/profile data, and only DI-backed protocol collaborators required by the finalized execution API (leaving dependency-free codecs unregistered).
- [ ] 4.2 Add the internal-worker root over shared/core plus heavy execution registrations without Kestrel/Web server, Razor/Blazor, antiforgery/static/endpoints, Data Protection, `ProcessingState`, scheduler, Web coordinator, or UI contracts.
- [ ] 4.3 Preserve lazy/finalized asynchronous country-index initialization, cache singleton state, mapping delegates, skipped-store async initialization ownership, and centralized DuckDB HTTP/Azure/spatial bootstrap including Linux curl transport; perform none of this work in DI factories.
- [ ] 4.4 Expose the worker root for block 20 consumption but do not build the Generic Host, read stdin, emit protocol events, run an executor loop, or map exits in this change.

## 5. Role wiring and composition verification

- [ ] 5.1 Invoke the finalized block 18 parser before any role-specific builder factory; apply shared plus Web roots only for Web, and leave the parsed internal-worker role to block 20's Generic Host handoff without falling through to `WebApplication.CreateBuilder`.
- [ ] 5.2 Add deterministic shared-path tests for development, production, and environment overrides, configuration/data separation, no secret persistence/logging, and one Npgsql data-source identity without opening live connections.
- [ ] 5.3 Add Web descriptor/resolution tests for current page dependencies and reference-equality tests for concrete/interface/hosted scheduler, coordinator, reporter adapter, executor, repository, and cache aliases required by the finalized API.
- [ ] 5.4 Add worker descriptor/resolution tests proving the executor graph is complete and forbidden Web/control-plane services are absent and not constructible.
- [ ] 5.5 Add non-materialization guards proving provider construction does not initialize the country index, DuckDB, downloads, PostgreSQL connections, or skipped SQLite; dispose every provider/host fixture.
- [ ] 5.6 Run focused MSTests, `npm run test`, `openspec validate 19-split-web-and-worker-composition-roots --strict`, final OpenSpec status, and a scope diff confirming block 18 and blocks 20/55 were not modified.
