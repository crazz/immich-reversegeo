## Why

A later event adapter needs a precise baseline for the state observed by the Dashboard, Logs, and navigation UI; this characterization is independently applicable and does not require an execution-lifecycle fixture. The current tests leave counter reset, terminal snapshots, activity cleanup, log retention, and mutation notifications largely unspecified.

## What Changes

- Add focused state tests for run totals, processed/skipped/error counters, reset behavior, latest-error logging, prior-completion visibility, and retained terminal values.
- Characterize equal- and distinct-label activity scopes, idempotent disposal, completion cleanup, and late-disposal behavior without relying on dictionary ordering.
- Characterize ordered 100-entry log retention and at-least-one `OnChanged` notification for observable mutations.
- Make no production behavior, UI, logging-format, or reporter-interface changes.

## Capabilities

### New Capabilities
- `processing/state-observability-characterization`: Regression contract for UI-visible run snapshots, scoped activity, bounded logs, and change notifications.

### Modified Capabilities
- None.

## Impact

- Tests: `tests/ImmichReverseGeo.Tests/ProcessingStateTests.cs`; existing state checks in `ProcessingPipelineTests.cs` remain dependency context and need not change.
- Behavior characterized but not modified: `src/ImmichReverseGeo.Web/Services/ProcessingState.cs` and its Dashboard, Logs, and NavMenu consumers.
- No API, dependency, configuration, storage, or deployment impact.
