## Why

A persisted polling watermark can silently miss eligible Immich assets when a value is assigned before transaction commit or when a relevant mutation does not advance that value. Block 61 must therefore make an evidence-backed safety decision before block 62 can replace block 58's exact full-eligibility EXISTS gate.

## What Changes

- Record repository and commit-pinned upstream evidence for asset, EXIF, UUID, transaction, trigger/event, and detector-owned-state candidates.
- Define a compatibility matrix and mutation matrix covering inserts, GPS changes and clears, ReverseGeo writes, overwrite modes, deletes/restores, backfills, timestamp ties, multi-container operation, and schema drift.
- Select **no polling watermark** because no reviewed scalar source proves freedom from false negatives under commit inversion and all relevant mutation paths.
- Preserve block 58's stateless full-eligibility EXISTS detector and mark block 62 **no-go** unless measurable revisit criteria are met by new evidence and a revised proposal.

## Capabilities

### New Capabilities
- `immich-watermark-source-selection`: Establishes an evidence-based safety gate, no-go behavior, and measurable criteria for reconsidering incremental work detection.

### Modified Capabilities
- None.

## Impact

Planning and maintainer research artifacts only. No runtime code, Immich schema, trigger, replication slot, cursor state, scheduling behavior, or public configuration changes. Blocks 62–64 remain gated; block 58's exact EXISTS behavior remains the planned correctness-preserving detector.
