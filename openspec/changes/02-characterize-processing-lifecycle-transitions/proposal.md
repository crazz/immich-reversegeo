## Why

Phase 1 needs an executable baseline for processing admission, single-run ownership, and terminal cleanup before the coordinator and worker boundary replace in-process control flow. These behaviors currently live in `ProcessingBackgroundService` and have no service-level regression coverage.

## What Changes

- Extend block 1's internal processing test seam only enough to drive pending, active, successful, cancelled, and failed passes deterministically.
- Add a narrow internal scheduled-admission entry used by the real due-schedule branch, without testing cron timing or delays.
- Characterize silent manual contention and logged scheduled contention while one run owns execution, including that a rejected attempt does not mark pending or alter the owning run's start timing.
- Prove run ownership is released only after success, manual cancellation, or pass-level failure completes terminal cleanup, so later manual and scheduled work can be admitted.
- Preserve current behavior; do not introduce an outcome model, coordinator, executor, or worker protocol.

## Capabilities

### New Capabilities
- `processing/lifecycle-characterization`: Regression contract for observable processing lifecycle transitions and single-run arbitration.

### Modified Capabilities
- None.

## Impact

- Planning and later tests center on `ProcessingBackgroundService` admission, `RunOnceAsync` terminal handling, and observable `ProcessingState` fields and logs.
- Tests belong in `tests/ImmichReverseGeo.Tests` and build on block 1's internal operation/pass seam.
- Scheduled host-token cancellation, per-asset cancellation behavior, cron calculation, real scheduler delays, public API behavior, persistence, and geodata algorithms remain unchanged.
