## 1. Reconcile prerequisites and add the scheduled gate

- [ ] 1.1 Re-read the applied block 11–13 and 25–33 APIs, registrations, and tests; confirm the exact scheduled start, coordinator active-handle, adapter abandonment/finalization, selected-backend, and child-finality seams, and do not edit block 34.
- [ ] 1.2 Add a narrow internal fakeable scheduled-only boolean gate contract and a production adapter that calls the current exact eligibility count once and returns count greater than zero using the unchanged predicate.
- [ ] 1.3 Register the gate without resolving AppConfig, skipped storage, either execution backend, the in-process executor, protocol/session services, or Overture/GADM/airport dependencies.

## 2. Order admission, pending state, and detection

- [ ] 2.1 Insert detection into the admitted scheduled operation after active handle/CTS publication, frozen backend selection, immediate `MarkPending()`, and exact-request adapter arming, but before run-scope creation or backend resolution.
- [ ] 2.2 Preserve local busy rejection before request/pending/detection/backend effects and retain the exact scheduled-contention control-plane log.
- [ ] 2.3 Pass the matching coordinator-owned cancellation token to the detector and retain local admission through detector completion, local finalization, or child cleanup.

## 3. Finalize predispatch outcomes locally

- [ ] 3.1 Add an identity-checked, idempotent adapter/coordinator operation for no-work, cancellation, and failure before backend dispatch; stale cleanup must not clear a newer request and no path may fabricate a worker event, worker result, or reporter session.
- [ ] 3.2 For no work, project eligibility zero and completed-zero state with reset counters/LastError, start/completion timestamps, the exact nothing-to-process line, then `Run complete. Processed=0 Skipped=0 Errors=0`; release the matching handle without backend resolution.
- [ ] 3.3 For active detector cancellation, use the established pre-eligibility `Run cancelled.` plus completion-summary presentation with no new error; for unexpected failure, use bounded safe detail and the established pre-eligibility fatal plus summary presentation; return to idle and release the matching handle in both cases.
- [ ] 3.4 Route finalizer/projection faults through the existing exact-request abandonment cleanup so admission and adapter ownership cannot strand.

## 4. Dispatch eligible scheduled work once

- [ ] 4.1 After a positive decision, lazily create the run scope and invoke the already-frozen child backend exactly once with the admitted Scheduled request, exact armed adapter/reporter, and coordinator token; add no trigger-specific selector or block-34 change.
- [ ] 4.2 Keep the scheduled start call awaiting authoritative terminal processing, exit/stdout/stderr finality, child cancellation/classification cleanup, scope disposal, and exact-handle release before schedule reevaluation.
- [ ] 4.3 Preserve outcomes for a worker authoritative count of zero and PostgreSQL advisory-lock Busy/exit 3; do not map either to local contention and add no in-process fallback, replacement child, retry, replay, or resubmission.

## 5. Preserve snapshot and race boundaries

- [ ] 5.1 Keep block 12's pinned Enabled/Cron schedule snapshot and block 33's immutable backend selection/launch descriptor boundaries unchanged; add no AppConfig, persisted setting, environment/CLI deployment mode, or UI option.
- [ ] 5.2 Keep detector output, eligibility totals, work sets, credentials, schedule data, and processing settings out of the worker request; leave the worker executor's one non-empty processing-config snapshot after its authoritative count.
- [ ] 5.3 Document in code/tests that the initial eligible path intentionally performs a Web exact count and a second authoritative worker exact count, and that either-direction eligibility races do not reopen, fall back, or retry an occurrence.

## 6. Verify block 35 behavior and scope

- [ ] 6.1 Add deterministic scheduler/coordinator tests with gate/backend/state fakes for local busy, ordering through pending/arm, positive child dispatch, local empty completion/log order, detector cancellation, detector safe failure, and accepted scheduling awaiting final cleanup.
- [ ] 6.2 Add focused child-path tests for detector-positive/worker-zero and worker advisory Busy/exit 3, asserting one child, authoritative worker outcome, zero fallback, and zero retry.
- [ ] 6.3 Use fail-on-resolution fakes to prove no-work/cancel/failure resolve no backend, launcher, protocol/session, in-process executor, skipped/config/batch, or geodata dependency; leave block 36's dedicated exactly-one-detector/zero-launch/zero-geodata/zero-protocol regression unchanged.
- [ ] 6.4 Run focused scheduler/coordinator/backend tests, relevant Phase 4 worker process-fixture integration coverage, and `npm run test` with default exclusions.
- [ ] 6.5 Run `openspec validate 35-execute-scheduled-runs-in-child-worker --strict` and `openspec status --change 35-execute-scheduled-runs-in-child-worker`; confirm the scope diff changes only numbered block 35 in `MASTERPLAN.md` plus change-35 planning/implementation files and does not touch block 34, block 36, or future blocks 57–58.

## Audit Reconciliation

Scope is scheduled accepted execution only and consumes the established detector/local-finalizer contracts and prerequisites; it neither changes manual routing nor makes child-worker the default. The default remains in-process until block 37. Its detector-zero local path emits no worker producer event or worker result, while a canonical advisory Busy remains a child terminal distinct from local admission rejection.

