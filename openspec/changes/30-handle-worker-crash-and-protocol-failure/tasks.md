## 1. Reconcile Prerequisites and Contracts

- [x] 1.1 Re-read applied blocks 15, 17, 21–23, and 25–29; map their exact protocol, exit, launcher, bridge, cancellation, shutdown, and coordinator APIs without changing those owners.
- [x] 1.2 Define immutable evidence, authoritative outcome, stable category, supplementary anomaly, projection receipt, and cleanup-decision contracts using the existing run identity and exit constants.
- [x] 1.3 Encode the closed classification matrix, including block-23 precedence and the rule that raw exit values alone are not domain authority.

## 2. Classifier and Finalization State Machine

- [x] 2.1 Implement separate monotonic transport/control and UI-commit dimensions; allow normal terminal commitment before exit while waiting for typed command/start failure or exit plus stdout/stderr finality before freezing classification/anomaly evidence.
- [x] 2.2 Classify command-resolution/OS-start, pre-ready timeout/EOF/exit, ready rejection, execute write/flush, malformed/oversized/partial/unknown/incompatible output, sequence/correlation/lifecycle/progress/terminal/activity-cardinality, sink/projection, output transport, crash, unmapped exit, and missing-terminal cases.
- [x] 2.3 Preserve committed Completed/Cancelled/Failed terminal authority and record terminal/exit, late input, post-flush output, disposal, and shutdown contradictions only as bounded supplementary anomalies.
- [x] 2.4 Classify cooperative Stop, exact-session forced kill, kill rejection, managed shutdown, and unrelated abrupt death from combined intent and process facts rather than raw code alone.
- [x] 2.5 Route terminal-preventing faults on still-live workers through one exact-session internal fault-containment reason that reuses block-28 deadline/kill/drain/disposal ownership, sends no unsafe protocol input, and remains distinct from Stop/shutdown.
- [x] 2.6 Preserve reserved code-3 behavior as a matching advisory observation for an existing Failed busy terminal, with current failed UI semantics and no retry or block-31 lock implementation.

## 3. UI, Diagnostics, and Cleanup Composition

- [x] 3.1 Add or reconcile one run-scoped idempotent finalization receipt shared by normal block-27 terminal projection and abnormal finalization, including pre-commit rejection and indeterminate projection resolution.
- [x] 3.2 Add the narrow abnormal Failed/Cancelled finality and post-terminal anomaly surfaces needed to produce exactly one UI terminal mutation, fatal/cancel accounting, completion summary, and notification sequence.
- [x] 3.3 Compose terminal or abandonment activity cleanup, callback closure, launcher/Stop/shutdown disposal, and exact matching coordinator release in the specified order; reject late, duplicate, stale, cross-run, and post-finality events.
- [x] 3.4 Preserve the launcher's 65,536-byte stderr tail metadata and implement typed safe diagnostics plus a separately bounded control-character-safe redactor for any displayed excerpt; never copy arbitrary stderr into LastError or UI logs.
- [x] 3.5 Verify no classifier path starts a replacement worker, retries stdout/projection, schedules automatic retry, or releases ownership after an unsettled kill failure.

## 4. Deterministic Verification

- [x] 4.1 Add exhaustive pure matrix tests for every classifier category, block-23 precedence, mapped-looking abrupt exits, additive-field compatibility, committed-terminal authority, code-3 Failed-terminal behavior, and no-retry results.
- [x] 4.2 Add gated seam tests for admitted command-resolution and OS-start failure, ready timeout/rejection, execute write/flush faults, EOF/finality ordering, sink/projection receipt states for each terminal type, output transport failure, Stop/shutdown/fault-containment races, non-exiting pre-ready/post-acceptance faults, kill rejection, zero-process descriptor cleanup, and exact-session release.
- [x] 4.3 Add race tests proving one UI terminal mutation, one summary/fatal effect, complete activity cleanup, callback closure, no late-event mutation, and no replacement-run impact.
- [x] 4.4 Reuse unchanged block-26 ready, success, no-work, pre/post-ready crash, malformed, oversized, unknown, invalid-sequence, terminal mismatch, stderr-flood, mapped/unmapped exit, cooperative-cancel, and unresponsive modes with gates, full drainage, and orphan-safe cleanup.
- [x] 4.5 Add adversarial diagnostic tests for 65,536-byte tail metadata, replacement decoding, display bounds, control stripping, credentials, URI userinfo, connection strings, authorization/bearer and generic tokens, command/request frames and payloads, secret-like mixed-case key/value delimiters, explicit truncation/redaction markers, and valid-success behavior under stderr flood.
- [x] 4.6 Run focused classifier/state/launcher/bridge/cancellation/shutdown/fixture tests, `npm run test`, `openspec validate 30-handle-worker-crash-and-protocol-failure --strict`, and strict status/scope review proving block 31 remains untouched and implementation is confined to the applied change-30 contracts.

## Audit Reconciliation

There is one exact-session internal deadline, started by whichever happens first: accepted Stop, host shutdown, or fault containment. It is the block-28 internal exact 10-second `TimeProvider` deadline, never a second timer. Classification must keep semantic rejection (a definite invalid/contradictory event), noncommit (no authoritative terminal commit), and indeterminate receipt (a terminal/projection attempt whose authoritative commit cannot be known) distinct; none may be silently upgraded to a committed terminal. The coordination and worker-event bridge capability contracts are modified to expose these bounded observations and finalization handoff without changing UI projection ownership.

