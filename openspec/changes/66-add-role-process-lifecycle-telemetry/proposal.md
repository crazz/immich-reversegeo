## Why

The worker-based deployment has several independent lifecycle owners, but no single stable and safe event catalog that lets operators correlate selected mode, parent/child process lifetime, cancellation, protocol/finality failures, detector cost, and bounded coalescing pressure. Block 66 adds that diagnostic contract after block 65 without changing execution behavior or exposing high-cardinality identifiers as metrics.

## What Changes

- Define exact stable EventIds, names, fields, closed values, and levels for mode selection; role-process startup, readiness, stopping, and stop; correlated child launch and PID observation; cancellation request, grace, and escalation; protocol violation; terminal and process-exit classification; and coalescer saturation.
- Preserve one canonical JobId end to end—equal to RunId for ProcessAssets—plus exact job kind and bounded origin, while explicitly distinguishing controller and worker PIDs.
- Reuse `EventId(5901, "ProcessingWorkDetectorCompleted")` unchanged instead of producing duplicate detector telemetry.
- Measure lifecycle durations with injected `TimeProvider` monotonic timestamps and define parent-owned, best-effort child working-set sampling with explicit unavailable semantics.
- Keep lifecycle telemetry structured-log-only, bounded, non-per-item, and redacted from coordinates, payloads, command lines/private selectors, environment/configuration, credentials/secrets, raw protocol data, and raw stderr.
- Add deterministic structured-event-sink tests and focused extensions to the existing process fixture; block 67's failure matrix remains separate.

## Capabilities

### New Capabilities
- `worker-lifecycle-telemetry`: Stable, correlated, redacted log events for deployment roles, worker jobs and processes, cancellation/finality, detector reuse, coalescer saturation, and best-effort memory observation.

### Modified Capabilities
- None.

## Impact

Implementation will instrument the landed selection/composition, host lifecycle, launcher/session, cancellation, classifier, detector, and post-validation coalescer seams from blocks 18–32, 40–51, 59, and 65. It changes no protocol bytes, exit codes, retry policy, scheduler behavior, UI state, metrics/exporter configuration, public settings, or block 65/67 ownership.