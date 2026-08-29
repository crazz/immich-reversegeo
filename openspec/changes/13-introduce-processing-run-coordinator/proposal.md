## Why

Run ownership is still split across manual and scheduled paths in `ProcessingBackgroundService`, leaving cancellation, pending projection, dispatch faults, and cleanup vulnerable to different races. After blocks 7–12 establish run identity, reporting, execution, and the scheduler start seam, one local control-plane owner is needed before execution can later move behind a worker launcher.

## What Changes

- Introduce one process-local Web processing-run coordinator that owns atomic idle/active/stopping admission for `Manual` and `Scheduled` triggers only. `RunOnce` remains a valid block-7 request/executor trigger for a separate run-once deployment invoker and is not exposed through this Web coordinator, Dashboard surface, or scheduler contract.
- Create a non-empty run ID and immutable trigger-specific request only after admission, publish the active cancellation handle before UI pending notification, call `MarkPending()`, arm the exact singleton reporter adapter, and then dispatch the block-11 executor in-process.
- Expose prompt accepted/already-running/stopping admission to Dashboard callers, while implementing block 12's exact asynchronous scheduled boundary: reject immediately as already running or await the accepted run through terminal cleanup and return its accepted-after-terminal outcome.
- Intentionally normalize cancellation so the existing Dashboard Cancel command cancels whichever local run is active, including a scheduled run; idle cancellation remains harmless.
- Observe the owned execution task, keep domain terminal reporting in the executor/session, and always detach/dispose the exact active handle after completion, cancellation, ordinary failure, setup/reporting infrastructure failure, or shutdown so a later trigger can be admitted.
- Close local admission when Web-host shutdown begins, cancel and await the active in-process run within host shutdown, and coexist with the block-12 hosted scheduler without taking ownership of cron calculation.
- Preserve one local run only. PostgreSQL cross-process exclusion remains block 31; worker protocol/process launch, pipeline behavior, and worker shutdown escalation remain later blocks.

## Capabilities

### New Capabilities
- `processing-run-coordination`: coordinates local admission, immutable run identity, adapter preparation, in-process dispatch, cancellation, shutdown quiescence, and exact terminal cleanup for one processing run.

### Modified Capabilities
- None.

## Impact

- Replaces `ProcessingBackgroundService` ownership of `_runLock`, `_runCts`, manual dispatch, and terminal release after blocks 11–12 have been applied.
- `Dashboard.razor` moves Run Now and Cancel calls from the hosted scheduler to a narrow coordinator contract; the scheduler uses its block-12 execution-start contract.
- `Program.cs` must factory-alias the coordinator's concrete, Dashboard, scheduler-start, and hosted-lifecycle registrations to one reference-identical singleton instance while retaining a separate reference-identical concrete/hosted `ProcessingBackgroundService` scheduler instance; the coordinator and scheduler are distinct.
- Depends on finalized blocks 7–12. Apply block 12 first and re-read its concrete start API, adapter, registrations, and tests before implementing rather than inventing a parallel seam.
- Adds no package, persisted setting, database schema, protocol, process, pipeline, cron, or cross-process locking behavior.
