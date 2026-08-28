## Why

Immich ReverseGeo cannot resolve valid coordinates in Hong Kong because the bundled Overture country cache includes only `country` features, contains no Hong Kong coverage, and the bundled ISO mapping omits `HKG`/`HK`. The same export assumption can omit other distinct ISO territories, causing either no match or an incorrect sovereign identity and preventing the intended country-specific administrative lookup.

## What Changes

- Expand the offline country bootstrap to cover supported ISO territories represented by Overture below the `country` subtype.
- Resolve Hong Kong as `Hong Kong` with ISO Alpha-3 `HKG` and Alpha-2 `HK`, rather than normalizing it to China.
- Apply a generic territory-coverage rule instead of adding hand-maintained polygons or coordinate exceptions.
- Make representative offline coordinate fixtures mandatory for Hong Kong, Macao, Greenland, the Faroe Islands, Jersey, Guernsey, the Isle of Man, Puerto Rico, Guam, the U.S. Virgin Islands, Bermuda, Gibraltar, the Cayman Islands, the British Virgin Islands, Aruba, Curaçao, the Åland Islands, Réunion, French Polynesia, and New Caledonia.
- Reject a generated bundled artifact when any mandatory territory fixture is missing, maps to its administering sovereign, or has an unusable canonical ISO identity.
- Ensure every standard country or territory identity emitted by bundled spatial resolution has a usable Alpha-2/Alpha-3 mapping.
- Make overlapping country and territory selection deterministic and independent of source iteration order.
- Distinguish a true spatial no-match from a matched geometry with missing or inconsistent ISO identity.
- Add parent-country, boundary, overlap, artifact-validation, and downstream-pipeline regression coverage.
- Document distinct-territory behavior, offline guarantees, and remaining coverage limitations.

## Capabilities

### New Capabilities
- `country-resolution`: Offline coordinate-to-country and territory resolution, canonical ISO identity, deterministic selection, failure diagnostics, mandatory territory coverage, and downstream lookup eligibility.

### Modified Capabilities

None.

## Impact

- Affects the Overture country export query and schema, bundled `overture-country-divisions.db`, and bundled ISO mappings.
- Affects `OvertureDivisionsService`, `AdministrativeAreaResolverService`, country result models, Lookup diagnostics, and country-specific downstream cache selection.
- Requires regeneration and validation of the bundled country artifact from a pinned Overture release; generation becomes invalid if any mandatory fixture does not resolve to its own identity.
- Requires automated territory, parent-country, artifact, administrative-pipeline, and performance tests plus updates to public data-source documentation and changelogs.
- Requires a generated before/after inventory of canonical identities, row count, database size, country-index memory, cold initialization, and warm lookup performance.
- May increase bundled-data size and process-lifetime country-index memory more than the Hong Kong-only case; the change must avoid bundling all administrative features and must satisfy explicit release budgets before replacement.
