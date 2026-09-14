## Why

The Dashboard manual Run action must prove the temporary child-worker backend end to end before scheduled execution changes. It must retain the existing prompt, state, log, Stop, and retrigger experience while ensuring one admitted run executes in only one process.

## What Changes

- Exercise the existing Dashboard-facing coordinator path with block 33's internal `ChildWorker` selection; keep the Dashboard unaware of backend choice.
- Preserve atomic admission and the accepted-run order: publish one run ID and cancellation owner, call `MarkPending()`, arm the matching reporter, then dispatch one child backend.
- Carry the exact request through child start, readiness, execute, typed events, state projection, cancellation, evidence classification, terminal cleanup, and matching-handle release.
- Preserve compatible user-visible progress, activities, logs, completion, no-work, cancellation, busy, and failed-run status while keeping diagnostics bounded and safe.
- Reject duplicate manual triggers without another run ID, pending transition, backend resolution, child start, or contention log.
- Do not retry, fall back to in-process execution, or expose a public backend/mode toggle. The production default and scheduled path remain unchanged for later numbered blocks.

## Capabilities

### New Capabilities
- `processing/manual-child-worker-execution`: executes an accepted manual processing request in one selected child worker while preserving the Dashboard lifecycle and exact cleanup.

### Modified Capabilities
- None.

## Impact

Depends on the finalized coordinator, temporary backend selector, child launcher, event bridge, cancellation owner, and evidence classifier/finalizer from blocks 13, 25, 27, 28, 30, and 33. Primary affected boundaries are the Dashboard-facing manual coordinator API, internal Web composition used to select `ChildWorker` for the transition and tests, the child backend adapter, and deterministic control-plane/process-fixture tests. There is no settings, CLI, environment, protocol, database, geodata, or scheduled-run contract change.

## Audit Reconciliation

Block 26 is a prerequisite for deterministic real-worker fixture coverage. The manual request uses one exact `Guid` identity whose canonical wire representation is preserved unchanged through child launch, events, bridge, cancellation, and finality. It consumes the internal exact 10-second `TimeProvider` cancellation policy without adding a public setting. UI `Processed` is projected from `UpdatedCount`, never aggregate `ProcessedCount`.

