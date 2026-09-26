# Changelog

Technical release notes for Immich ReverseGeo live here.

For a shorter user-facing summary, see [docs/website/changelog.md](./docs/website/changelog.md).

## Unreleased

- Updated live Overture Places queries for the September 2026 schema and documented category renames. Removed the raw Places row cap that could hide nearby landmarks behind unrelated results in dense areas; geographic bounds, confidence thresholds and ranking are retained. Added local Parquet coverage for both schemas, late-arriving landmarks and category compatibility. See the [public release summary](docs/website/changelog.md#unreleased).
- Processing now evaluates the configured primary administrative source first and consults the enabled secondary source only for a null city or state. GADM enable/prefer settings, territory precedence, airport rules and individual Immich writes are preserved; Lookup retains all requested source diagnostics.
- Added optional persistent administrative candidate metadata and R-tree bounds, with exact double filtering, characterized legacy scan-order compatibility and whole-query fallback. Existing country caches are augmented offline through a bounded temporary-copy publication; source rows/WKB/version/download dates remain intact. Ordinary optional preparation failures retain usable legacy caches. Owned Overture mutations now drain cleanup before returning cancellation. No Compose changes or larger geometry budget are required. See [cache preparation](./docs/website/using-the-app.md#administrative-cache-inventory).

- Added adaptive administrative geometry reuse based on polygon size and memory cost, shared by all countries and both sources. Large valid polygons use packed double coordinates and a compatible area scan with accounted distance workspace; smaller eligible polygons retain prepared indexes. Intrinsically unaffordable admissions now preserve unrelated cache entries. Default memory budget, coordinate precision, disk cache formats and location selection rules are unchanged. See the [measurement and accounting guide](./docs/maintainer/ADMINISTRATIVE_GEOMETRY_MEASUREMENT.md).

- Added invocation-owned prepared administrative geometry reuse shared by GADM and Overture. Metadata-first SQLite reads avoid materializing geometry blobs on cache hits; bounded admission, active leases and generation retirement preserve source selection and replacement semantics. Web and Core remain free of the Spatial dependency. Settings, cache formats, volumes, airport matching and Immich writes are unchanged. See [architecture](./docs/website/architecture.md#persistent-data-and-temporary-state) and the [opt-in measurement guide](./docs/maintainer/ADMINISTRATIVE_GEOMETRY_MEASUREMENT.md).

- Redesigned the Web UI as a Google Admin-style console with Light/Dark/Auto Appearance, system fonts, grouped Work/Configure/Library navigation, and destination titles Overview, City matching, Area caches, Reset locations, and Skip list (`/data/skip-list`; `/data` redirects). Shared CSS tokens cover primary/secondary/destructive/disabled/hover/focus states in both palettes. Existing controls, content, handlers, and worker/database behavior are retained.

- Added startup-only deployment modes through `IMMICH_REVERSEGEO_MODE`: only an absent variable defaults to Standard; exact lowercase `standard`, `web-only`, and `run-once` are accepted. Any other present value, including empty, whitespace, padded or case-varied values, fails before startup with exit `2`. Mode is not persisted; changing the container environment requires recreation and a new startup.
- All modes use one neutral image and the unchanged entrypoint, with separate persistent `/config` and `/data` mounts. Standard retains the Web UI and saved scheduling. Web-only retains the UI, manual processing and saved schedule values while suppressing the internal scheduler; it adds no public automation endpoint. Standard scheduling retains the full current-eligibility check and existing schedule presets.
- Standard/Web-only processing, Lookup and supported cache download/export/refresh jobs use temporary workers from the same image, with local arbitration and cancellation/cleanup finality. Inventory, coordinated deletion and database maintenance stay in the Web service. Heavy geographic datasets leave with the worker process; this is no guarantee of lower total memory, an RSS ceiling, an absolute peak or numeric reclamation. The selected repeated-worker fixture is bounded and has no calibrated memory profile.
- Run-once performs one direct same-process attempt without a listener, child worker, detector precheck or internal retry. Configure no automatic container restart; the operator owns any later retry. Managed exits are `0` completed (including no work), `2` invalid invocation/mode, `3` advisory-lock busy, `4` domain failure, `5` startup/infrastructure/required dependency or cleanup failure, and `130` orderly cancellation. Abrupt platform termination may return an unmapped status. Committed effects survive failure or cancellation.
- This change introduces no Immich schema change or migration of Immich data or persisted ReverseGeo configuration data. Reuse tested separate volumes after taking backups. The [release checklist](./docs/maintainer/RELEASE_CHECKLIST.md) records the candidate and previous image identities, settings/skip-list checks and stopped-work rollback limits; it does not certify all caches or image-volume combinations. Stop admissions and active work, then stop the candidate before starting the tested previous image. Remove mode configuration unsupported by that image. New settings, cache formats, forward-created data and retained/partial Immich writes may require restoring compatible backups; rollback neither reverses writes nor provides zero downtime.
- Read the [deployment and recovery guide](./docs/website/deployment-modes.md) before changing modes. Optional GADM remains restricted to academic and other non-commercial use; see [data sources and licensing](./docs/website/data-sources.md#optional-gadm-administrative-data). These notes remain Unreleased until a release version/date and published candidate image have been verified.

- Coordinated Immich location resets and skip-list clearing with the existing process-local heavy-work owner. Reset results now report each store separately, preserve partial completion, and offer a skipped-only retry without replaying a committed Immich update.
- Replaced Area caches row-count scans with a lazy bounded cache inventory that reports file status, size, modification time, optional download/version metadata, temporary work, and safe storage diagnostics without loading geodata.
- Coordinated Area caches deletion with local processing, Lookup, and cache-refresh work; deletion now reports deterministic per-file outcomes and refuses unsafe linked paths.
- Expanded the bundled Overture country artifact to include `dependency` boundaries and canonical Alpha-2, Alpha-3, and display-name identities.
- Fixed offline country detection for Hong Kong and added validated distinct-territory coverage for Macao, Greenland, the Faroe Islands, the Crown Dependencies, selected US and UK territories, Aruba, Curaçao, the Åland Islands, Réunion, French Polynesia, and New Caledonia.
- Added structured country-bootstrap outcomes that distinguish spatial misses from identity mapping failures, plus deterministic dependency-over-sovereign selection.
- Added shared territory fixtures, artifact validation, downstream Overture/GADM routing tests, and country-index size, memory, initialization, and warm-lookup performance budgets.

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
