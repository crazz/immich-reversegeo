## Why

The active processing pipeline reports directly into the WebUI-specific `ProcessingState`, so its lifecycle, counters, diagnostics, and concurrent activity cannot later be consumed by an extracted executor or worker bridge. Block 7 defines run identity and terminal accounting; block 8 must define the UI-independent event boundary that carries those facts without prematurely defining a Web-state adapter or wire protocol.

## What Changes

- Introduce an asynchronous `IProcessingEventReporter` that opens a run-scoped reporting session and emits immutable, transport-neutral events for execution start, eligibility determination, progress snapshots, correlated activity start/end, typed logs, and one terminal result.
- Make the session enforce lifecycle order, coherent block-7 accounting, activity closure, terminal singularity, linearizable concurrent acceptance, cancellation races, and broken-reporter behavior without wiring the active pipeline yet.
- Distinguish aggregate processed assets, successful Immich updates, handled per-asset failures, and fatal run failure; preserve irreversible asset dispositions even if cancellation arrives before their progress publication.
- Define diagnostic severity/content boundaries from existing UI-log call sites, including pre-write resolution detail, while excluding `ILogger`-only messages and any assumption of future wire safety.
- Provide a no-op production reporter and thread-safe recording/fault-injection test support independent of `ProcessingState`.
- Preserve current scheduling, admission, `MarkPending()`, `_runLock`, cancellation ownership, UI state, counters, logs, and processing behavior. Production routing and the `ProcessingState` adapter remain block 9; resolver/cache progress remains block 10.

## Capabilities

### New Capabilities
- `processing-event-reporting`: Defines a UI-independent asynchronous run-reporting session, event vocabulary, accounting, diagnostic, activity, ordering, cancellation, and failure semantics.

### Modified Capabilities
- None.

## Impact

- Planned contract paths: dependency-light event/reporter/session types alongside block 7's Core models, plus focused contract-test support under `tests/ImmichReverseGeo.Tests/`.
- Reviewed compatibility paths: `ProcessingBackgroundService`, `ProcessingState`, `AdministrativeAreaResolverService`, and Dashboard/Logs/navigation consumers; none is adapted or rewired in this block.
- Dependencies: block 7's source models must exist before implementation. No new package, database, configuration, public HTTP API, UI, or serialization dependency is introduced.
- Follow-ons: block 9 projects the finalized session events into `ProcessingState` and performs first production routing; block 10 migrates resolver/cache activity; Phase 3 alone defines worker envelopes and wire serialization.
