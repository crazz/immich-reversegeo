## 1. Prerequisite Reconciliation

- [x] 1.1 Re-read the applied source APIs from blocks 7–9, 15, 21, and 25 and record the exact request/result, transport-neutral event, state-adapter arm/projection, typed protocol envelope, accepted-event sink, launcher session, and observation types used by this change.
- [x] 1.2 Stop and reconcile the predecessor changes if those source APIs are absent or incompatible; do not recreate parallel reporter, adapter, protocol, launcher, job identity, or terminal contracts in block 27.
- [x] 1.3 Confirm block 26's fixture files and change artifacts remain untouched and that current in-process routing is not duplicated or switched to child execution in this change.

## 2. Accepted-Event Bridge Contracts

- [x] 2.1 Add one Web/controller bridge factory and one run-scoped asynchronous sink instance for an admitted `ProcessingRunRequest`, bound to the exact singleton block-9 adapter.
- [x] 2.2 Reuse or add the narrow safe typed sink-rejection/nonterminal observation shape needed for block-25 retention and block-30 handoff, without raw payload, crash classification, or user-facing fatal synthesis.
- [x] 2.3 Keep PID on the launcher session and run/job identity on the request/bridge; add no protocol/process fields to `ProcessingState` or Razor components.

## 3. Correlation and Ordered Projection

- [x] 3.1 Implement the typed projection cursor for exact-next sequence, ready-first/null-run identity, exact request run ID thereafter, closed type/payload mapping, lifecycle cardinality, eligibility, activity IDs, progress coherence, and terminal finality without reparsing JSON.
- [x] 3.2 Make ready advance only bridge handshake state and map run-started as correlation-only, eligibility as the existing visible start/reset operation, and pre-eligibility cancelled/failed terminal without fabricated total/start state.
- [x] 3.3 Serialize direct/concurrent sink calls through one async gate, await the entire adapter projection, add no second queue or fire-and-forget path, and return only after synchronous `OnChanged` callbacks complete.
- [x] 3.4 Reject stale, mismatched-run, skipped/regressive/duplicate sequence, duplicate lifecycle, invalid activity, duplicate terminal, and post-terminal callbacks before state mutation; preserve one safe sink observation for block 30 and do not advance the cursor on rejection.

## 4. State Mapping and Terminal Semantics

- [x] 4.1 Map absolute progress with Updated-to-visible-processed, Skipped-to-skipped, and per-asset Failed-to-ordinary-errors while validating aggregate Processed and preventing replay/delta double accounting.
- [x] 4.2 Map typed diagnostics through the block-9 log path without predecorating timestamps/severity, so handled Error diagnostics update `LastError` and one log line while progress alone supplies their failed count.
- [x] 4.3 Map activity starts/ends by exact run and opaque activity ID while preserving labels, equal-label reference counts, out-of-order valid ends, and isolation from late prior-run disposal.
- [x] 4.4 Cross-check terminal request/trigger, type/outcome, UTC/failure-detail invariants, count equation, and equality with the latest progress snapshot (or zero counts when no progress has been accepted) before invoking terminal projection.
- [x] 4.5 Project coherent Completed/Cancelled/Failed outcomes once through the existing adapter order, preserving handled-versus-fatal accounting, cancellation behavior, activity cleanup, completion timestamp, unchanged summary text, and ownership release.

## 5. Disposal and Composition Boundary

- [x] 5.1 Add the narrow block-9 adapter abandonment operation that idempotently clears only the expected run's projected activity scopes without completing state, appending diagnostics/summary, or exposing protocol types.
- [x] 5.2 Implement idempotent bridge disposal that suppresses new callbacks, awaits an in-flight accepted projection, performs nonterminal activity cleanup when needed, and emits only the bounded handoff observation for block 30.
- [x] 5.3 Connect the bridge to block 25's accepted-event sink/factory boundary and exact singleton state adapter without changing launcher parsing/drainage, worker emission, process start, cancellation, coordinator policy, or current in-process routing.

## 6. Deterministic Synthetic Verification

- [x] 6.1 Add synthetic typed-event tests for ready versus pending/run-started/eligibility timing, zero and nonzero eligibility, and pre-eligibility cancelled/failed terminal retention.
- [x] 6.2 Test absolute updated/skipped/failed mapping, aggregate-versus-visible processed meaning, handled Error log plus disposition without duplicate count/line, completed runs with handled failures, and one additional fatal UI error only for failed terminal.
- [x] 6.3 Test Trace/Information/Warning/Error prefix behavior, `LastError`, accepted insertion order, newest-100 retention, exact terminal log/summary order, and at-least-one post-mutation notification with none on rejected events.
- [x] 6.4 Test equal-label distinct activity IDs, distinct-label fallback, matching and out-of-order ends, invalid duplicate/unknown ends, open-activity terminal rejection and coherent terminal after matching ends, nonterminal disposal cleanup, repeated disposal, and late old-run isolation.
- [x] 6.5 Test every correlation/lifecycle rejection class and assert unchanged counters, logs, timestamps, error, activity, running state, and notifications plus one safe block-30 handoff observation.
- [x] 6.6 Test terminal result cross-checks for run ID, trigger, outcome/type, timestamps, failure detail, count equation, latest progress equality, duplicate terminal, and coherent completed/cancelled/failed acceptance.
- [x] 6.7 Use deterministic gates rather than sleeps to prove a held projection backpressures its sink callback, later callbacks cannot overtake it, committed projection is not retracted by unrelated wait cancellation, and disposal waits for in-flight projection.
- [x] 6.8 Add boundary/DI tests proving the bridge resolves the exact singleton adapter, ready never mutates state, Dashboard/Logs remain protocol-unaware, and no block-26 fixture, raw codec, launcher drain, crash classification, cancel, or production backend-switch behavior is introduced.

## 7. Validation

- [x] 7.1 Run focused ProcessingState, block-9 adapter, bridge, and block-25 sink tests with deterministic filters, then run `npm run test` with default exclusions.
- [x] 7.2 Run `openspec validate 27-bridge-worker-events-to-processing-state --strict` and `openspec status --change 27-bridge-worker-events-to-processing-state`, then review the diff to confirm only MASTERPLAN block 27 and this linked change were modified.

## Audit Reconciliation

A terminal received while this bridge has any open projected activity is a typed terminal-coherence rejection, not an instruction to close activities. Only a coherent accepted terminal performs normal terminal cleanup. Forced activity cleanup is limited to nonterminal bridge/session abandonment. A terminal that follows eligibility but no accepted progress is coherent only when all four result counts (`ProcessedCount`, `UpdatedCount`, `SkippedCount`, and `FailedCount`) are zero; eligibility alone never permits nonzero counts.


## Applied seam choices

- The shared Core stream validator is the typed cursor: Preview → exact singleton adapter projection → Validate/commit under one bridge gate. There is no second sequence/activity/progress state machine.
- The Web adapter adds only direct correlated projection, arm inspection, and nonterminal activity abandonment. The existing in-process session, fatal abandonment, public state model, coordinator, and block-26 fixture remain outside these changes. Scope acquisition receives only a narrow exception-safety fix so a throwing observer cannot orphan an activity.
- Launcher sink failures keep the existing generic typed observation; the bridge owns a bounded first projection/nonterminal observation for the future block-30 owner.

## Validation evidence

- Build completed without warnings/errors. Final focused Change27 suite: 52/52 passed. Related state/adapter/launcher/validator subset before review corrections: 212/212 passed.
- Final `LC_ALL=en_US.UTF-8 npm run test`: 1260/1260 passed with default Integration/Performance exclusions. The explicit locale preserves the existing executor snapshot baseline on macOS.
- Brooks review identified and resolved unowned activity scopes after a throwing start observer, and stale ready acceptance after an adapter arm was replaced. Regression coverage verifies cleanup, independent same-label scopes, original exception retention, and unchanged stale-ready state.
- OpenSpec strict validation and whitespace checks passed. Block-26 fixture/source/artifacts, existing backend routing, and Razor components remain unchanged.
- Feature-branch implementation commit `76f5817d7dd1cf1b9f4e11be0753b589cc62c352`: all six CI jobs passed, including Docker build and Linux/Windows/macOS fixture checks. Run: https://github.com/crazz/immich-reversegeo/actions/runs/33987678962
