## Why

Block 11 is a planning prerequisite, not evidence that its source has landed. At apply time, verify its required source APIs, registrations, and focused tests exist and pass; if absent, stop and apply it first rather than recreating or assuming its contract here. On that verified baseline, the hosted service still mixes cron parsing, clock-based waiting, scheduled-trigger generation, admission, and execution dispatch. Block 12 needs a deterministic schedule boundary so timing can be tested and evolved without moving block 11's authoritative eligibility/count work or preempting block 13's coordinator ownership.

## What Changes

- Isolate standard five-field cron evaluation into a deterministic UTC schedule plan driven by an injected clock, and isolate cancellable waiting behind the same time source.
- Reduce the hosted schedule loop to startup initialization, configuration snapshots, schedule-plan evaluation, next-run visibility, waiting, and generation of one scheduled trigger when due.
- Send scheduled triggers through a narrow scheduler-facing run-trigger contract whose implementation owns admission and execution; retain only a temporary adapter over block 11's existing host-owned control path until block 13 supplies the coordinator.
- Preserve the current one-minute disabled retry, five-minute invalid-expression retry, strictly-future Cronos occurrence semantics, UTC interpretation/formatting, startup ordering, configuration reevaluation points, scheduled contention log, and shutdown cancellation.
- Preserve Dashboard Run Now and manual cancellation behavior without routing schedule calculations through those APIs.
- Keep block 11's executor count as the sole authoritative eligibility fact. Block 12 adds no work preflight and owns no count, skipped-ID, asset, geodata, batching, persistence, request admission, or terminal execution behavior.
- Preserve current next-run visibility as a UI log line; add no new persisted setting, public time-zone option, dashboard field, or schedule-editor behavior.

## Capabilities

### New Capabilities
- processing-schedule-orchestration: Deterministically calculates and waits for UTC schedule occurrences and emits scheduled triggers without owning admission or processing execution.

### Modified Capabilities
- None.

## Impact

- Planned implementation affects the hosted scheduling portion of ProcessingBackgroundService, deterministic schedule-plan/wait collaborators, a narrow scheduled-trigger boundary and temporary adapter, TimeProvider registration, and focused scheduler/lifecycle tests.
- ConfigService remains the persisted snapshot source; ScheduleConfig and ScheduleEditorState remain unchanged and continue to produce/read standard five-field cron text.
- ProcessingRunExecutor and its exact count/config/skipped/batch/geodata/persistence ownership from block 11 remain unchanged.
- ProcessingBackgroundService retains its concrete-singleton plus hosted-service alias and Dashboard-facing methods until block 13 migrates coordinator ownership.
- Depends on blocks 1–11 and precedes block 13 coordinator wiring; no source, test, database, configuration, UI, or protocol migration is part of this planning change.
