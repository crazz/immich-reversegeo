## 1. Reconcile landed prerequisites and ownership

- [ ] 1.1 Verify applied blocks 19, 38–45, and 47–53 and bind to their exact role roots, mode snapshot, scheduler/coordinator aliases, worker-job clients, arbitration, cache controllers, deletion command, and inventory result/invalidation contracts; stop rather than create substitutes when landed semantics differ.
- [ ] 1.2 Wait for parallel-owned block 54, record only its finalized reset control-plane facade and registration needs, and make no edit to block 54 artifacts or implementation.
- [ ] 1.3 Build a post-migration matrix for every production Standard/Web-only descriptor, alias, hosted service, factory closure, constructor dependency, startup initializer, disposal owner, and project/package/assembly reference, classifying each as approved common/Web control plane or forbidden worker/Run-once heavy.
- [ ] 1.4 Re-audit all Web components (`App`, routes/imports/layout/navigation/reconnect and every page) plus every Web service after the prerequisite migrations; record each injection/consumer and prove no Lookup/Data/processing/reset/status/settings/log/error path still reaches a source resolver, cache mutator, geometry/index service, or in-process executor.

## 2. Establish lightweight shared contracts

- [ ] 2.1 Characterize current country identity behavior, including alpha2/alpha3 mappings, territories/aliases, unknowns, ordering, and resource-loading bounds used by Lookup, cache validation/deletion, city profiles, and display.
- [ ] 2.2 Move the country identity records/catalog/resource behind Core or another package-free geometry-free boundary, preserve characterized behavior, and keep Web `CountryCodeService` only if its complete constructor/assembly closure performs no Overture/GADM, SQLite geodata, network, geometry, cache, or index work.
- [ ] 2.3 Move any Web-consumed worker request/result/event or display DTO still housed in Overture/GADM to the finalized dependency-light transport/contracts boundary; update Web and worker consumers without duplicating DTOs.
- [ ] 2.4 Define and test the Web data-dependency allowlist: validated configuration/storage, bounded identity/profile catalogs, one lazy Npgsql data source plus repositories/work detector, skipped/inventory `Microsoft.Data.Sqlite` metadata access, UI state, and worker/control-plane clients only.

## 3. Migrate remaining Web consumers

- [ ] 3.1 Verify `Lookup.razor` and its controller consume only finalized CoordinateLookup request/settings/presentation and admission/session contracts; remove every direct Overture/GADM country/division/cache/airport/Places injection, local ensure/resolution path, heavy result model, and in-process fallback.
- [ ] 3.2 Verify `Data.razor` and `GeoBoundaries.razor` read only block-53 immutable inventory, route refresh through the cache-mutation controller and deletion through block 52's lightweight command, and invalidate/reread only through block-53 adapters after finalized outcomes.
- [ ] 3.3 Migrate processing UI/scheduling to the landed lightweight coordinator/launcher/state contracts; retain only the Npgsql work detector/repository before scheduled admission and remove any constructor path to `AdministrativeAreaResolverService`, source services, or the processing executor.
- [ ] 3.4 Bind reset UI only to block 54's finalized facade and review Dashboard, Settings, City Resolver, Logs, navigation/layout, error routes, skipped-asset views, and mode/status services for indirect heavy constructor or namespace edges.
- [ ] 3.5 Remove Overture/GADM global Razor imports, production `using` directives, stale page-private geodata helpers/models, source-service status fallbacks, and factory mapping delegates owned by heavy assemblies.

## 4. Narrow production composition and startup

- [ ] 4.1 Modify the exact landed registration slices so Standard and Web-only apply common lightweight plus Web-control-plane registrations only, while internal-worker and Run-once retain the complete heavy executor/geodata/cache/DuckDB/geometry graph.
- [ ] 4.2 Remove Standard/Web-only descriptors and aliases for `AdministrativeAreaResolverService`, Overture/GADM resolvers and source clients, division cache mutation/export services, DuckDB/geometry/index loaders, worker handlers, and the in-process processing executor; ensure no application-owned factory closes over any forbidden type, delegate, path, or option.
- [ ] 4.3 Preserve exact scheduler/concrete/hosted and coordinator alias identity from prerequisites, Standard's detector-before-admission and `MarkPending` timing, Web-only's no-scheduler policy, manual control behavior, launcher shutdown ownership, and finalized block-54 maintenance behavior.
- [ ] 4.4 Prove provider construction and Web host startup launch no child, perform no inventory scan, open no geodata or SQLite inventory file, build no country index, initialize no DuckDB/geometry infrastructure, and make no eager PostgreSQL connection; inventory and worker activity begin only on their finalized triggers.
- [ ] 4.5 Verify worker and Run-once roots still resolve heavy handlers/executor with one intended data-source owner and no dependency on Razor, Web inventory, or Web controllers; do not run a worker as part of Web startup verification.

## 5. Remove static project and package edges

- [ ] 5.1 Remove Web project references to `ImmichReverseGeo.Overture` and `ImmichReverseGeo.Gadm` after identity/transport relocation and confirm no Web component, service, generated Razor source, or factory signature requires their types.
- [ ] 5.2 Remove direct Web package references to `DuckDB.NET.Data.Full`, `NetTopologySuite`, and `GeoJSON4STJ`; inspect restore assets/compiled references for heavy transitive re-entry and keep `Microsoft.Data.Sqlite` only for block-53 inventory and skipped/control-plane metadata stores.
- [ ] 5.3 Build/restore the Web project with approved Core/control-plane, `Npgsql`, `Cronos`, and SQLite dependencies; if a heavy reference cannot be removed, document the exact blocking symbol/path and stop rather than retain a façade with native transitive dependencies.

## 6. Add structural boundary guards

- [ ] 6.1 Add a production-registration descriptor guard for both Standard and Web-only that rejects forbidden service/implementation/alias/hosted/open-generic registrations and verifies approved control-plane descriptors, lifetimes, and alias identities.
- [ ] 6.2 Add a constructor/dependency graph walk rooted at every Web component, controller, hosted service, and application service; report an actionable root-to-forbidden path and require explicit metadata for application-owned factories that cannot be analyzed directly.
- [ ] 6.3 Add Web source/project/restore-assets/compiled-assembly guards rejecting Overture, GADM, DuckDB, NetTopologySuite/geometry, GeoJSON, resolver/cache/index namespaces, and heavy transitive contract assemblies while allowing bounded inventory/skipped SQLite only.
- [ ] 6.4 Add negative self-tests proving each descriptor, graph, factory-metadata, and static-reference guard fails when an intentional forbidden edge is introduced and identifies its category/path.

## 7. Add runtime sentinels and Web-mode verification

- [ ] 7.1 Install deterministic throwing/counting sentinels for every forbidden constructor/factory category plus the existing pre-country-index-load hook and native DuckDB/geometry initialization seams; build/start the exact Standard and Web-only production roots with fake external boundaries.
- [ ] 7.2 Resolve or instantiate representative constructor graphs for every Web page/control service and exercise Lookup submission, Data inventory refresh, cache refresh/delete controls, manual processing, Standard detector-empty/positive scheduling, settings/status/log paths, and the finalized reset facade without live geodata or PostgreSQL.
- [ ] 7.3 Assert startup/page rendering/rejected or unavailable actions launch zero workers, and only an explicitly admitted Lookup/cache/manual/scheduled action records exactly one fake worker session with no local fallback; do not eagerly launch a real worker.
- [ ] 7.4 Use minimal temporary SQLite schema/`_meta` fixtures to prove inventory remains metadata-only, bounded, `Pooling=false`, and handle-free; assert no `division_area`/`gadm_area` count/content or geometry read occurs.
- [ ] 7.5 Verify Standard and Web-only feature parity for Lookup, Data/actions, resets, processing controls, settings, and mode/status while varying only internal scheduling policy; verify Web hosts retain no heavy managed/native lifetime after fake sessions finalize.

## 8. Validate and hand off enforcement

- [ ] 8.1 Run focused Standard/Web-only composition, component/controller, inventory, processing-boundary, and static-dependency tests plus the normal default-exclusion Web test suite; record any unavailable environment-gated test rather than using live downloads, integration databases, or eager worker processes.
- [ ] 8.2 Run `openspec validate 55-remove-heavy-geodata-web-registrations --strict`, inspect final `openspec status --change 55-remove-heavy-geodata-web-registrations`, and review the diff to confirm only MASTERPLAN block 55 and this change's existing artifacts were edited and block 54 remained untouched.
- [ ] 8.3 Hand block 56 the finalized allow/deny catalog, exact production-composition seam, dependency-path reporter, static-reference checks, and constructor/index/native/launcher sentinels as enforcement inputs without implementing block 56 here.
