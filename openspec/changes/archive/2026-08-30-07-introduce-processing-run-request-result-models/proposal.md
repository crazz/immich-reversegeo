## Why

The processing pipeline currently carries run lifecycle facts through mutable Web UI state whose counters conflate successful writes with “processed” work. Immutable, validated request and terminal-result values are needed before event reporting and executor extraction can establish a UI-independent boundary.

## What Changes

- Add an immutable request with a non-empty `Guid` run ID and one of three trigger values: manual, scheduled, or run-once.
- Add an immutable terminal result with the originating request, ordered zero-offset UTC timestamps, non-negative `long` processed/updated/skipped/failed asset counts, one of completed/cancelled/failed outcomes, and failure detail only for a failed outcome.
- Define processed as the aggregate of terminally classified assets and updated as the subset successfully written to Immich; preserve the current Web UI “processed” meaning as successful writes.
- Validate identity, enum, timestamp, counter, accounting, and failure-detail invariants at construction.
- Keep execution wiring, scheduling, cancellation control flow, persistence, UI state, reporting, worker protocol, serialization, and public behavior unchanged.

## Capabilities

### New Capabilities
- `processing-run-models`: Defines the immutable request identity, trigger vocabulary, terminal result, accounting, outcome, and validity rules for one accepted processing run.

### Modified Capabilities
- None.

## Impact

- Add dependency-light model types under `src/ImmichReverseGeo.Core/Models/` and focused tests under `tests/ImmichReverseGeo.Tests/`.
- Existing `ProcessingBackgroundService`, `ProcessingState`, Blazor components, DI, configuration, storage, database schema, and processing behavior remain unchanged in this block.
- The models are transport-neutral domain contracts. Phase 3 separately defines protocol envelopes, JSON/wire names, versions, framing, sequencing, and exit-code mapping.
