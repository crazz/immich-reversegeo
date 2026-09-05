## 1. Reconcile Prerequisite Ownership

- [x] 1.1 Re-read the applied block-13 active-handle/Stop API, block-17 cancel codec/validator, block-22 request lease and stdin finality, block-23 exit facts, block-25 session/process adapter, block-26 fixture, and block-27 bridge; stop rather than inventing parallel identities, writers, tokens, observations, or cleanup paths.
- [x] 1.2 Record scope guards that block 28 adds no block-29 admission/shutdown-timeout composition, block-30 classification/UI failure projection, persisted grace or deployment-mode setting, protocol acknowledgement/retry, or generalized job model.

## 2. Define Cancellation Policy and Raw Facts

- [x] 2.1 Add one immutable internal cancellation policy with a fixed positive grace of 10 seconds, injected through the existing session/launcher composition with `TimeProvider` and no `AppConfig` or Settings-page ownership.
- [x] 2.2 Add typed raw cancellation facts for first Stop/deadline, request acceptance, cancel write/flush, exit races, grace expiry, tree-kill outcome, and eventual block-25 completion without assigning block-30 classifications.
- [x] 2.3 Extend the process abstraction narrowly for one exact-session `Kill(entireProcessTree: true)` attempt and safe-normalized already-exited/platform-failure outcomes without exposing `Process` or reacquiring by PID.

## 3. Implement Exact-Session Idempotent Stop

- [x] 3.1 Extend the coordinator's identity-checked active handle with one stopping transition and one shared asynchronous cancellation operation; make idle Stop a no-op and make concurrent/repeated callers join the same operation.
- [x] 3.2 Capture the exact session, run ID, and process generation once so delayed command, deadline, completion, or cleanup work cannot target or detach a replacement run.
- [x] 3.3 Preserve prompt Dashboard semantics: Stop returns after accepting/joining the current operation, displays stopping, and does not claim terminal cancellation before worker/process evidence settles.

## 4. Send One Accepted-Only Cancel Command

- [x] 4.1 Add an early-Stop latch that starts grace immediately but waits for the same session's successful execute write/flush before control delivery; suppress cancel for failed/nonaccepted/exited/closed/replaced sessions.
- [x] 4.2 Serialize exactly one canonical correlated next-sequence cancel frame through the session's sole stdin writer, write it completely, flush it, and expose write/flush/exit races as raw facts without closing stdin or fabricating acceptance.
- [x] 4.3 Keep block-25 wait-token behavior wait-only: caller cancellation may stop awaiting the shared Stop result but cannot cancel the owned deadline, pumps, process, or cancellation operation.

## 5. Link Cooperative Worker Cancellation

- [x] 5.1 Connect block-22's accepted request-lease cancellation source and existing host-stopping linkage to the exact executor token without replacing the immutable request or adding a second execution path.
- [x] 5.2 Verify cancel before executor entry is already requested at entry, cancel during execution reaches the same token, and cancel after terminal is effect-idempotent while terminal ownership remains with the worker reporter/emitter.
- [x] 5.3 Document and test the synchronous/native limitation: cancellation cannot interrupt work that does not observe the token, so no cooperative terminal is assumed before escalation.

## 6. Enforce Grace, Escalation, Drain, and Disposal

- [x] 6.1 Start one fake-clock-testable deadline at first accepted Stop; let actual process exit suppress escalation, but do not treat a terminal frame alone as process finality.
- [x] 6.2 At grace expiry, recheck the exact process and attempt whole-process-tree kill once when still alive; record already-exited, accepted-kill, and safe platform-failure facts with no blind retry, fallback PID lookup, or false stopped state.
- [x] 6.3 After cooperative exit or accepted kill, await the existing raw exit plus stdout/stderr finality, preserve trailing terminal/diagnostic observations, and converge Stop/completion/disposal on exactly-once resource release.
- [x] 6.4 If tree kill fails while the process remains alive, retain session ownership and coordinator stopping state until later exit or block-29-owned host policy acts; do not close handles or report cleanup as complete.

## 7. Add Deterministic Unit and Fixture Tests

- [x] 7.1 Add gated fake-session tests for idle Stop, concurrent/repeated callers, Stop during readiness/execute delivery, accepted-only one-frame write/flush, stdin failure, exit before/during cancel, terminal-before-exit, and stale-operation rejection after retrigger.
- [x] 7.2 Add fake-`TimeProvider` tests for the 10-second default, one deadline from first Stop, caller wait cancellation, exit-before-deadline, deadline/exit race, one tree-kill call, kill-platform failure, and delayed exit after failure.
- [x] 7.3 Add disposal/drain tests proving successful kill still awaits process exit and both streams, trailing stdout/stderr is retained, every timer/source/stream/process/session resource is released once, and no task or callback survives settled cleanup.
- [x] 7.4 Reuse block 26 cooperative-cancel and unresponsive modes with protocol-marker coordination: assert cancel/terminal/130/no-kill for cooperative behavior and cancel-observed/grace/tree-kill/exit/drains/no-orphan for unresponsive behavior, using watchdogs only for failure cleanup.
- [x] 7.5 Add classification-boundary assertions that cancellation preserves terminal, raw exit, stdin/protocol/sink, kill, stream-finality, and stderr facts without producing block-30 crash/missing-terminal/mismatch decisions or automatic retry.

## 8. Validate

- [x] 8.1 Run focused coordinator/session/worker-input/fixture tests and verify no timing sleeps or polling coordinate expected behavior.
- [x] 8.2 Run `npm run test`, `openspec validate 28-add-graceful-worker-cancellation --strict`, and `openspec status --change 28-add-graceful-worker-cancellation`.
- [x] 8.3 Review the final diff to confirm only block 28 planning/implementation scope changed and block 29 plus later mode-setting ownership remain untouched.

## Audit Reconciliation

The one bounded escalation decision uses exactly one internal, exact-session 10-second deadline measured through `TimeProvider`; it is not configurable and creates no current or future public setting. After that deadline, raw process exit suppresses one tree-kill attempt; a live owned process receives at most one attempt. A terminal frame alone never settles process ownership.


## Implementation verification

- Debug solution build: 0 warnings/errors. Focused and related coordinator, session, fixture, bridge, worker host and input tests: 506/506. Full suite: 1289/1289 using `LC_ALL=en_US.UTF-8` with the repository default category exclusions.
- Existing worker request-lease and host-stopping token linkage is preserved and covered by the related tests. No second execution path or backend switch was added.
- Deterministic gates cover blocking synchronous and asynchronous stdin, callback finality, explicit kill versus physical-exit ordering, write/flush failures, and both legacy/prompt Stop orderings. No sleeps or polling coordinate expected behavior.
- Docs build and strict change validation passed. The existing temporary non-escalating launcher disposal requirement is explicitly renamed and modified by this change’s launcher delta.
- Independent Serena/Brooks review found no remaining concrete defects after corrections; cross-platform build/publish fixture checks include Change28 in CI.
- Release published fixture/cancellation tests: 89/89.
