## Why

Scheduled occurrences must use the child-worker isolation path without paying process startup cost when the current eligibility predicate finds no work. The route needs an explicit pre-launch gate that preserves the coordinator, state, and scheduler contracts already established by blocks 11–13 and 33.

## What Changes

- Add one scheduled-only, fakeable boolean pre-launch detector seam. Its initial implementation calls the current exact eligibility count and maps count greater than zero to work; the worker executor still performs and publishes its own authoritative exact count.
- Run the detector only after process-local admission, active-handle/CTS publication, immediate pending-state mutation, and reporter arming, but before resolving either execution backend or constructing child/geodata services.
- Complete a detector no-work result locally with the established zero-run state and log presentation; launch no child and perform no skipped-ID, processing-config, batch, protocol, or geodata work.
- Route a positive result through the already-selected child backend and await the accepted attempt through worker terminal, transport finality, cleanup, and exact-handle release.
- Define local-busy, worker-busy, detector cancellation/failure, and detector/worker race outcomes without adding fallback, retry, catch-up, or new schedule semantics.
- Preserve the existing schedule snapshot and worker-owned processing-config boundaries; do not add settings or eligibility data to the worker request.

## Capabilities

### New Capabilities
- `processing/scheduled-child-worker-execution`: gates an admitted scheduled attempt before backend activation and runs eligible work through the existing child-worker lifecycle.

### Modified Capabilities
- None.

## Impact

The scheduler/coordinator boundary from blocks 12–13 gains a scheduled-only pre-dispatch gate and local pre-dispatch finalization path. The implementation reuses block 33's selected-backend route and blocks 25–31's launcher, event bridge, cancellation, failure, and advisory-lock behavior. Block 36 owns the focused empty-path regression; blocks 57–58 later replace the count-backed detector implementation with the general detector/existence-probe optimization without changing this route.

## Audit Reconciliation

Scope is scheduled accepted execution only and consumes the established detector/local-finalizer contracts and prerequisites; it neither changes manual routing nor makes child-worker the default. The default remains in-process until block 37. Its detector-zero local path emits no worker producer event or worker result, while a canonical advisory Busy remains a child terminal distinct from local admission rejection.

