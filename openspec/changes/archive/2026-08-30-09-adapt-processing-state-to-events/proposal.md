## Why

Blocks 7 and 8 define run identity, accounting, and a validating event session, but the active in-process pass still mutates the singleton WebUI state directly. Block 9 must add the state-backed reporter and route the main production pass through it without changing what Dashboard, Logs, or NavMenu show.

## What Changes

- Add a singleton in-process event-reporter adapter that projects only the currently admitted run into the existing singleton `ProcessingState`.
- Arm the adapter with the accepted request after nonblocking admission and `MarkPending()`, then route the main `RunOnceAsync`/asset lifecycle, progress dispositions, UI logs, and terminal result through the block-8 run session.
- Preserve pending versus start timing, supplied total, terminal timestamps and summaries, `UpdatedCount`-to-`ProcessedThisRun` compatibility, distinct skipped/handled-error/fatal-error presentation, `LastError`, bounded ordered logs, scoped activities (including duplicate labels), and at-least-one `OnChanged` notification per observable projection.
- Correlate projection by run and activity identity so stale, late, duplicate-terminal, and cross-run events cannot mutate a current or completed snapshot.
- Keep the Blazor consumers unchanged and bind `IProcessingEventReporter` to the exact singleton adapter instance.
- Leave startup/schedule/contention logs, `MarkPending()`, lock/CTS ownership, and the production resolver/cache `ProcessingResolutionProgress -> ProcessingState` bridge direct; block 10 moves that remaining resolver progress path.
- Exclude all Phase 3 protocol envelopes, serializer names, wire timestamps/sequences, framing, redaction, and worker transport.

## Capabilities

### New Capabilities
- `processing-event-state-adapter`: Projects one admitted in-process event session into the characterized WebUI processing state and performs the first compatibility-preserving production routing.

### Modified Capabilities
- None.

## Impact

- Planned implementation paths include `ProcessingState`, a Web-layer event adapter, `ProcessingBackgroundService`, and Web DI registration; Razor consumers remain unchanged.
- Focused adapter/state/service tests will cover deterministic lifecycle, mapping, log, activity, correlation, cancellation, and DI ownership behavior.
- Blocks 7 and 8 are hard source prerequisites. Their planning artifacts are complete in this checkout, but their source types are not currently present; block-9 apply must verify them and stop rather than duplicate those contracts.
- No database, configuration, HTTP API, persisted data, UI markup, resolver result, processing output, or worker protocol changes are introduced.
