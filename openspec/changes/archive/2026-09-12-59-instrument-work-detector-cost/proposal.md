## Why

The scheduled work detector is intentionally advisory and may still be expensive on large Immich databases, especially when no eligible row exists. Operators need one safe, structured record per detector call to distinguish normal work/no-work checks, slow checks, cancellation, and failure without exposing database or asset data.

## What Changes

- Emit exactly one `EventId(5901, "ProcessingWorkDetectorCompleted")` terminal structured measurement for every scheduled detector invocation, including deterministic elapsed duration and the bounded outcome `HasWork`, `NoWork`, `Cancelled`, or `Failed`.
- Identify the scheduled trigger context and the detector strategy/query contract with the bounded literal `strategy=postgres-exists-v1`, rather than CLR type names, request IDs, SQL, or arbitrary metadata.
- Record the successful existence strategy's known single database roundtrip and exact `database_operation=eligibility-existence-probe` family; treat rows scanned, query plans, and physical-read evidence as unavailable at runtime and hand those diagnostics to change 60.
- Use one terminal event whose level is elevated when the detector fails or crosses a fixed slow-operation threshold. Do not sample detector terminal events.
- Keep observability log-only because the project has no metrics pipeline; do not add a metrics dependency, UI/state projection, settings, or behavior changes.
- Classify `Cancelled` only when the exact caller cancellation token is requested; classify database/command timeout and every other exception as `Failed`, while adding no timeout behavior. Cover all four outcomes, threshold levels, redaction, exact-once emission, and concurrent calls with fake time and a structured log sink.

## Capabilities

### New Capabilities
- `processing-work-detector-observability`: Provides safe, low-cardinality, exact-once operational evidence for detector outcome and cost.

### Modified Capabilities
- None.

## Impact

This change follows the detector seam from change 57 and the finalized existence-backed strategy from change 58. Expected implementation areas are a dependency-light detector instrumentation boundary, Standard-mode DI composition, structured application logging, and focused tests. It does not change scheduling, eligibility, cancellation, timeout policy, worker dispatch, detector results, processing state, public UI, configuration, database schema, SQL, or metrics infrastructure. Change 60 consumes exact `strategy=postgres-exists-v1`, exact `database_operation=eligibility-existence-probe`, and explicit runtime-unavailable plan facts for maintainer troubleshooting.
