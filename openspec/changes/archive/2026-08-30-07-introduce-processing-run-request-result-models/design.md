## Context

See [proposal.md](proposal.md) for motivation and [specs/processing-run-models/spec.md](specs/processing-run-models/spec.md) for the behavioral contract. Today `ProcessingBackgroundService` owns accepted-run execution and directly mutates `ProcessingState`. The state uses `long` counters and UTC `DateTime` values, but its “processed” counter is incremented only after a successful Immich write and its error counter includes both handled asset errors and a fatal pass error. Blocks 8–14 need a request/result boundary that does not inherit those UI-specific meanings; Phase 3 later defines the actual worker protocol.

## Goals / Non-Goals

**Goals:**
- Put a small, dependency-light, immutable run contract where Web, a later executor, and a later worker host can reference it without a Web-state dependency.
- Make invalid identity, enum, time, accounting, and failure states unrepresentable through public construction.
- Give downstream reporter/executor tests precise, deterministic counter and terminal semantics.

**Non-Goals:**
- Wiring the models into `ProcessingBackgroundService`, changing its current control flow, or returning a result from it in this block.
- Adding reporter payloads, progress totals, activity/log models, a coordinator, mutable current-run state, UI fields, persistence, or run history.
- Defining protocol envelopes, versions, event/job sequence numbers, JSON property names, converters, framing, line limits, stdin/stdout behavior, or exit codes.

## Decisions

### Place transport-neutral models in Core

Add the request, result, and their enums under `ImmichReverseGeo.Core.Models`. Core already owns dependency-light records shared by Web and processing services, and the existing test project references it. Keeping these types out of Web avoids making the block-11 executor or future worker host depend on mutable Blazor state.

Alternative considered: place them beside `ProcessingBackgroundService`. Rejected because that preserves the exact Web dependency the extraction is intended to remove.

### Use closed validated value types

Use `ProcessingRunRequest` and `ProcessingRunResult` as sealed immutable records (or equivalently immutable value objects) with get-only data and validating public construction. Use `Guid` rather than a string for `RunId`; reject `Guid.Empty`. Use `ProcessingRunTrigger` with exactly `Manual`, `Scheduled`, and `RunOnce`, and `ProcessingRunOutcome` with exactly `Completed`, `Cancelled`, and `Failed`. Validate enum membership because a .NET enum can still be constructed from an undefined numeric value.

The coordinator introduced in block 13 will create a fresh `Guid` only after admission succeeds and will preserve it across execution and reporting. Block 7 provides and tests the validated value contract but deliberately does not move admission or generate requests in the current service.

Alternative considered: strings for identifiers and trigger/outcome labels. Rejected because blank identity and spelling drift would weaken correlation before any serialization boundary exists. Alternative considered: add `Worker` as a trigger. Rejected because worker is an execution backend; the invocation remains manual, scheduled, or run-once.

### Retain the originating request as result identity

`ProcessingRunResult` carries a `ProcessingRunRequest Request` rather than independently accepting another ID and trigger. Its remaining fields are `DateTimeOffset StartedAtUtc`, `DateTimeOffset EndedAtUtc`, four `long` counters, `ProcessingRunOutcome Outcome`, and `string? FailureMessage`. Retaining the immutable request prevents result identity and trigger from diverging.

Alternative considered: duplicate `RunId` and `Trigger` on the result. Rejected because public construction could pair a run ID with the wrong trigger. Protocol flattening, if desired, belongs to Phase 3 serializers rather than this domain shape.

### Define execution timestamps independently of UI publication timing

`StartedAtUtc` denotes entry into execution for an accepted request and is captured before eligibility counting; `EndedAtUtc` denotes classification of the one terminal outcome after cleanup-relevant execution has stopped. This gives count-query cancellation or failure a bounded result even if the existing UI has not yet called `StartRun(total)`. Block 7 does not project either value into `ProcessingState`, so the Phase 1 UI timestamp behavior remains unchanged.

Use `DateTimeOffset` and require `Offset == TimeSpan.Zero` for both values; reject rather than silently normalize non-UTC input. Require end greater than or equal to start. A later executor should inject or locally centralize a clock only if deterministic execution tests require it; no clock abstraction is introduced with these data-only models.

Alternative considered: `DateTime` with `Kind == Utc`. Rejected because `DateTimeOffset` carries an explicit offset and avoids unspecified-kind ambiguity. Alternative considered: a request-created timestamp. Rejected because request admission and execution start are distinct and no current requirement consumes queue duration.

### Make processed an aggregate and updated the successful-write subset

All counters are non-negative `long` values. `UpdatedCount` counts successful returns from the existing Immich location write. `SkippedCount` counts actively evaluated assets that reach an existing deliberate no-write disposition. `FailedCount` counts handled per-asset exceptions. `ProcessedCount` is their aggregate and must equal the checked sum of the other three; validation must reject arithmetic overflow as well as mismatches.

An asset loaded only as a batch member has not yet been processed. An ID ignored from the pre-existing skipped repository and an asset interrupted by cancellation before a terminal disposition contribute zero. Each actively handled asset contributes to exactly one of updated, skipped, or failed. Partial cancelled/failed results retain only completed dispositions.

The current Web `ProcessedThisRun` counter continues to increment only on successful writes. When block 9 later adapts events to state, compatibility therefore maps `UpdatedCount` to that UI counter; it must not relabel aggregate `ProcessedCount` in the existing UI.

Alternative considered: preserve the current “processed means updated” naming in the new result. Rejected because downstream executors need to distinguish total terminally handled assets from actual database modifications. Alternative considered: include a pass-level fatal exception in `FailedCount`. Rejected because it breaks asset accounting and is represented by the terminal outcome instead.

### Represent run termination without transporting exceptions

`Completed` includes zero-work passes and passes that handled one or more per-asset failures. `Cancelled` applies only when execution terminates under the active requested cancellation token and carries no failure message. `Failed` applies to a pass-level unexpected failure and requires a non-whitespace `FailureMessage`. Other outcomes reject any failure message. An unrelated `OperationCanceledException` remains a failure under the cancellation rules established by block 6.

The result does not carry `Exception`, stack trace, cancellation token, or cancellation reason. Detailed diagnostics remain reporter/log concerns; the failure message is the minimal terminal detail required by later failure reporting. This keeps the domain result usable in-process without prematurely declaring safe wire serialization or exposure rules.

Alternative considered: use `FailedCount > 0` to infer a failed run. Rejected because per-asset errors are currently recoverable and do not terminate the pass. Alternative considered: put arbitrary exception objects on the result. Rejected because they are mutable, non-portable, and may contain unsafe implementation detail.

## Risks / Trade-offs

- [“Processed” now has a broader domain meaning than the current UI label] → Document the distinction at every artifact layer and regression-test that current UI state remains unchanged; later adapters map successful writes from `UpdatedCount`.
- [Strict constructors make hand-built test data more verbose] → Provide direct, deterministic construction with clear argument validation rather than permissive objects that can violate lifecycle invariants.
- [A fatal failure before eligibility counting has no current UI start timestamp] → Keep result timing separate from current state publication; block 8/9 must preserve characterized UI timing while consuming the result.
- [A plain failure message can contain sensitive provider detail if copied blindly] → Treat it as diagnostic contract data, never as automatic public UI or wire output; sanitization/exposure policy belongs to the reporter/protocol changes that consume it.
- [Later protocol payloads may choose a different flattened shape] → Keep serialization annotations out of Core and map explicitly in Phase 3.

## Migration Plan

1. Add and test the trigger and outcome enums plus the validating immutable request.
2. Add the validating result with request identity, timestamp, accounting, and failure-detail invariants.
3. Add focused model tests for every valid trigger/outcome and each rejected invariant, including checked aggregate overflow.
4. Run the existing processing lifecycle/state coverage and the normal default-exclusion suite without wiring the new models into runtime services.
5. Roll back by removing only the new Core model and focused test files; there is no stored data, configuration, public API, UI, or wire migration.
