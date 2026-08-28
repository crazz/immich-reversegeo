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

## Bundled country artifact

Country bootstrap exports Overture `country` and `dependency` `division_area` rows directly. The exporter applies the shared canonical ISO catalog and rejects the artifact unless every mandatory territory and parent-country fixture resolves correctly.

Pinned release: `2026-08-19.0`

| Measurement | Previous artifact | Current artifact | Change |
|---|---:|---:|---:|
| Rows | 378 | 483 | +27.8% |
| Source identities | 219 | 272 | +24.2% |
| Database size | 162,414,592 bytes | 167,931,904 bytes | +3.40% |
| WKB size | 161,847,272 bytes | 167,239,276 bytes | +3.33% |
| Retained managed memory | 758,169,224 bytes | 791,235,176 bytes | +4.36% |
| Cold initialization | 784.93 ms | 882.47 ms | +12.43% |
| Warm lookup average | 2.36 ms | 5.50 ms | +3.14 ms |

The current artifact contains 105 dependency rows across 53 additional source identities. Validation recognized 248 standard identities. Performance was measured on macOS arm64 with 25 warm Berlin lookups; absolute timings vary by machine.

Release budgets enforced by the performance test are no more than 5% file/WKB growth, no more than 5x WKB size in retained managed memory, cold initialization under 30 seconds, and warm lookup average under 250 ms.

Regenerate with `npm run export:country-divisions`. The exporter pins the requested release in `_meta`, validates the complete fixture catalog before replacing the existing file, and prints row, identity, WKB, and file-size totals.

## Example: neighborhood-style names

If a place such as `Kreuzberg` exists only as a `division` label with parent `Berlin`, but has no linked `division_area`, the app should keep the containing area-based result such as `Berlin`.

Only switch to the more specific neighborhood-style name when Overture provides a real containing `division_area` for it, or when another trusted boundary source is introduced.
