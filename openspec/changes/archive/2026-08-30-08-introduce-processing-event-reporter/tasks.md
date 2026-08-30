## 1. Prerequisite and Event Surface

- [x] 1.1 Confirm block 7's request/result source models, UTC rules, accounting, and outcomes exist; stop and apply block 7 rather than recreating them if absent.
- [x] 1.2 Add dependency-light immutable events for run started, eligibility determined, progress changed, activity started/ended, typed log emitted, and run finished, with request identity on every event.
- [x] 1.3 Validate request/result identity, UTC execution start, eligibility/count invariants, defined levels, non-empty activity IDs, and non-blank labels/messages while excluding state, exception/stack/token payloads and protocol metadata.

## 2. Run-Scoped Reporter Session

- [x] 2.1 Add asynchronous `IProcessingEventReporter` run-session creation and a session API that emits start at execution entry, allows eligibility-known or pre-count terminal paths, and enforces one final terminal event.
- [x] 2.2 Serialize each session's concurrent calls into a linearizable accepted order, isolate multiple sessions, define acceptance before reporter-owned bounded capacity, and await backpressure without dropping, coalescing, fire-and-forget work, or unbounded producer queues.
- [x] 2.3 Commit updated/skipped/failed dispositions and monotonic snapshots under the session gate; publish irreversible dispositions through a non-cancelled path so post-write/post-disposition cancellation cannot erase terminal accounting.
- [x] 2.4 Make reporter fault/cancellation before acceptance break the session, propagate the infrastructure failure, clean activity state locally, and prohibit recursive cleanup or terminal attempts through the broken reporter.

## 3. Activity and Support Implementations

- [x] 3.1 Add asynchronous activity scopes with unique IDs, start-before-return, exactly one non-cancelled end, equal-label independence, and idempotent disposal.
- [x] 3.2 Make session finish close every open activity before terminal acceptance and mark scopes locally closed so late disposal cannot emit after the final event.
- [x] 3.3 Add a stateless thread-safe no-op reporter and thread-safe recording/fault-injection test support with deterministic pre-acceptance gates and immutable per-session snapshots.

## 4. Contract Verification

- [x] 4.1 Test valid/invalid payloads and sequences, including successful eligibility, completed empty run, rejection of pre-eligibility progress/log/activity-start/activity-end operations, count-query cancellation/failure with the exact start-to-finish sequence and no intervening log or activity event, duplicate eligibility/finish, mismatched requests, and events after finish.
- [x] 4.2 Test coherent updated/skipped/failed accounting, completed runs with handled failures, fatal failure without per-asset increment, and cancellation immediately after write, while waiting for the session gate, and during reporter backpressure.
- [x] 4.3 Test linearizable concurrent ordering, multi-session isolation, cancellation before versus after acceptance, awaited bounded backpressure, and no event loss/coalescing.
- [x] 4.4 Test equal-label activities, unwind cleanup, finish-owned closure, duplicate/late disposal, and reporter faults during start, eligibility, progress, activity start/end, log, and terminal acceptance.
- [x] 4.5 Test diagnostic mapping boundaries: pre-write Trace resolution detail, current UI Warning/Error sites, no new event for `ILogger`-only no-city diagnostics, plain message content, and absence of exception/stack/protocol payloads.

## 5. Boundary and Compatibility Verification

- [x] 5.1 Confirm the diff changes only new event/reporter/session/support contract and focused test files; do not edit or rewire `ProcessingBackgroundService`, `ProcessingState`, `Program.cs`, Razor, or resolver/cache progress.
- [x] 5.2 Confirm follow-on mapping defers UI `StartRun(total)` until eligibility determined, maps `UpdatedCount` to legacy processed, keeps fatal failure outside domain `FailedCount`, derives lifecycle/summary logs in block 9, and leaves pending/run-lock/CTS ownership outside the reporter.
- [x] 5.3 During apply, run focused reporter tests, Phase 1 lifecycle/state regressions, and `npm run test` with default exclusions; record unrelated failures without broadening this block.
- [x] 5.4 Verify no block-9 adapter, block-10 resolver migration, block-11 executor extraction, Phase-3 wire concern, or block-65 progress coalescing was introduced.
