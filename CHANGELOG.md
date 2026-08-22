# Changelog

Technical release notes for Immich ReverseGeo live here.

For a shorter user-facing summary, see [docs/website/changelog.md](./docs/website/changelog.md).

## 2026-08-23

- Fixed repeated `System.OutOfMemoryException` container kills during geocoding. Bundled country containment now resolves in two passes: `PreparedGeometry.Covers` first, with the boundary tolerance applied only when nothing contains the point. Previously every candidate that failed `Covers` fell through to `Geometry.Distance`, and because country bounding boxes overstate their footprint (antimeridian crossings, distant overseas territories), most lookups ran a full brute-force `DistanceOp` against several complete country boundaries.
- Replaced the `Geometry.Distance` boundary tolerance with a small rectangle intersection in the bundled country lookup, `OvertureDataAccess.TryGeometryContains`, and `GadmDivisionsService`. NTS `DistanceOp` computes an exact minimum, so it cannot short-circuit at the tolerance, and it copies every boundary ring into a fresh `Coordinate[]` per call — large arrays landing on the non-compacted large object heap. Rectangle intersection reuses the prepared geometry index instead. Measured against the bundled dataset, a Berlin lookup drops from 2.71 ms and 2,596 KB allocated to 0.02 ms and 2.2 KB.
- Switched the web app to Workstation GC and added `mem_limit` to both compose files. Server GC allocates a heap per core and collects lazily, and with no container limit the runtime sized heaps against total host memory. `2g` is chosen so the limit stays above the resident country index rather than below it.
- Exact geometry containment now always outranks a within-tolerance neighbour in bundled country selection. Both previously set `GeometryContainsPoint`, so a neighbouring country with a tighter bounding box could win over a true containment.
- Pinned `SQLitePCLRaw.bundle_e_sqlite3` to 2.1.13, overriding the 2.1.11 resolved transitively by `Microsoft.Data.Sqlite` 10.0.5. 2.1.11 carries a SQLite build affected by CVE-2025-6965, which `NuGetAudit` raised as `NU1903` and `TreatWarningsAsErrors` turned into a hard restore failure. 2.1.13 bundles SQLite 3.53.3, past the 3.50.2 fix.
- Added `BundledCountryLookupTests`, which builds a synthetic two-country database so the containment behaviour is covered without the LFS-backed bundled dataset, including an allocation guard against regressing to full-boundary distance scans.

## 2026-04-12

- Added optional GADM administrative-area support with per-country on-demand downloads, local SQLite cache export, Kosovo code mapping, and curated split-territory fallback families.
- Split cache ownership into source-specific services for Overture and GADM, with a shared administrative-area resolver used by background processing.
- Added processing settings for enabling GADM, preferring GADM over cached Overture divisions, and enabling GADM territory fallback packages.
- Updated Lookup to show GADM diagnostics, cache status, source comparison, and live lookup progress while cache downloads and queries run.
- Added GADM cache management to the Data area, including a merged sortable/filterable administrative cache table with source, country, row count, version/release, size, downloaded time, delete, and re-download actions.
- Added GADM-specific unit and integration test coverage in a dedicated `ImmichReverseGeo.Gadm.Tests` project, plus a heavyweight all-country GADM import test marked as `Integration` and `Performance`.
- Added public Data Sources documentation covering Overture, GADM, live Overture Places diagnostics, source purpose, storage behavior, and license constraints.
- Renamed the app UI entry from Reset Geo Data to Reset Immich Geo Data to make the database impact clearer.

## 2026-04-03

- Added the City Resolver page for reviewing bundled defaults, changing the global profile, and setting country-specific city resolver overrides.
- Added bundled city resolver profile defaults plus configuration and processing support for applying user overrides on top of bundled country profiles.

## 2026-04-01

- Added a processing setting to disable airport infrastructure lookup when you prefer administrative city names.
- Added the Reset Geo Data page for clearing reverse geo `city`, `state`, and `country` values in Immich by all assets, selected asset GUIDs, or matching location values before reprocessing.

## 2026-03-29

- Initial Version.
