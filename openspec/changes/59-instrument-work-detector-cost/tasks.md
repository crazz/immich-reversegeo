## 1. Prerequisite Reconciliation

- [x] 1.1 Verify changes 57 and 58 are applied, re-read change 58's final artifacts, and record the landed existence detector, single-query guarantee, scheduled call site, safe diagnostics, and Standard DI registration without modifying change 58.
- [x] 1.2 Confirm the active project still has no metrics pipeline and identify the existing structured `ILogger` test conventions; retain a log-only scope.

## 2. Detector Instrumentation

- [x] 2.1 Add one dependency-light `InstrumentedProcessingWorkDetector` decorator using injected `TimeProvider` monotonic timestamps and invocation-local state, while returning results and propagating cancellation/failure unchanged.
- [x] 2.2 Define exact `EventId(5901, "ProcessingWorkDetectorCompleted")`, one terminal template, and closed fields for duration, four outcomes, scheduled trigger context, logical purpose/coverage, fallback, exact `strategy=postgres-exists-v1`, exact `database_operation=eligibility-existence-probe`, and successful roundtrip count.
- [x] 2.3 Implement exact-once terminal emission with Information for below-threshold success/cancellation and Warning for failure or duration at/above the named 1000 ms threshold, with no sampling or second slow warning.
- [x] 2.4 Register exactly one instrumented detector path in Standard composition and prove manual, Web-only, Run-once, private-worker, startup-lazy, and heavy-dependency boundaries remain unchanged.

## 3. Safe Database Evidence

- [x] 3.1 Emit `database_roundtrips = 1` only after the finalized existence strategy successfully returns; omit it for cancelled/failed calls and never emit SQL, query plans, rows scanned, buffers, index claims, or exact eligible counts.
- [x] 3.2 Emit only the bounded literals `strategy=postgres-exists-v1` and `database_operation=eligibility-existence-probe`, and hand those exact values to change 60 without depending on CLR names or exposing query text.

## 4. Deterministic Verification

- [x] 4.1 Add a fake `TimeProvider` and capturing structured log sink that expose event ID, level, template, named state, and rendered output for assertions.
- [x] 4.2 Test `HasWork` and `NoWork` outcomes, exact elapsed values, successful one-roundtrip evidence, unchanged returned results, and the 999/1000 ms level boundary.
- [x] 4.3 Test `Cancelled` if and only if the exact caller token is requested; test database/command timeout, unmatched `OperationCanceledException`, and ordinary exceptions as `Failed` when that token is not requested; prove unchanged propagation, no timeout creation, roundtrip omission, and exactly one terminal event per call.
- [x] 4.4 Test hostile exception/request fixtures containing synthetic SQL, coordinates, IDs, credentials, parameter values, database/host/user names, and connection strings; assert none occur in event state, template, rendered message, or attached exception.
- [x] 4.5 Test independently gated concurrent calls released out of order, without sleeps, and assert per-call duration/outcome attribution and one terminal event per invocation.
- [x] 4.6 Run focused detector/coordinator/composition tests and `npm run test`, then verify no UI/state/configuration/protocol/SQL behavior changed.

## 5. Validation and Handoff

- [x] 5.1 Run `openspec validate 59-instrument-work-detector-cost --strict` and `openspec status --change 59-instrument-work-detector-cost`, then review a block-59-only diff that excludes block 58 and project code during planning.
- [x] 5.2 Hand change 60 exact `strategy=postgres-exists-v1`, exact `database_operation=eligibility-existence-probe`, and the explicit runtime-unavailable facts (rows scanned, buffers, physical reads, index use); leave all troubleshooting documentation to change 60.
