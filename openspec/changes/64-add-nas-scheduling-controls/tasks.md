## 1. Evidence Reconciliation

- [ ] 1.1 Re-read finalized blocks 61–63 and verify no watermark passed, block 62 remains no-go, reconciliation remains withdrawn, and block 58's exact full-eligibility `EXISTS` observation remains authoritative for every scheduled check.
- [ ] 1.2 Review MASTERPLAN block 64, proposal, delta spec, design, and tasks together and remove every stale assumption about frequent watermark checks, reconciliation controls, new NAS modes, migration/defaults, copy, or documentation work.

## 2. Existing-Contract Verification

- [ ] 2.1 Inspect `ScheduleEditorState`, schedule settings, and focused schedule tests and confirm the existing enabled/disabled, hourly, minute/hour interval, daily, weekly, and custom-cron behaviors remain unchanged.
- [ ] 2.2 Inspect the finalized Standard, Web-only, Dashboard manual-run, and Run-once contracts and confirm they already own internal scheduling, structural schedule suppression, manual processing, and external one-shot execution without a block 64 settings mode.
- [ ] 2.3 Inspect the scheduled detector contract and confirm every scheduled check uses the complete current eligibility predicate, with no watermark, cursor, reconciliation timer, or catch-up control.

## 3. Runtime-Neutral Validation

- [ ] 3.1 Confirm applying block 64 requires no source, runtime, test, configuration, migration/default, Settings or Dashboard copy, or public documentation edits; record any genuine documentation clarification only as block 70 scope.
- [ ] 3.2 Run `openspec validate 64-add-nas-scheduling-controls --strict` and `openspec status --change 64-add-nas-scheduling-controls`, confirm 4/4 artifacts are complete, and review a block-64-only diff proving blocks 63 and 65 and all implementation files are untouched.
