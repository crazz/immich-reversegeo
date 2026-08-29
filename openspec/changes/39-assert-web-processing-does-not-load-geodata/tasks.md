## 1. Reconcile applied processing composition

- [ ] 1.1 Confirm blocks 19–20 and 33–38 are applied and passing, then record the finalized Web registration root, manual and scheduled processing roots, scheduled detector/repository seam, child-dispatch boundary, processing executor, and Overture/GADM/airport type names; do not edit block 38.
- [ ] 1.2 Inventory production ServiceDescriptor aliases/factories and classify lightweight detector repository, country identity, and resolver-profile services as allowed while classifying executor, resolver, country-index, Overture Places/divisions/cache, GADM divisions/cache/exporter, and airport services as forbidden for processing roots.
- [ ] 1.3 Expose only the narrow internal test visibility or registration metadata needed to invoke and inspect the exact production Web composition; do not create a parallel test-only composition root.

## 2. Add deterministic boundary instrumentation

- [ ] 2.1 Add a reusable service-graph test helper that walks constructor, alias, and recorded factory edges from the manual, scheduled, detector, and child-adapter roots and reports the shortest path to a forbidden processing dependency.
- [ ] 2.2 Add fail-on-resolution factories and constructor/method counters for the production executor, administrative resolver, Overture Places/divisions/cache, GADM divisions/cache/exporter, airport resolver, and any finalized equivalents.
- [ ] 2.3 Add an instance-scoped no-op production/counting-test observer immediately before the lazy bundled-country-index load, preserving the current lock, first-use behavior, and one-time publication; the test observer must throw before SQLite or geometry work.
- [ ] 2.4 Add a fake detector repository with no PostgreSQL access and a recording child-boundary fake that performs no command construction, launch, protocol I/O, worker event production, executor call, or geodata work.

## 3. Cover the processing route matrix

- [ ] 3.1 Compose the exact production Web root, apply test descriptor overrides before provider construction, and prove the processing-root service graph contains no forbidden executor/geodata dependency even though unrelated Lookup/Data descriptors remain present.
- [ ] 3.2 Exercise an accepted manual request and assert zero detector/repository access, exactly one child-boundary delegation with matching request/token, zero alternate/fallback backend access, and zero forbidden resolution, construction, method, or country-index-load observations.
- [ ] 3.3 Exercise an accepted detector-positive scheduled request through the real applied detector adapter over the fake repository and assert one detector decision, permitted repository access, exactly one later child-boundary delegation, and zero forbidden resolution, construction, method, or country-index-load observations.
- [ ] 3.4 Exercise an accepted detector-empty scheduled request through the same adapter and assert one detector decision, permitted repository access, established local zero completion, no child delegation, and zero forbidden resolution, construction, method, or country-index-load observations; reuse block 36's state/log fixture rather than duplicating its detailed lifecycle assertions.
- [ ] 3.5 Assert CountryCodeService and resolver-profile identity access is not classified as heavy geometry/index work, while an intentional test-only forbidden constructor edge and an intentional lazy-load observer call each make the guard fail with an actionable path/category.
- [ ] 3.6 Dispose every scope/provider and assert no process, command builder, launcher, protocol/session, worker event, database connection, SQLite/DuckDB, filesystem geodata, download/export, or geometry effect occurred.

## 4. Verify and preserve future boundaries

- [ ] 4.1 Run focused composition/processing boundary tests and npm run test with default exclusions.
- [ ] 4.2 Run openspec validate 39-assert-web-processing-does-not-load-geodata --strict and final openspec status --change 39-assert-web-processing-does-not-load-geodata.
- [ ] 4.3 Review the diff for block-39-only scope, confirming block 38, Lookup/Data behavior, deployment modes, protocol, and public configuration are unchanged.
- [ ] 4.4 Record block 55's stronger gate: after Lookup/Data cutover, Standard and Web-only Web composition must contain no heavy geodata, cache-mutation/exporter, country-index-loader, or processing-executor descriptor, and removable direct references must be reviewed then rather than banned here.

## Audit Reconciliation

The test substitutes and proves the finalized child-dispatch boundary contract, not a real child process. Assertions about coordinator/detector/boundary names, registration roots, and available test seams are conditional on their landed forms after prerequisite application; bind to those exact contracts and do not claim process startup, protocol, or real worker execution occurred.

