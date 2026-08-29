## 1. Verify sequencing and applied boundaries

- [ ] 1.1 Confirm blocks 33–35 are applied and their focused/default suites pass; consume their actual coordinator, selection, detector, and backend APIs without creating replacements.
- [ ] 1.2 Confirm the separately owned block-36 empty-schedule regression passes unchanged and Phase 4 process integration covers launch, protocol, cancellation, classification, cleanup, advisory-lock, and retrigger outcomes.
- [ ] 1.3 Verify the production publish/image stages the same application assembly, internal-worker role entry point, and required managed/native runtime dependencies used by the child launcher.

## 2. Change startup composition and default

- [ ] 2.1 Change only the ordinary internal composition default from InProcess to ChildWorker, preserving the immutable per-run selection and the explicit temporary InProcess composition seam.
- [ ] 2.2 Ensure no persisted setting, environment variable, CLI argument, endpoint, or UI binds or displays the temporary backend selection; document in code/maintainer-facing seams that emergency use requires rebuild/revert.
- [ ] 2.3 Validate the selected enum value, keyed registration, launcher/internal-role descriptor, application assembly, and required child files before host startup; emit a safe actionable failure and never substitute InProcess.
- [ ] 2.4 Keep startup validation free of run-scoped backend resolution, process launch, in-process executor construction, and worker-only geodata construction; do not activate or validate the unselected backend graph.

## 3. Preserve dispatch and lifecycle behavior

- [ ] 3.1 Prove admitted manual requests freeze and lazily resolve exactly one default child backend through the existing coordinator, reporter, cancellation, classifier/finalizer, and identity-checked cleanup path.
- [ ] 3.2 Prove scheduled requests resolve the default child backend only after a positive detector result, while local contention and detector zero/cancellation/failure resolve neither backend and preserve the established local state outcome.
- [ ] 3.3 Preserve exact request/run identity, pending and eligibility timing, counters, logs, activities, terminal outcome, cancellation targeting/escalation, stream drainage, scope disposal, matching-handle release, and retrigger behavior.
- [ ] 3.4 Assert child startup, handshake, protocol, projection, crash, cancellation-escalation, and cleanup failures produce one authoritative visible outcome with no retry, replacement child, replay, resubmission, or in-process fallback.

## 4. Verification and rollout gate

- [ ] 4.1 Add composition tests for default child and explicit in-process selection, invalid enum, missing child prerequisites, selected-only registration validation, singleton alias identity, and zero eager backend/geodata/process effects.
- [ ] 4.2 Add a trigger matrix covering manual and eligible scheduled default selection; explicit in-process override; Completed, Cancelled, and Failed state parity; duplicate rejection; Stop targeting; exact cleanup; and safe retrigger.
- [ ] 4.3 Retain or extend the empty scheduled matrix to prove one detector call and zero backend resolution, launcher/process activity, protocol events, in-process executor access, or geodata construction without editing block 36's owned artifacts.
- [ ] 4.4 Run focused coordinator/backend/scheduler/composition tests, npm run test, strict worker process-fixture/integration coverage, and production publish/image packaging verification.
- [ ] 4.5 Run openspec validate 37-make-child-worker-default-backend --strict and strict status review; confirm the diff changes only block 37 planning/implementation scope.
- [ ] 4.6 Record the block-38 gate: advance only when default child startup and every mandatory terminal path leave no orphan process, stream, activity, backend scope, or coordinator handle; otherwise revert or rebuild with the code-only internal seam and preserve failure evidence.

## Audit Reconciliation

Block 36 must be applied first. Preserve four distinct outcomes: authoritative committed worker terminals; local admission rejection (no child); advisory Busy (the canonical failed child terminal with no eligibility and four zero counts); and forced raw kill, which is transport evidence classified through block 30 and is not itself a terminal. No fallback, retry, replay, or in-process execution follows any of them.

