# Overture Divisions Notes

Use `division_area` for containment.

- `division_area` is the reliable source for point-in-polygon checks and any claim that a coordinate is "in" a city, neighborhood, region, or country.
- `division` is point-based. It is useful for labels, hierarchy, and diagnostics, but it is not a reliable containment source.
- `division_area.division_id` links an area back to its parent `division`, but the relationship is not guaranteed to be 1:1.
- A `division` can exist without any `division_area`.
- A `division` can also have multiple `division_area` rows.

## Practical rule

- Keep admin resolution based on containing `division_area` rows.
- Do not let a bare `division` point override a containing `division_area` result.

## Example: neighborhood-style names

If a place such as `Kreuzberg` exists only as a `division` label with parent `Berlin`, but has no linked `division_area`, the app should keep the containing area-based result such as `Berlin`.

Only switch to the more specific neighborhood-style name when Overture provides a real containing `division_area` for it, or when another trusted boundary source is introduced.

## Country database export and validation

The maintainer exporter includes both Overture `country` and `dependency` boundaries, writes canonical names and Alpha-2/Alpha-3 codes from the bundled catalog, and retains source names/codes for traceability. Unknown source identities fail the export unless explicitly classified as non-ISO.

Generate a candidate into ignored output rather than overwriting the shipped database during review:

```powershell
dotnet run --project src/ImmichReverseGeo.Tools.OvertureExport -- --dataset country-divisions --release 2026-08-19.0 --output _out/country-export/country.db
```

The exporter validates required columns, identities, geometries, source bounds, release metadata, and exact coverage for the shared territory and mainland coordinates before replacing the requested output. On failure the previous output remains in place and the temporary export is removed.

Recheck an existing candidate without network access or file changes:

```powershell
dotnet run --project src/ImmichReverseGeo.Tools.OvertureExport -- --dataset country-divisions --validate-only --output _out/country-export/country.db
```

Validation checks that the expected identity has exact boundary coverage at each coordinate. It does not choose the runtime winner when boundaries overlap, or validate the runtime resolver's tolerance and routing behavior. Those belong in resolver tests. The existing shipped database predates the new export schema and is not expected to pass this validator. Shipping a replacement is a separate change.