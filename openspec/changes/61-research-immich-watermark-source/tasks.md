## 1. Evidence Artifact

- [ ] 1.1 Record the inspected repository revision and a declared Immich release/commit matrix with commit-pinned schema, migration, trigger, UUID, index, and mutation links.
- [ ] 1.2 Publish the candidate and compatibility matrices from the design in maintainer research documentation, clearly separating deterministic pagination, practical risk reduction, and zero-false-negative proof.
- [ ] 1.3 Record the current no-watermark decision, preserved block 58 EXISTS path, and block 62 no-go without adding runtime code or database objects.

## 2. Reproducible Verification

- [ ] 2.1 Add an isolated PostgreSQL research fixture or script that demonstrates scalar commit inversion and verifies that timestamp/UUID/txid tuples do not repair it.
- [ ] 2.2 Define version-pinned mutation cases for inserts, delayed EXIF, GPS add/change/clear, metadata clears, ReverseGeo feedback, overwrite predicates, soft delete/restore, hard delete/recreate, backfills, timezone/precision/ties, and equal marker values.
- [ ] 2.3 Define restart, replay, corruption, independent/shared-volume, and concurrent-container cases that a future persisted source must pass without unsafe advancement.

## 3. Gate Handoff

- [ ] 3.1 Verify the evidence artifact contains every measurable revisit criterion and explicitly treats overlap, deduplication, and reconciliation as insufficient proof.
- [ ] 3.2 Mark block 62 go only after a revised proposal proves zero missed transitions, commit-order safety, supported-version compatibility, bounded query cost, and operational recovery; otherwise stop with EXISTS unchanged.
- [ ] 3.3 Run `openspec validate 61-research-immich-watermark-source --strict` and `openspec status --change 61-research-immich-watermark-source`, then perform a block-61-only scope review that confirms block 60 and implementation files were untouched.
