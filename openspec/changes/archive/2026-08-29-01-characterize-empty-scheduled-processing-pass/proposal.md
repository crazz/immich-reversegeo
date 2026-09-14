## Why

Phase 1 needs an executable no-work baseline before processing execution is extracted from the Web host. The current scheduled path enters a private pass method backed by concrete services, so the zero-count gate cannot yet be tested deterministically.

## What Changes

- Add a narrow internal test seam around the existing pass invoked after scheduled admission; keep the public DI constructor, scheduler, and production call order unchanged.
- Add focused tests with the exact eligibility count returning zero once.
- Establish that no configuration read, skipped-record loading, batch retrieval, location resolution, airport lookup, or write operation is invoked after a zero eligibility result.
- Establish the current zero totals, completed non-error state, timestamps, and ordered empty-run log messages without changing production behavior.

## Capabilities

### New Capabilities
- `processing/empty-pass-characterization`: Regression contract for the no-work execution path of a scheduled processing pass.

### Modified Capabilities
- None.

## Impact

- Planning scope centers on `ProcessingBackgroundService.RunOnceAsync`, `ProcessingState`, and controlled seams for its concrete repository and geodata collaborators.
- Tests belong in `tests/ImmichReverseGeo.Tests`; current processing/state/skipped tests do not execute this service branch.
- Cron timing, run-lock admission, `MarkPending()`, startup initialization of `skipped.db`, exact-count optimization, and worker extraction remain unchanged and out of scope.
