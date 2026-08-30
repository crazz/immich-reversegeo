## 1. Verify Immutable Prerequisites and Baseline

- [x] 1.1 Verify block-7 request/result models and tests and block-8 event/reporter/session types and tests exist in source; stop without recreating them if either prerequisite is absent.
- [x] 1.2 Re-read blocks 3, 7, and 8 and map each current main-pass state call to the finalized session API, explicitly separating startup/schedule/contention/pending and resolver/cache progress calls that remain direct.
- [x] 1.3 Extend deterministic baseline coverage for pre-eligibility pending/cancel/failure retention, terminal log ordering, and exact current message text before replacing main-pass mutations.

## 2. Add Narrow State Projection Operations

- [x] 2.1 Add an internal absolute progress projection that applies Updated to legacy processed, Skipped to skipped, and Failed to ordinary errors as one coherent snapshot while preserving the existing public increment methods.
- [x] 2.2 Add an internal error-diagnostic projection that sets `LastError`, timestamps and appends exactly one `[ERROR]` line, and does not increment the error counter.
- [x] 2.3 Add focused state tests proving projection reset/retention, absolute count replacement, fatal increment compatibility, log cap/order/prefixes, completion timing, and at-least-one synchronous `OnChanged` notification without exact callback counts.

## 3. Implement the Singleton State Adapter

- [x] 3.1 Add the Web-layer block-8 reporter adapter with one projection gate, exact armed request identity, terminal state, last progress snapshot, and per-run activity-ID scope ownership.
- [x] 3.2 Add the admission arm operation and reject overlapping arm ownership while consuming unarmed, mismatched, stale, post-terminal, and duplicate-terminal events without any ProcessingState mutation.
- [x] 3.3 Project `RunStarted` as correlation-only and `EligibilityDetermined` as `StartRun(total)` plus exactly one existing zero/nonzero start line.
- [x] 3.4 Project absolute progress, typed Trace/Information/Warning/Error logs, and `LastError` without mapping aggregate `ProcessedCount` or double-counting handled failures.
- [x] 3.5 Track each (RunId, ActivityId) start to its exact `BeginActivity(label)` scope; make duplicate/unknown ends no-ops and make terminal cleanup idempotent against block-8 finish-owned closure and late disposal.
- [x] 3.6 Project Completed/Cancelled/Failed terminal outcomes so block-8 finish-owned activity ends are projected first; then project the outcome line, idempotent defensive cleanup, completion/inactive state, summary, and ownership release in order, including one legacy fatal UI error outside domain `FailedCount`.

## 4. Register Lifetime and Ownership Correctly

- [x] 4.1 Register the adapter as one singleton and bind `IProcessingEventReporter` through a factory to that exact instance while retaining the singleton `ProcessingState` and concrete/hosted `ProcessingBackgroundService` registrations.
- [x] 4.2 Add a DI composition test that requires reference identity between the concrete adapter and reporter interface and proves singleton state/session-correlation ownership is not duplicated.

## 5. Route the First Production Pass

- [x] 5.1 After each manual or scheduled lock win and immediate `MarkPending()`, create the correct trigger request, arm the adapter, and pass the request into the existing pass; rejected duplicate invocations create no request/session.
- [x] 5.2 Open and await one run session at pass execution entry before eligibility count, report eligibility only after count success, and finish through the finalized block-8 API for completed, active-token cancelled, and failed results.
- [x] 5.3 Replace direct main-pass start, batch/asset UI-log, updated, skipped, handled-error, cancellation, fatal, completion, and summary mutations with awaited session operations, with no parallel direct duplicate.
- [x] 5.4 Report Updated only after successful write, Skipped only for actively evaluated no-write branches, Failed only for handled per-asset exceptions, and nothing for previously suppressed or cancellation-interrupted assets; preserve the no-city logger-only distinction with a pure decision seam and an exact one-Skipped/no-processing-log proof.
- [x] 5.5 Replace the batch message's ProcessingState counter read with a run-local successful-write count that preserves its current text without making the routed main pass read UI state.
- [x] 5.6 Keep startup, next-schedule, scheduled-contention, `MarkPending()`, lock/CTS ownership, and the nested production `ProcessingResolutionProgress -> ProcessingState` bridge unchanged; assert resolver/cache progress is not also event-reported in block 9.

## 6. Add Deterministic Adapter and Routing Verification

- [x] 6.1 Test manual and scheduled pending before gated eligibility, eligibility start/reset/total, duplicate rejection, and count cancellation/failure with no fabricated start timestamp or total.
- [x] 6.2 Test mixed progress where aggregate processed differs from Updated, previously suppressed exclusion, warning-producing skips, logger-only no-city skip, handled failures, fatal-after-handled accounting, and newest `LastError`.
- [x] 6.3 Test completed-empty, completed-nonempty, cancelled, and failed terminal transitional snapshots: activity removal before outcome line, outcome line before inactive/completion, and completion before summary, plus exact lifecycle/fatal/cancellation/summary suffixes and retained pre-eligibility snapshot behavior. Remediation exact-order coverage explicitly exercises Completed, Cancelled, and Failed.
- [x] 6.4 Test more than 100 uniquely identified lines for exact newest-100 insertion order, one-time severity prefixes, and Trace resolution detail before a failing write.
- [x] 6.5 Test equal-label distinct activity IDs, a sole surviving distinct label, out-of-order ends, duplicate/unknown ends, finish-owned closure, terminal-before-late-end, and old-run same-label isolation.
- [x] 6.6 Test wrong-run events during pending/active state, old-run events after a later arm, progress/log/activity after terminal, and duplicate terminal, asserting no values, logs, timestamps, activity, or notifications change.
- [x] 6.7 Use gates rather than sleeps to prove cancellation after a successful write or other irreversible disposition retains its mapped progress in the cancelled result and terminal summary.
- [x] 6.8 Test at-least-one `OnChanged` after every accepted observable projection (eligibility, progress, every log level, activity start/end, and terminal) and no notification for ignored stale/cross-run events; do not assert exact callback multiplicity or Blazor render scheduling.

## 7. Validate Scope and Regression Behavior

- [x] 7.1 Run focused `ProcessingState`, adapter, and `ProcessingBackgroundService` MSTests with the repository's Microsoft.Testing.Platform command form.
- [x] 7.2 Run `npm run test` with default exclusions and record only failures attributable to this change.
- [x] 7.3 Run `openspec validate 09-adapt-processing-state-to-events --strict` and require success.
- [x] 7.4 Review the diff to confirm no Razor, resolver/cache implementation, Lookup, scheduler semantics, persistence, protocol/serialization, Phase 3, or other numbered-block changes were introduced.

## 8. Remediate Final Review Infrastructure-Fault Findings

- [x] 8.1 Add an internal exact-request-correlated, idempotent reporter abandonment operation that disposes local activity scopes, releases only the matching arm, and commits a non-running fatal failure snapshot without using the broken session.
- [x] 8.2 Invoke abandonment for synchronous reporter/session faults at open/start, eligibility, progress, log, and terminal production publication boundaries, while keeping activity cleanup recoverable, preserving attempted irreversible dispositions, and preventing recursive reporting.
- [x] 8.3 Add deterministic real-adapter tests for injected projection and synchronous state-notification faults, stale-request rejection, activity cleanup, fatal snapshot, prior-run isolation, irreversible Updated/Skipped/Failed accounting, later manual/scheduled admission, admission-lock cleanup, and handled-plus-terminal failure accounting.
- [x] 8.4 Run focused ProcessingState, ProcessingStateEventReporter, and ProcessingBackgroundService routing tests; 43 tests passed with zero failures.
