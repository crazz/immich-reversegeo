# Web and worker compilation boundary

The executable project is `src/ImmichReverseGeo.Host/ImmichReverseGeo.Host.csproj`. It retains the `ImmichReverseGeo.Web` executable name and the existing startup dispatcher. Standard and Web-only use the existing Web composition roots; internal worker and Run-once use the existing worker roots. Moving these roots between assemblies does not add another role or composition path.

| Owner | Compilation and services |
| --- | --- |
| Host | Existing `Program.cs` role dispatch, application settings, launch profile, complete application publication |
| Web / `ImmichReverseGeo.Web.ControlPlane` | Razor components, settings, identity/profile catalogs, job clients, admission and maintenance, status, scheduler, lazy Npgsql repositories, skipped-assets SQLite and bounded cache inventory |
| Worker | Existing worker/Run-once hosts, job handlers, processing executor, coordinate lookup, administrative resolver, heavy registration slices |
| Core | Shared job contracts, `CountryIdentity`, `CountryIdentityCatalog`, `GadmCountryCodeMapper`, canonical `Countries/iso3166.json` |
| Overture / GADM | Source caches, downloads, geometry and native query dependencies |

Host references Web and Worker. Worker references Web's shared lightweight services, Core, Overture and GADM. Web references Core, Cronos, Npgsql and Microsoft.Data.Sqlite. Core has no package references. Worker consumes existing internal Web contracts through a friend assembly grant; that grant adds no Web-to-Worker assembly reference. The namespaces of relocated worker types stay unchanged to preserve consumers and log categories.

The canonical identity resource is copied to the established `bundled-data/iso3166.json` output path. Its mappings and bytes are unchanged. Country indexes and source cache implementations are not registered in either Web mode. The Npgsql data source remains a single lazy owner; inventory reads only schema and bounded metadata with pooling disabled.

## Build and publication

Publish Host, not the Web class library. Host explicitly sets `RequiresAspNetWebAssets` because its Razor components live in another project. Without that property, .NET 10 can publish HTML and application styles while omitting the Blazor bootstrap script. Web uses the root static asset path, and routing identifies the component assembly through `App`.

The test project's production apphost staging targets build Host and keep the original executable and staging-directory names. Docker and release publication also target Host. A complete artifact must contain Web.ControlPlane, Worker, the source dependencies, the identity resource and the Blazor static asset manifests.

## Default boundary enforcement

The Web test project runs these checks with the normal default test command. None requires Integration or Performance categories, a database server, geodata download, native geodata initialization, a port, or a child process:

- `WebControlPlaneCompositionTests`: exact Standard/Web-only descriptor and compiled-reference checks, introduced before the production cutover.
- `WebBoundaryInspection`: constructor/injection paths and IL metadata for application factories and callbacks, including keyed, hosted and generic registrations. It never executes factories to classify them. Abstract interface dispatch is covered by the complete descriptor inventory and compiled dependency checks; framework implementations are bounded by the restored dependency set.
- `WebControlPlaneGuardTests`: every compiled Web component, supplemental source guards, negative controls, and production apphost bootstrap/resource checks. Each run writes a separate ownership inventory under `_out/execution/56/ownership`.
- `DeploymentModeCompositionMatrixTests`: exact four-role registrations and aliases; actual Web startup with forbidden database/inventory/geodata factories; all component injection graphs; existing mode, scheduling and control-plane behavior.
- `WebProcessingGeodataBoundaryTests`: real manual/scheduled boundaries and a deliberate lazy country-index activation that fails before SQLite access.
- Existing Lookup, cache mutation/deletion/inventory and database-maintenance suites retain the actual controller behavior and fake external boundaries. Inventory tests check schema/metadata-only reads, bounded results and released SQLite handles.

The allowlist includes small country/profile catalogs, lazy Npgsql and the two explicit SQLite uses. It excludes Worker, Overture, GADM, DuckDB and NetTopologySuite from Web's compiled/package closure and from its registrations and construction paths. Host and Worker intentionally carry the heavy deployment dependencies. An HTTP Reset-page probe requires PostgreSQL; use its controlled facade tests or the explicit integration job when no database is available.

`ControlPlaneDependencyPolicy` owns exact contract categories and role ownership. `WebBoundaryInspection` follows constructors, aliases, instances, keyed registrations, generic arguments and inherited private injection properties. Factory IL is read through the shared `BoundaryIlMetadata` cache; factories are never executed to discover dependencies. The older processing-specific graph uses this same metadata reader and category catalog, retaining its stricter rule against a repository in the child-processing factory.

`control-plane-factories.json` declares the 50 application-owned factory contracts from both Web modes. Their service, owner, lifetime and compiled dependencies must match in each mode. Missing or changed declarations fail; the test writes the observed catalog to a separate local file for review. The structural walker still rejects heavy dependencies even if someone adds them to this catalog. Compiler-generated factory names are intentionally review-sensitive: changing registration shapes may require updating the exact manifest after inspecting the observed dependencies.

`ControlPlaneStaticPolicyTests` checks both Web and Core projects, the exact restored dependency set and PE metadata without loading new assemblies. Framework names come from the installed framework's own manifests. Host has five explicit heavy member edges: private invocation validation/dispatch and the Worker/Run-once bootstrap callbacks. Other heavy bootstrap edges fail. Existing startup-selection tests verify the role continuations.

`ControlPlaneRolePolicyTests` checks the same four production registration roots. Worker and Run-once must retain their registered and reachable executor/resolver/source graph, plus compiled source-to-DuckDB/index/export edges. Unused heavy registrations cannot satisfy the reachability guard. Web presentation and control ownership are forbidden in these roles; the type argument of the framework's `ILogger<T>` is only a log category, not an activation edge.

SQLite access is limited to the two reviewed owners. The inventory reader's compiled async bodies must retain its three bounded schema/metadata queries and explicitly disable pooling. Tests pair these allowances with geodata query, area-count, mutation, export, attachment and pooling negatives. Existing minimal-schema inventory tests verify results and released handles. The country identity mapping/resource checks from Change 55 remain in force.

Runtime sentinels are per fixture and record owner, category, count and order before failing. Actual Web hosts use a nonbinding server; page and controller tests keep production composition and admission while substituting external boundaries. Admitted Lookup/cache sessions finish with a controlled terminal cancellation and exact disposal; manual and eligible scheduled work delegate once. Invalid, unavailable, busy and empty paths add no session and cannot use a local heavy fallback.

## Updating the policy

The maintainer changing a boundary owns its policy update. First inspect the reported root-to-offender path. Move heavy ownership into the disposable role when the dependency is real. For a legitimate lightweight change, name the exact contract or edge, record its reason and owner, prove the complete constructor/factory and project/package/assembly closure, and add a positive case with the adjacent forbidden implementation case. Update renamed or moved heavy types and their negative cases together. Do not add blanket namespace, assembly, package or `object` suppressions; no suppression mechanism is provided.

Diagnostics carry a rule, role, root, category, offender, ordered path and remediation. They aggregate distinct offenders deterministically and retain the shortest observed path per offender. Treat `UnclassifiedFactory` as missing evidence: inspect its implementation and metadata rather than resolving it during analysis.

Use `npm run agent:test -- --filter 'TestCategory=Change56'` for the focused policy suite and `npm run agent:test` for the required full default suite. Existing Change 39/55, composition, inventory and maintenance tests remain complementary checks; the standard CI test command already discovers the complete Web assembly.

## Scheduled work detection

Standard owns one stateless `ExistenceProcessingWorkDetector` singleton wrapped by one `InstrumentedProcessingWorkDetector` singleton. `IProcessingWorkDetector` resolves to that outer instance. The coordinator calls it once before creating a run identity or attempting admission. The immutable request carries the Scheduled trigger and a current scheduled-launch/full-eligibility snapshot; bounded diagnostics describe implementation kind, coverage and fallback use. Only `HasWork` controls the existing launch branch. Worker requests and public settings carry none of this metadata.

No-work, cancellation and detection failure close the scheduler occurrence without a ProcessingState lifecycle. A positive observation still has to acquire shared admission and can lose to another job. Admitted work keeps the existing pending, arming, child dispatch and cleanup order. Detection is advisory: eligibility can disappear before the worker starts, or appear after a negative observation. There is no automatic retry or catch-up; the worker takes its own advisory lock and performs a fresh authoritative exact count. Dashboard statistics keep their separate exact count, and manual processing bypasses detection. Web-only, Run-once and private workers do not register this Web scheduled detector.

The adapter makes one lazy existence read with the exact preflight token. It retains no request, connection or result and performs no writes. The decorator records one structured completion event with monotonic duration and bounded outcome fields; it adds no processing-state or UI log entry. Exact structural guards enforce the lightweight graph, including the single decorator path.

The scheduled detector issues one full-eligibility PostgreSQL `EXISTS` read. It returns an advisory boolean and does not count or reserve assets. Dashboard statistics and the worker retain their independent exact counts. A no-match result can require scanning all relevant rows; the query does not guarantee a fixed time bound.
