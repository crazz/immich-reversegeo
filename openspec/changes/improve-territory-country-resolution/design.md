## Context

The bundled country exporter currently selects only Overture `division_area` rows whose subtype is `country`. Runtime lookup loads those rows into an in-memory spatial index, chooses a containing candidate, and separately maps its Alpha-2 code to Alpha-3. The administrative resolver treats a missing geometry and a missing mapping as the same no-match result.

Hong Kong is not present at the exported subtype, China's bundled geometry excludes it, and the current ISO map has no `HKG`/`HK` entry. The bootstrap must remain offline and compact because it runs before the application knows which country-specific data to prepare. See `proposal.md` for motivation and `specs/country-resolution/spec.md` for required behavior.

## Goals / Non-Goals

**Goals:**

- Derive compact canonical coverage for standard ISO territories that lack an Overture country feature.
- Keep spatial coverage, display name, Alpha-2, and Alpha-3 identity consistent in the generated artifact.
- Guarantee the mandatory territory matrix defined by `specs/country-resolution/spec.md`, including Hong Kong, Macao, and the 18 additional requested territories.
- Provide stable candidate selection and explicit failure outcomes.
- Keep artifact size, index memory, and warm lookup performance bounded and measurable.

**Non-Goals:**

- Add a live country-resolution dependency.
- Hand-maintain territory boundaries or coordinate-range exceptions.
- Normalize mandatory territories to their administering sovereign states.
- Bundle all global administrative divisions.
- Change state/city source preference or city-selection policy.
- Guarantee distinct coverage beyond the mandatory matrix when the pinned Overture release has no usable geometry.

## Decisions

### 1. Export canonical country and dependency coverage directly

The exporter will select Overture `division_area` rows whose subtype is `country` or `dependency`. Overture already publishes the mandatory territories as complete level-1 dependency features with their own Alpha-2 codes, stable source IDs, land or maritime class, geometry, and bounds. These rows will be preserved directly rather than dissolved or reconstructed from lower administrative tiers.

A read-only spike against Overture release `2026-08-19.0` found dependency coverage for all 20 mandatory identities. Both reported Hong Kong coordinates, the other mandatory coordinates, and representative controls in China, Denmark, the United Kingdom, the United States, the Netherlands, Finland, and France resolved to the expected source identity. The mandatory subset comprised 39 rows and 1.52 MiB of WKB; all dependency features comprised 105 rows across 53 codes and 2.60 MiB of WKB.

Each exported row will carry its source ID, canonical display name, Alpha-2, Alpha-3, source subtype, land/territorial metadata, WKB, and source bounds. Export validation will reject duplicate IDs, invalid geometry or bounds, and mandatory identities that fail the shared fixture catalog.

Alternatives rejected:

- A Hong Kong-only polygon or coordinate check would create a second boundary source and would not fix similar territories.
- Treating China as a fallback would violate the required Hong Kong identity.
- Reconstructing dependencies from `macroregion` or `region` rows and dissolving their geometry adds complexity without improving coverage in the verified release.
- Exporting all division rows would unnecessarily increase image size and process memory.
- A live reverse-geocoding fallback would break the offline bootstrap contract.

If a future pinned release no longer provides a mandatory dependency, artifact validation will fail. A lower-tier fallback can then be proposed with evidence from that release rather than being carried preemptively.

### 2. Share one canonical ISO identity catalog

The exporter and runtime will use the same bundled ISO identity catalog. It will provide canonical display name, Alpha-2, and Alpha-3 values for every mandatory identity: Hong Kong, Macao, Greenland, the Faroe Islands, Jersey, Guernsey, the Isle of Man, Puerto Rico, Guam, the U.S. Virgin Islands, Bermuda, Gibraltar, the Cayman Islands, the British Virgin Islands, Aruba, Curaçao, the Åland Islands, Réunion, French Polynesia, and New Caledonia.

The exporter will reject standard coverage rows that cannot be mapped and will write canonical identity fields into the country database. Runtime will validate stored identity against the catalog before returning a match. Non-standard Overture codes may remain explicitly classified as non-ISO and cannot silently enter country-specific downstream paths.

This avoids deriving identity from localized platform names and prevents the spatial database and mapping file from drifting independently.

### 3. Return a structured bootstrap outcome

Country lookup will return a structured outcome rather than inferring state from nullable tuple members:

- `Matched`: geometry and canonical identity are usable.
- `SpatialNoMatch`: no candidate covers the coordinate.
- `IdentityMappingFailure`: a candidate covers the coordinate but its identity is missing or inconsistent.

The administrative resolver will continue only for `Matched` and will report the two failure outcomes separately. The matched result supplies `HK` to Overture cache selection and `HKG` to GADM cache selection.

### 4. Apply a total candidate ordering

Candidate selection will compare, in order:

1. Exact geometry coverage before tolerance-only coverage.
2. Canonical distinct-territory coverage before a broader sovereign candidate when identities differ.
3. Coverage-tier priority.
4. Existing land/territorial and area rules.
5. Stable row ID as the final tie-breaker.

The final tie-breaker makes results independent of SQLite and spatial-index iteration order. Tests will construct reversed candidate orders and repeat lookups to verify stability.

### 5. Use one mandatory fixture catalog across validation layers

A single test-data catalog will define the representative coordinate, canonical display name, Alpha-3, Alpha-2, and administering sovereign family for every mandatory territory in the specification. Country unit tests, generated-artifact validation, integration tests, and export acceptance will consume the same catalog so coordinates and expected identities cannot drift between test layers.

The catalog will also define representative parent-country controls for China, Denmark, the United Kingdom, the United States, the Netherlands, Finland, and France. These controls prove that adding distinct territory coverage does not relabel ordinary sovereign coordinates.

### 6. Validate generated data before replacement

Country export will validate:

- non-empty and valid generated geometry;
- source bounds containing the exported geometry;
- unique stable IDs;
- canonical identity completeness;
- every mandatory coordinate resolving to its specified territory identity;
- no mandatory coordinate resolving to its administering sovereign;
- all representative parent-country controls retaining their sovereign identity;
- release metadata, row count, database size, and expected indexes;
- a before/after identity inventory and disk, retained-memory, cold-start, and warm-lookup measurements.

Generation will fail if the pinned Overture release's country and dependency coverage cannot satisfy any mandatory fixture. The checked-in artifact will be replaced only after all correctness checks and performance budgets pass.

## Risks / Trade-offs

- **[Overture changes territory classification or lacks a mandatory dependency]** → Export an explicit `country`/`dependency` subtype set, run the shared fixture catalog on every regeneration, and block artifact replacement until every required identity passes.
- **[Source dependency geometry is invalid or too broad]** → Validate geometry, mandatory territory coordinates, and parent-country controls before publishing the artifact.
- **[Overlapping coverage selects the wrong identity]** → Use explicit distinct-territory priority and deterministic overlap tests.
- **[The broader mandatory matrix increases bundle and in-memory index size]** → Export only `country` and `dependency` rows, publish before/after disk and retained-memory measurements, enforce release budgets, and reject an all-division fallback.
- **[Expanded identity coverage reveals missing downstream data]** → Keep country bootstrap success separate from source-specific Overture or GADM availability and report later failures accurately.
- **[A process retains the old country index]** → Ship code and artifact together and require an application/container restart after deployment.

## Migration Plan

1. Introduce the shared mandatory fixture catalog and canonical ISO identity catalog.
2. Update the country export schema and select canonical `country` and `dependency` rows.
3. Generate a candidate database from a pinned Overture release and validate every mandatory territory and parent-country control.
4. Update runtime loading, deterministic selection, and structured outcomes.
5. Run country, downstream-pipeline, integration, and performance tests from the shared fixture catalog.
6. Publish the before/after identity, disk, and memory report and confirm release budgets.
7. Replace the bundled database and deploy it with the matching runtime code.
8. Restart the application/container to load the new index.

Rollback restores the prior runtime code, ISO catalog, and bundled database as one unit, followed by a restart. Downloaded country-specific administrative caches require no migration.
