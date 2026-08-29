## Why

Phase 5 needs a bounded way to exercise the already-planned child-worker path without moving admission, state ownership, or cancellation semantics out of the singleton processing coordinator. The transition must start safely on the in-process executor, expose an explicit internal child selection for tests and staged cutover, and disappear once child execution becomes the only production path.

## What Changes

- Add one deliberately temporary, internal processing-backend selection at the coordinator dispatch boundary.
- Default the internal selection to in-process execution in block 33, while allowing explicit child-worker selection in tests and the transition changes.
- Make both backends consume the same admitted request, run ID, reporter, coordinator cancellation token, arbitration handle, and result contract; dispatch exactly one selected backend with no fallback, retry, or dual execution.
- Resolve and instantiate only the selected backend so an unselected child process path and an unselected in-process/geodata graph have no construction or runtime effects.
- Normalize child-session cancellation, finality, and classified outcomes through the coordinator contract so Web callers and ProcessingState remain backend-agnostic.
- Keep the selector out of AppConfig, persisted settings, environment/command-line deployment modes, public DI APIs, and UI. Reject unsupported internal values before host start or run admission.
- Define the staged removal through blocks 34–38; block 38 deletes the production selector and in-process registration.

## Capabilities

### New Capabilities
- `processing/worker-backend-selection`: provides a temporary internal choice between in-process and child-worker execution behind one coordinator contract, with isolated lazy activation and deterministic failure behavior.

### Modified Capabilities
- None.

## Impact

Implementation is expected at the block-13 coordinator dispatch seam and its Web composition registration, adapting the block-11 executor and blocks 25, 27, 28, and 30 child-session path without changing their owned contracts. Focused tests belong under `tests/ImmichReverseGeo.Tests/`; no AppConfig, settings JSON, Dashboard, public deployment-mode, protocol, geodata algorithm, or database-schema change is included.

## Audit Reconciliation

This change has applied blocks 29, 31, and 32 as prerequisites in addition to its existing prerequisites. The child backend consumes launcher/session/bridge/classifier finalization only; it is never a producer/reporter, never emits lifecycle/progress/log/activity/terminal events, and never reports a second terminal. It returns only the finalized receipt/result of the authoritative child path.

