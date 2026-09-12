## Context

See `proposal.md` for the withdrawn premise and `specs/nas-processing-scheduling/spec.md` for the no-go contract. Finalized block 61 selected no watermark, block 62 rejected persisted incremental detection, and block 63 rejected a separate reconciliation cadence. Block 58's exact full-eligibility `EXISTS` observation therefore remains authoritative for every scheduled check.

The existing schedule stores one enabled flag and one cron expression. `ScheduleEditorState` already parses/builds hourly, minute/hour interval, daily, weekly, and custom-cron choices; disabled scheduling leaves Dashboard manual runs available. Separately, Standard owns the internal scheduler, Web-only structurally excludes scheduling without mutating the saved schedule, and Run-once supplies one external-scheduler attempt.

## Goals / Non-Goals

**Goals:**
- Reconcile block 64 with finalized blocks 61–63 and the existing schedule/deployment-mode boundaries.
- Preserve the single full-eligibility scheduled path and all existing valid operator choices.
- Make an apply pass runtime-neutral and define an objective future evidence gate.

**Non-Goals:**
- Do not introduce a watermark, cursor, reconciliation cadence, catch-up path, or related control.
- Do not modify schedule configuration, persistence, defaults, migration behavior, `ScheduleEditorState`, Settings/Dashboard copy, Standard, Web-only, Run-once, or manual processing contracts.
- Do not add runtime tests, public documentation, or implementation work for rejected modes. Any genuine deployment-mode documentation clarification remains with block 70.
- Do not revise blocks 63 or 65.

## Decisions

### 1. Withdraw NAS-specific umbrella modes

The proposed control family is rejected because its frequent-watermark and reconciliation responsibilities do not exist. Renaming existing manual, custom-cron, or deployment-mode behavior as NAS modes would add a second vocabulary and configuration surface without new capability.

Alternative: keep dormant controls for a future watermark. Rejected because it would create defaults, migration, copy, and persistence semantics before the prerequisite architecture passes its evidence gate.

### 2. Preserve schedule preference and deployment topology as separate contracts

The existing enabled/cron settings remain schedule preferences. Standard consumes them for internal scheduling; Web-only suppresses internal scheduling structurally while retaining them; Run-once is selected outside settings and performs one externally initiated attempt. Dashboard manual processing remains available under the existing Web contracts.

Alternative: represent manual, Web-only, and Run-once as values in one Settings scheduling-mode selector. Rejected because it conflates a persisted cadence preference with immutable process topology and would contradict blocks 40–43.

### 3. Keep every scheduled check on full eligibility

No optimization identity is introduced. A scheduled check continues to use the exact block 58 full-current-state `EXISTS` predicate; it is not a watermark check or reconciliation pass. Reconciliation, overlap, deduplication, and low observed miss rates cannot relax block 61's zero-false-negative gate.

Alternative: describe frequent full checks as watermark checks for user simplicity. Rejected because the label is technically false and would create expectations for cursor state and a repair cadence.

### 4. Leave documentation clarification to block 70

Block 64 adds no public copy. Existing schedule UI already explains enabled/disabled and preset/custom behavior, while blocks 40–43 own deployment-mode semantics. If a later repository-wide review finds a real operator documentation gap, block 70 can clarify the established contracts without inventing settings.

## Risks / Trade-offs

- [The feature-shaped change name may imply implementation] → State withdrawn/no-go consistently in MASTERPLAN and all four artifacts.
- [NAS operators may still need deployment guidance] → Keep behavior unchanged and evaluate only documentation clarification in block 70.
- [A future safe incremental source may need controls] → Reopen only after every block 61 gate passes, then explicitly revise block 64 for that actual architecture.
- [Negative requirements may drift from runtime] → Verify existing schedule, mode, manual-run, and full-eligibility contracts by repository inspection without modifying implementation.

## Migration Plan

No migration, deployment, rollback, default, copy, or documentation change applies. Validate the four planning artifacts and verify a block-64-only diff; future implementation requires new passing block 61 evidence and explicit revision of this planning set.
