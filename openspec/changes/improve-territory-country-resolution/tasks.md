## 1. Canonical Identity and Country Export

- [x] 1.1 Create one shared mandatory fixture catalog containing coordinates, canonical names, Alpha-2, Alpha-3, and administering-sovereign families for all 20 required identities plus parent-country controls.
- [x] 1.2 Replace the Alpha-3-to-Alpha-2-only data with one canonical identity catalog covering every mandatory identity and preserving explicitly classified non-ISO codes.
- [x] 1.3 Update country-code service tests to verify forward and reverse canonical mappings for the full mandatory matrix.
- [x] 1.4 Extend the country export query to select canonical `country` and `dependency` rows directly.
- [x] 1.5 Preserve source IDs, geometry, bounds, and land/territorial classification without dissolving lower administrative tiers.
- [x] 1.6 Update the bundled country schema to store canonical Alpha-2, Alpha-3, display name, source subtype, geometry, and metadata required for deterministic lookup.
- [x] 1.7 Add export-time validation for identity completeness, geometry validity, source bounds, stable IDs, every mandatory fixture, every parent-country control, and release metadata.
- [x] 1.8 Regenerate `overture-country-divisions.db` from a pinned release and publish the before/after identity inventory, row count, file size, and WKB size.

## 2. Runtime Country Bootstrap

- [x] 2.1 Add a structured country-bootstrap result representing matched, spatial-no-match, and identity-mapping-failure outcomes.
- [x] 2.2 Update bundled country-index loading to read canonical identities and source tiers and validate each standard identity against the shared catalog.
- [x] 2.3 Implement a total candidate ordering with exact-before-tolerance, distinct-territory priority, coverage-tier ordering, existing geographic rules, and stable-ID tie-breaking.
- [x] 2.4 Update administrative resolution and progress diagnostics to handle spatial and mapping failures distinctly.
- [x] 2.5 Make downstream cache selection consume each matched territory's own Alpha-2 and Alpha-3 values without substituting an administering sovereign identity.
- [x] 2.6 Add data-driven routing tests proving all mandatory identities pass their expected Alpha-2 to Overture and Alpha-3 to GADM preparation paths.

## 3. Mandatory Territory and Regression Tests

- [x] 3.1 Add offline Hong Kong tests for both reported coordinates and a mainland China control.
- [x] 3.2 Add an offline Macao test and a nearby mainland China control.
- [x] 3.3 Add offline Greenland and Faroe Islands tests with a mainland Denmark control.
- [x] 3.4 Add offline Jersey, Guernsey, and Isle of Man tests with a mainland United Kingdom control.
- [x] 3.5 Add offline Puerto Rico, Guam, and U.S. Virgin Islands tests with a mainland United States control.
- [x] 3.6 Add offline Bermuda, Gibraltar, Cayman Islands, and British Virgin Islands tests with a mainland United Kingdom control.
- [x] 3.7 Add offline Aruba and Curaçao tests with a Netherlands control.
- [x] 3.8 Add an offline Åland Islands test with a mainland Finland control.
- [x] 3.9 Add offline Réunion, French Polynesia, and New Caledonia tests with a metropolitan France control.
- [x] 3.10 Add reversed-order and repeated overlap tests across representative sovereign families to prove deterministic selection.
- [x] 3.11 Add fixture tests distinguishing a true spatial miss from a matched geometry with missing or inconsistent ISO identity.
- [x] 3.12 Extend bundled-artifact tests so every mandatory fixture resolves correctly, no fixture normalizes to its administering sovereign, and every emitted standard identity has canonical mappings.
- [x] 3.13 Add administrative-resolution tests proving mandatory territory matches reach configured downstream stages instead of returning initial no-match.
- [x] 3.14 Compare bundled database size, total WKB, country-index retained memory, cold initialization, and warm lookup performance with the previous artifact and enforce the agreed release budgets.

## 4. Documentation and Verification

- [x] 4.1 Update public data-source documentation with the mandatory territory matrix, distinct-identity behavior, offline guarantees, limitations, and the Lookup-first validation workflow.
- [x] 4.2 Update `CHANGELOG.md` and `docs/website/changelog.md` together with the user-visible territory-resolution improvements.
- [x] 4.3 Run `npm run test` and resolve all normal test failures.
- [x] 4.4 Run the relevant Overture export and integration validation and confirm the checked-in database passes the complete shared fixture catalog.
