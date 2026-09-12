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

## Change 56 enforcement inputs

These are the concrete Change 55 seams to extend; no separate policy framework is introduced here:

- `WebControlPlaneCompositionTests`: exact Standard/Web-only descriptor and compiled-reference checks, introduced before the production cutover.
- `WebBoundaryInspection`: constructor/injection paths and IL metadata for application factories and callbacks, including keyed, hosted and generic registrations. It never executes factories to classify them. Abstract interface dispatch is covered by the complete descriptor inventory and compiled dependency checks; framework implementations are bounded by the restored dependency set.
- `WebControlPlaneGuardTests`: every compiled Web component, source/project/restore/assembly guards, negative controls, and production apphost bootstrap/resource checks. The test emits the current descriptor/factory/component ownership inventory under `_out/execution/55/ownership`.
- `DeploymentModeCompositionMatrixTests`: exact four-role registrations and aliases; actual Web startup with forbidden database/inventory/geodata factories; all component injection graphs; existing mode, scheduling and control-plane behavior.
- `WebProcessingGeodataBoundaryTests`: real manual/scheduled boundaries and a deliberate lazy country-index activation that fails before SQLite access.
- Existing Lookup, cache mutation/deletion/inventory and database-maintenance suites retain the actual controller behavior and fake external boundaries. Inventory tests check schema/metadata-only reads, bounded results and released SQLite handles.

The allowlist includes small country/profile catalogs, lazy Npgsql and the two explicit SQLite uses. It excludes Worker, Overture, GADM, DuckDB and NetTopologySuite from Web's compiled/package closure and from its registrations and construction paths. Host and Worker intentionally carry the heavy deployment dependencies. An HTTP Reset-page probe requires PostgreSQL; use its controlled facade tests or the explicit integration job when no database is available.
