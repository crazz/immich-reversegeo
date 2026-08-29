## 1. Decision Verification

- [ ] 1.1 Re-read finalized change 61 and verify that its selected decision remains no watermark, change 58 `EXISTS` remains preserved, and no new or revised evidence satisfies every reopen criterion.
- [ ] 1.2 Verify MASTERPLAN block 62, proposal, delta spec, design, and tasks consistently identify this as a gated/rejected implementation change with no runtime behavior.

## 2. Stale-Assumption Removal

- [ ] 2.1 Verify block 62 contains no remaining plan to add a cursor file, query, state, fallback, schema object, trigger, listener, replication slot, detector implementation, dependency-injection registration, configuration, or implementation test.
- [ ] 2.2 Verify reconciliation, overlap, deduplication, and observed low miss rates are not presented as satisfying the change 61 safety gate.

## 3. Scope and Artifact Validation

- [ ] 3.1 Confirm applying this change requires no source, database, configuration, dependency-injection, or runtime-test edits and leaves change 58's exact full-eligibility `EXISTS` behavior unchanged.
- [ ] 3.2 Run `openspec validate 62-add-persisted-watermarked-detector --strict` and `openspec status --change 62-add-persisted-watermarked-detector`, then confirm 4/4 artifacts are complete and the diff is limited to MASTERPLAN block 62 plus this change's existing four artifacts.
