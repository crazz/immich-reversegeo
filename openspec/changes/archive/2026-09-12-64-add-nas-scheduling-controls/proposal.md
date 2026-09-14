## Why

Block 64 assumed a safe frequent watermark path and a separate reconciliation cadence would need a NAS-oriented control surface. Finalized block 61 selected no watermark, block 62 is no-go, and block 63 rejected reconciliation, while the existing schedule and deployment-mode contracts already express every valid operating choice.

## What Changes

- Reject and withdraw NAS-specific scheduling controls under the finalized blocks 61–63 evidence.
- Preserve the existing enabled/disabled schedule and `ScheduleEditorState` hourly, minute/hour interval, daily, weekly, and custom-cron behavior.
- Preserve Standard internal scheduling, Web-only structural schedule suppression without saved-setting mutation, Dashboard manual runs, and the separate Run-once external-scheduler contract.
- Require block 58's exact full-eligibility `EXISTS` observation on every scheduled check; add no frequent-watermark or reconciliation controls.
- Add no settings migration, defaults, UI copy, runtime behavior, tests for invented modes, or public documentation; defer any genuine deployment-mode documentation clarification to block 70.

## Capabilities

### New Capabilities
- `nas-processing-scheduling`: Records the evidence-based no-go contract for NAS-specific umbrella controls while existing schedule and deployment-mode behavior remains authoritative.

### Modified Capabilities
- None.

## Impact

Planning artifacts and the MASTERPLAN block 64 decision only. No source, runtime, tests, configuration, migration/default behavior, Settings or Dashboard copy, or documentation changes. Blocks 63 and 65 remain untouched.
