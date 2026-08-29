## 1. Prerequisite reconciliation and catalog

- [ ] 1.1 Re-read applied blocks 18–32, 40–45, 47–51, 59, and 65; record the exact landed role/mode, JobId/RunId, job kind/origin, PID/session, cancellation, classifier, detector, and coalescer-owner symbols, and stop rather than create parallel contracts on divergence.
- [ ] 1.2 Add one internal lifecycle event catalog with exact EventIds 6601–6605, 6610–6612, 6620–6623, 6630, 6640–6641, and 6650, static templates, exact common-plus-event field sets with no extra application fields, closed-value mappings, conditional null/presence invariants, and level selection from the specification.
- [ ] 1.3 Add an immutable safe worker-job log context that reuses canonical JobId, maps ProcessAssets JobId to the same RunId without a second field, normalizes the four bounded origins, and distinguishes controller from nullable worker PID.
- [ ] 1.4 Add a non-throwing elapsed-millisecond helper over injected TimeProvider monotonic timestamps with truncation and non-negative/saturating behavior.

## 2. Role and worker lifecycle emission

- [ ] 2.1 Instrument successful public mode selection and Web/InternalWorker/RunOnce role starting, readiness, stopping, and stopped owners with events 6601–6605, while preserving pre-logger stderr failures and never emitting command-line/private-selector/environment values.
- [ ] 2.2 Instrument launcher entry, successful OS-process ownership, and accepted ready boundaries with events 6610–6612 and exact process-start/readiness/total-startup durations.
- [ ] 2.3 Instrument the existing shared exact-session stop operation with one cancellation request event, cooperative grace completion emitted after drain but timed request-to-exit, one grace-expiry escalation, and one post-exit/drain forced-stop result without adding or resetting the established 10000 ms policy.
- [ ] 2.4 Instrument first retained protocol violation, accepted terminal receipt, and one post-drain authoritative classifier finality with events 6630, 6640, and 6641; enforce the exact terminal/exit/readiness/forced-stop cross-field matrix and cover crash-before-ready and missing/contradictory terminal paths without fabricated ready or terminal events.
- [ ] 2.5 Consume block 65's exact final observation snapshot and emit event 6650 at most once when enqueue waits or replacements occurred, copying only its exact aggregates plus terminal/nonterminal finality and using null terminal-flush duration for nonterminal finality; add no derived capacity/suppression or per-progress/per-notification log.
- [ ] 2.6 Verify block 59 remains the sole emitter of EventId 5901 with unchanged fields, levels, one-event-per-call behavior, and no 66xx detector wrapper.

## 3. Best-effort child working-set observation

- [ ] 3.1 Extend the landed child-process abstraction with a testable current WorkingSet64 observation that returns bounded typed success/unavailable facts without exposing platform exception text.
- [ ] 3.2 Add one parent-session-owned sampler that attempts immediately after start, every 1000 ms from a TimeProvider timer, and once at finality; serialize callbacks, track only the maximum successful child sample and successful count, and join disposal before handle release.
- [ ] 3.3 Add the exact memory scope/method/interval/observation/bytes/count/unavailable-reason fields to event 6641, proving sampling failures never alter process control or classification and never imply process-tree, cgroup, system, or absolute peak memory.

## 4. Structured event-sink verification

- [ ] 4.1 Add a recording structured-log sink that captures EventId, name, level, original template, structured key/value state, scopes, rendered output, and attached exception for exact catalog assertions.
- [ ] 4.2 Add deterministic fake-TimeProvider tests for all startup/readiness/process/stop/cancellation/grace/escalation/terminal-flush durations, sub-millisecond truncation, wall-clock jumps, negative/faulty-provider clamping, and the exact 999/1000 ms detector boundary retained from event 5901.
- [ ] 4.3 Add event-sequence tests for exact Standard/Web-only→Web, Run-once→RunOnce, and null-mode InternalWorker role/readiness/stop mappings; successful and crash-before-ready child launch; ProcessAssets/CoordinateLookup/CacheMutation kind and origin; canonical identity; worker-PID null-before/non-null-after ownership continuity; cooperative/repeated cancellation; escalation/kill outcomes; protocol violations; every agreeing managed terminal/exit/classification tuple, null-terminal mapping, mismatch, readiness implication; and one final classifier event.
- [ ] 4.4 Add available, partially sampled, and fully unavailable memory tests including process-exited/access-denied/not-supported/sample-failed/no-sample cases, mixed-failure order-independent reason precedence, exact maximum/count semantics, one sampler owner, timer disposal races, and no fabricated zero bytes.
- [ ] 4.5 Add saturation tests proving one final 6650 Warning copies every exact block-65 aggregate under FIFO wait/replacement, reports nullable terminal-flush duration correctly for terminal versus nonterminal finality, and emits no event for an unsaturated job.
- [ ] 4.6 Feed hostile coordinates, request/result/protocol payloads, CLI/private-selector text, environment/configuration, paths, SQL, credentials, connection strings, tokens, raw stderr/tails, exception messages, and stacks through every failure seam and assert absence from template, state, scopes, rendering, and exception slot.
- [ ] 4.7 Assert no Meter/instrument/exporter, metric or trace dimension, protocol field, ProcessingState/UI-ring event, persisted setting, or per-asset lifecycle event is introduced.

## 5. Existing process-fixture coverage and validation

- [ ] 5.1 Extend the existing hermetic real child fixture with focused telemetry assertions for success, crash before ready, cooperative cancellation, grace escalation, malformed protocol, terminal/exit mismatch, trailing stderr, and unavailable memory, reusing its existing PID ownership, drain, and orphan-reaping paths rather than creating block 67's failure matrix.
- [ ] 5.2 Prove fixture captures preserve one JobId/kind/origin across launch through classification, never copy raw stderr or protocol/request payloads into telemetry, and leave no child process or sampling timer after finality.
- [ ] 5.3 Run focused catalog/sink/launcher/cancellation/classifier/coalescer/process-fixture tests, then the normal default-exclusion suite and `openspec validate 66-add-role-process-lifecycle-telemetry --strict`.
- [ ] 5.4 Confirm OpenSpec status is 4/4 complete and review the session file-operation manifest (plus a tracked diff when a baseline exists) proving planning wrote only MASTERPLAN block 66 and change-66 artifacts, not blocks 65/67 or implementation files.
