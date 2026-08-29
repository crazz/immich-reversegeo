## 1. Reconcile landed contracts and transition

- [ ] 1.1 Re-read applied blocks 47–49 and bind to their exact descriptor/resource metadata, one-JobId rule, closed admission result, session/classifier finality, cancellation, Lookup controller, and temporary-gate symbols; stop rather than introduce parallel DTOs, identities, or wire contracts if they differ.
- [ ] 1.2 Record the corrected sequence in implementation notes/tests: block 49 is already applied with a temporary lookup-only gate; block 50 replaces it and is not its prerequisite; block 51 remains future-owned and untouched.
- [ ] 1.3 Characterize existing `ProcessingBackgroundService` manual/scheduled admission, `MarkPending()` timing, pending/coalesced schedule behavior, shutdown, and PostgreSQL advisory-lock outcome before replacing `_runLock` as the heavy admission authority.

## 2. Coordinator contracts and validation

- [ ] 2.1 Define the typed process-local coordinator contract, internal exact owner record, safe identity-free `Busy(snapshot)`, exact `Admitted(handle)` / `Busy(snapshot)` / `Unavailable(reason)` union, owner handle, monotonic lifecycle, and read-only diagnostic observer surface without untyped payloads, PID, or JobId.
- [ ] 2.2 Consume block 47 descriptor facts for exact kind, friendly category, heavy/geodata flags, cancellability, origin, and resource class; startup-reject duplicate kinds, unknown classes, or inconsistent metadata.
- [ ] 2.3 Enforce one canonical JobId with ProcessAssets RunId equality and no reservation/attempt identity; make wrong-kind, wrong-identity, stale, duplicate, and non-owner updates/releases unable to clear an active owner.
- [ ] 2.4 Implement atomic first-successful-request-wins admission with no held lock across async work, queue, retry, preemption, priority promotion, fairness, or starvation promise; assert Busy/Unavailable start no process and fabricate no exit.

## 3. Lifecycle, cancellation, and status ownership

- [ ] 3.1 Implement active lifecycle transitions for admitted/starting/running/stopping/finalizing with admission/start timestamps and nullable PID set only after successful process creation.
- [ ] 3.2 Bind one session or startup finalizer to the admitted handle; keep normal cancel with the owner and existing exact-JobId grace/kill path, and deny cancellation/release capability to Busy/Unavailable callers.
- [ ] 3.3 Release exactly once only after controller classification and, for launched processes, exit plus stdout/stderr/protocol/bridge drain; cover launch failure, terminal, cancellation, crash, protocol/transport failure, forced stop, disposal, and terminal-before-EOF races.
- [ ] 3.4 Add a generic read-only coordinator diagnostic projection and notifications containing only safe identity-free arbitration facts; prove block 44's card and ProcessingState stay ProcessAssets-only, Lookup/cache pages own their state, and PID, JobId, results, logs, activities, counts, errors, and cancellation controls do not leak into generic UI diagnostics.

## 4. Replace block 49's temporary Lookup gate

- [ ] 4.1 Implement the shared coordinator adapter behind block 49's existing admission/launch seam using its exact descriptor and handle types where compatible.
- [ ] 4.2 Delete the temporary lookup-only gate implementation, DI registration, and gate-specific tests; assert there is no nested/dual gate and leave `Lookup.razor`, operation generation, and busy/unavailable presentation unchanged.
- [ ] 4.3 Rerun block 49 controller/page tests for admitted, Busy, Unavailable, startup failure, completion, cancellation, crash/protocol failure, circuit disposal, exact-once release, stale callbacks, and subsequent reuse against the shared coordinator.
- [ ] 4.4 Add cross-kind Lookup tests proving manual/scheduled ProcessAssets contention returns friendly safe owner metadata and starts at most one worker.

## 5. Adapt manual and scheduled ProcessAssets

- [ ] 5.1 Route manual ProcessAssets through the shared slot, preserve one RunId/JobId and detector bypass, call `ProcessingState.MarkPending()` immediately after successful admission and before asynchronous launch, and return structured Busy without starting a duplicate worker.
- [ ] 5.2 Supersede the landed block-35/36/39 admission-first scheduled ordering: run the lightweight detector before JobId creation, adapter arming, `MarkPending()`, and coordinator admission; prove no-work creates no identity/state/admission/backend work, while detector-positive/admission-lost follows existing skipped/coalesced trigger behavior with no coordinator queue. Preserve manual detector bypass.
- [ ] 5.3 Route scheduled ProcessAssets through the same slot; prove it cannot interrupt manual/Lookup work and, once admitted, cannot be preempted by a later interactive request.
- [ ] 5.4 Remove `_runLock` as an independent heavy admission path (retaining only narrowly proven trigger serialization if required), and verify no alternate processing launch bypasses the coordinator.
- [ ] 5.5 Preserve the in-worker PostgreSQL advisory lock and exact exit-3 meaning; verify advisory busy, completion, cancellation, and failure all finalize ProcessingState and release local admission once.

## 6. Shutdown and composition fencing

- [ ] 6.1 Register exactly one coordinator singleton in Standard and Web-only Web roots and no interactive coordinator/worker-launch surface in run-once or internal-worker roots beyond their established roles.
- [ ] 6.2 Implement a linearizable permanent shutdown fence before stop: later admissions return Unavailable, the exact owner-bound stop operation runs once, repeated shutdown joins it, and cleanup is awaited before release.
- [ ] 6.3 Add deterministic admit-versus-shutdown, shutdown-before-launch, shutdown-during-start/run/drain, cancel-versus-shutdown, repeated shutdown, and stale-release-after-shutdown race tests with no orphan or second kill.
- [ ] 6.4 Add negative composition tests proving descriptor/status resolution starts no worker or heavy geodata and observers cannot acquire, cancel, or release jobs.

## 7. Future compatibility and strict verification

- [ ] 7.1 Add contract-only fake future CacheMutation descriptors proving they contend in the same exclusive heavy-geodata class and receive all three admission outcomes; do not implement or edit block 51 payloads, handlers, operations, pages, or tests.
- [ ] 7.2 Add a deterministic parallel stress suite/barrier test across ProcessAssets, CoordinateLookup, fake CacheMutation, release, cancellation, failure, and shutdown proving the maximum concurrent admitted/launched heavy count is one and later reuse succeeds.
- [ ] 7.3 Verify local Busy/Unavailable creates no process/exit and only ProcessAssets uses advisory-lock exit 3; add explicit multiple-Web-container boundary documentation/test assertions that process-local status does not claim distributed exclusion.
- [ ] 7.4 Run focused coordinator, processing lifecycle, Lookup controller, launcher/classifier, shutdown, and composition tests plus `npm run test`; run `openspec validate 50-add-web-worker-job-arbitration --strict`, inspect final status, and perform a block-50-only scope review proving block 51 and implementation outside this change were untouched.
