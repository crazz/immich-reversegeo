## 1. Trigger, outcome, and request contract

- [ ] 1.1 Add `ProcessingRunTrigger` with only `Manual`, `Scheduled`, and `RunOnce`, and `ProcessingRunOutcome` with only `Completed`, `Cancelled`, and `Failed`, under `ImmichReverseGeo.Core.Models` without serialization or UI annotations.
- [ ] 1.2 Add immutable `ProcessingRunRequest` with get-only `Guid RunId` and `ProcessingRunTrigger Trigger` data, rejecting `Guid.Empty` and undefined trigger values while leaving admission and ID generation for block 13.

## 2. Terminal result contract

- [ ] 2.1 Add immutable `ProcessingRunResult` with the originating `ProcessingRunRequest`, `DateTimeOffset StartedAtUtc` and `EndedAtUtc`, non-negative `long` processed/updated/skipped/failed counts, `ProcessingRunOutcome Outcome`, and nullable `string FailureMessage`.
- [ ] 2.2 Validate non-null request identity, defined outcomes, zero-offset UTC timestamps, end-at-or-after-start ordering, non-negative counts, and `ProcessedCount == UpdatedCount + SkippedCount + FailedCount` using overflow-safe checked accounting.
- [ ] 2.3 Enforce terminal detail rules: failed requires a non-whitespace message; completed and cancelled require no message; do not carry exceptions, stack traces, cancellation tokens/reasons, progress/log/activity state, protocol fields, or mutable history.
- [ ] 2.4 Document in the Core API that processed is terminally classified assets, updated is successful Immich writes, skipped and failed are per-asset dispositions, empty runs have zero counts, and a fatal run outcome does not increment the per-asset failed count.

## 3. Focused contract verification

- [ ] 3.1 Add `ProcessingRunModelsTests` covering immutable preservation of non-empty IDs and every defined trigger, plus rejection of empty IDs and undefined triggers.
- [ ] 3.2 Cover completed empty and non-empty results, cancelled and failed partial results, handled asset failures with a completed outcome, exact originating-request retention, and every immutable field value.
- [ ] 3.3 Cover rejection of non-zero-offset timestamps, reversed timestamps, every negative count position, aggregate mismatch and overflow, undefined outcome, missing/blank failed detail, and detail supplied to completed or cancelled outcomes.
- [ ] 3.4 Verify the public model surface has no writable setters and no worker-protocol/serialization members or attributes.

## 4. Compatibility and acceptance

- [ ] 4.1 Run `dotnet test --project tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj --filter "FullyQualifiedName~ProcessingRunModelsTests"`.
- [ ] 4.2 Run the existing Phase 1 processing lifecycle/state tests and confirm the model-only change does not alter `ProcessingBackgroundService`, `ProcessingState`, Web UI processed-counter semantics, scheduling, admission, cancellation, persistence, or logs.
- [ ] 4.3 Run `npm run test` with the repository's default Integration/Performance exclusions.
- [ ] 4.4 Review the implementation diff before completion: only the new Core contract and focused tests may be added in block 7; reporter/executor/coordinator wiring, mutable UI state, and Phase 3 wire serialization remain deferred.
