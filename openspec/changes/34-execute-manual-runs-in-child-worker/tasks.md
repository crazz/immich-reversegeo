## 1. Reconcile prerequisites and selection setup

- [ ] 1.1 Verify blocks 13, 25, 27, 28, 30, and 33 are applied, then record the finalized coordinator manual start/Stop API, request/result identities, reporter arm/finalization receipt, keyed backend registration, child launcher/session, cancellation owner, classifier/finalizer, and fixture mode names used by this change.
- [ ] 1.2 Add or reuse an internal composition/test-host seam that explicitly selects `ChildWorker` while leaving the production default unchanged and exposing no AppConfig, settings, environment, CLI, endpoint, or Dashboard option.
- [ ] 1.3 Add constructor-counting and fail-on-resolution fakes proving child selection resolves no in-process backend/executor/geodata graph and rejected manual admission resolves neither backend.

## 2. Route the backend-agnostic manual controls

- [ ] 2.1 Bind Dashboard Run to the finalized coordinator-facing manual surface and Stop to active-run cancellation, removing any remaining direct in-process trigger dependency without exposing launcher, protocol, classifier, or selector details to the component.
- [ ] 2.2 Preserve the accepted order: atomic admission; one manual request/run ID/live CTS published; `MarkPending()`; matching reporter arm; frozen child selection and one run scope; then exactly one owned dispatch before the prompt call returns.
- [ ] 2.3 Preserve the page-local statistics refresh and `ProcessingState.OnChanged` rendering behavior while keeping manual AlreadyRunning contention silent.
- [ ] 2.4 Prove a duplicate or stopping rejection creates no run ID, CTS, pending mutation, reporter arm, backend scope/resolution, child start, run event, manual contention log, or in-process effect.

## 3. Complete one child backend lifecycle

- [ ] 3.1 Forward the exact coordinator request, run ID, armed reporter, cancellation token, and active-handle ownership into the finalized child backend composition; do not create a second request, reporter, terminal vocabulary, or release path.
- [ ] 3.2 Compose command resolution, one child start, immediate stdout/stderr/exit observation, validated readiness, one execute write/flush, typed event bridge, and normalized result using prerequisite owners rather than duplicating their policies.
- [ ] 3.3 Preserve event/state timing: ready is transport-only, run-started is correlation-only, eligibility starts/resets visible state, and validated absolute progress, activities, logs, and terminal data project once through the existing state adapter.
- [ ] 3.4 Preserve manual zero-work behavior by launching the child and projecting its zero eligibility, no-work message/status, zero counters, single completion summary, and idle terminal state; add no Web-side detector.

## 4. Integrate Stop, outcomes, and exact finality

- [ ] 4.1 Translate the coordinator token into the finalized exact-session cancellation owner so early Stop is latched, at most one cancel command is sent after execute flush, and coordinator ownership survives grace, tree containment, exit, drainage, and disposal.
- [ ] 4.2 Preserve cooperative Cancelled behavior and forced-kill behavior, including one bounded forced-termination warning; ensure wait cancellation, EOF, raw exit 130, or kill without matching intent cannot independently authorize cancellation.
- [ ] 4.3 Route success, no-work, failed/busy terminal, command/start failure, readiness timeout/pre-ready exit, execute write/flush failure, protocol/bridge/projection fault, post-ready crash/missing terminal, mapped/unmapped exit, and kill/cleanup failure through the finalized classifier and shared finalization receipt.
- [ ] 4.4 Preserve committed terminal authority and compatible `ProcessingState` counters, timestamps, activity cleanup, `LastError`, severity/order, no-work line, cancellation line, fatal diagnostics, and exactly one final summary; append at most one bounded safe anomaly for later contradictory evidence.
- [ ] 4.5 Close callbacks and activities, settle launcher/cancellation/process/stream/run-scope cleanup, and release only the matching coordinator handle in that order; reject late, duplicate, stale, cross-run, and post-finality events without affecting a replacement run.
- [ ] 4.6 Assert every child resolution, startup, protocol, cancellation, classification, and cleanup failure remains on the original run with no in-process fallback, automatic retry, replacement child, terminal replay, or duplicate execution.

## 5. Add deterministic control-plane tests

- [ ] 5.1 Add signal-driven manual admission tests for non-empty unique run ID, request/token/reporter identity, published cancellation before pending, `MarkPending()` before arm/dispatch, prompt accepted return, and silent duplicate rejection without sleeps or polling.
- [ ] 5.2 Add recording-state tests for ready/run-started/eligibility timing, successful progress and overlapping activities, compatible typed logs, successful and zero-work completion, and exactly one terminal mutation/summary.
- [ ] 5.3 Add Stop tests for cancellation immediately after pending, cancellation before and after execute flush, cooperative terminal, unresponsive grace/tree kill, kill rejection, complete drainage, and blocked retrigger until exact cleanup.
- [ ] 5.4 Add table-driven abnormal-outcome tests for busy, command/start failure, readiness timeout, execute transport failure, protocol categories, bridge/projection receipt races, crash/missing terminal, terminal/exit mismatch, raw mapped-looking and unmapped exits, stderr redaction, and cleanup failure.
- [ ] 5.5 For success, no-work, cancellation, busy, every abnormal failure class, and terminal/transport races, prove one selected backend dispatch, one finalization winner, no activity/callback residue, matching-handle release, a different run ID on retrigger, and no stale event impact.
- [ ] 5.6 Add a narrow Dashboard binding/component test only if coordinator-boundary tests cannot prove Run/Stop use the backend-neutral surface and existing state-change refresh remains intact; do not introduce a broad UI harness otherwise.

## 6. Reuse process fixtures and verify scope

- [ ] 6.1 Add a thin manual-coordinator integration layer over the finalized block-26 fixture; reuse its protocol and modes rather than creating a new executable or dialect.
- [ ] 6.2 Exercise existing success, no-work, failed/busy, pre-ready crash, post-ready crash, malformed, oversized, unknown/incompatible, invalid-sequence, terminal/exit mismatch, stderr-flood, mapped/unmapped exit, cooperative-cancel, and unresponsive/forced-kill modes with positive handshakes, full stream drainage, and unconditional process-tree cleanup.
- [ ] 6.3 Cover OS-start, readiness-timeout, execute write/flush, sink/projection, and kill-rejection cases through deterministic seams where the fixture has no mode; use fake time/gates rather than sleeps.
- [ ] 6.4 Run focused manual coordinator/backend/state tests, finalized process-fixture coverage, and `npm run test` with default exclusions.
- [ ] 6.5 Run `openspec validate 34-execute-manual-runs-in-child-worker --strict`, inspect `openspec status --change 34-execute-manual-runs-in-child-worker`, and review the scope diff to confirm no implementation from scheduled block 35, no public selector, no production-default change, and no fallback path.

## Audit Reconciliation

Block 26 is a prerequisite for deterministic real-worker fixture coverage. The manual request uses one exact `Guid` identity whose canonical wire representation is preserved unchanged through child launch, events, bridge, cancellation, and finality. It consumes the internal exact 10-second `TimeProvider` cancellation policy without adding a public setting. UI `Processed` is projected from `UpdatedCount`, never aggregate `ProcessedCount`.

