## Why

Block 35 defines a local no-work path before worker dispatch, but its broad scheduler matrix does not by itself make the absence of worker and heavy-service materialization a durable, focused regression. This change protects that exact accepted-empty boundary and complements block 1's former in-process characterization.

## What Changes

- Add one focused accepted scheduled-pass regression proving exactly one normal-false detector call and zero selected-backend resolution, command construction, child launch/process start, protocol/session handling, worker-event state bridging, or worker event/result input.
- Assert the exact local pending-to-zero lifecycle, ordered empty-run logs, terminal idle cleanup, and absence of cancellation/failure presentation.
- Use detector, backend, launcher, protocol, and geodata fakes plus fail-on-resolution factories/constructor counters so lazy non-materialization is deterministic.
- Keep the test independent of real PostgreSQL/SQLite databases, geodata files, the heavy production DI graph, and spawned processes.
- Leave block 35's eligible, busy/duplicate, detector-cancellation, detector-failure, worker-zero, and advisory-Busy matrix unchanged rather than duplicating it.

## Capabilities

### New Capabilities
- `processing/empty-scheduled-worker-gating`: regression verification that an admitted detector-empty schedule completes locally without resolving or communicating with a worker or heavy processing graph.

### Modified Capabilities
- None.

## Impact

Depends on the finalized block-35 scheduled detector, coordinator/local-finalizer, selected-backend, and test seams and the existing block-26 process-fixture boundary. Expected changes are confined to focused tests under `tests/ImmichReverseGeo.Tests/`; production behavior, configuration, protocol, DI registrations, databases, geodata, and process launch code do not change.

## Audit Reconciliation

This test-only change depends on the block-35 fixture and its landed scheduled detector/local-finalizer/child-backend seams. It reuses that fixture to prove detector-zero behavior rather than inventing a second scheduler, detector, child boundary, or worker fixture; implementation must conditionally bind to the exact landed names after block 35.

