## Why

Block 63 assumed frequent checks would become watermarked and would therefore need a periodic full-scan backstop. Finalized block 61 selected no watermark, made block 62 no-go, and retained block 58's exact full-eligibility observation on every scheduled check, so a separate reconciliation cadence has no distinct correctness role.

## What Changes

- Reject and withdraw the proposed daily or weekly reconciliation feature.
- Preserve the existing single schedule and block 58 full-eligibility detector for every scheduled check.
- Add no reconciliation configuration, cadence, trigger path, UI, activity/log classification, persisted state, or runtime behavior.
- Remove stale block 63 assumptions that a watermarked frequent path exists or that block 64 must choose a reconciliation default.
- Permit reconsideration only through a new or revised proposal after a watermark source passes block 61's zero-false-negative gate.

## Capabilities

### New Capabilities

- `periodic-eligibility-reconciliation`: Records the evidence-based no-go contract for a separate reconciliation cadence while ordinary scheduled checks remain full-eligibility checks.

### Modified Capabilities

- None.

## Impact

Planning artifacts and the block 63 MASTERPLAN entry only. No runtime code, tests, schedule configuration, settings storage, Settings or Dashboard UI, activity/log behavior, persisted state, database objects, or public documentation changes. Blocks 62 and 64 remain untouched.
