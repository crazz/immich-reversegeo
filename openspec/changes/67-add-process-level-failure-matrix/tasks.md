## 1. Matrix and harness foundations

- [ ] 1.1 Re-read the applied block-26 process fixture, block-46 Docker fixture, block-50 arbitration, and final block-66 event catalog; document the exact existing fixture extension points and stop if implementation would require a new production fault seam or telemetry contract.
- [ ] 1.2 Add a table-driven row model containing protocol version, exact job kind, phase/fault selector, raw-exit expectation (`Absent`, `ExactManaged`, or `PresentPlatformRaw`), terminal authority/outcome, event IDs, and required child/stream/coordinator/lock/filesystem probes, with explicit not-applicable entries rather than a blind Cartesian product.
- [ ] 1.3 Add unique per-row roots, pre-start process-tree registration, named phase watchdogs, unconditional kill/wait teardown, descendant-leak detection, and explicit capability-probe skip reasons; fail when fallback cleanup or artifacts remain.
- [ ] 1.4 Add a structured block-66 sink assertion helper covering EventId/name/level/template fields, canonical identity, separate controller/worker PIDs, monotonic durations, complete 6641 memory available/unavailable shape without thresholds, conditional 6650 cardinality/aggregates, and redaction across state/rendering/scopes/exceptions.

## 2. Closed child-fixture modes

- [ ] 2.1 Exercise spawn failure with a closed unstartable launcher descriptor, then extend the existing fixture with closed never-ready/crash-before-ready/crash-after-ready/terminal-then-exit/mapped-exit/unmapped-death modes without accepting arbitrary commands, paths, or shell input.
- [ ] 2.2 Add exact malformed, truncated, invalid-UTF8/framing, 1,048,577-byte, duplicate/unknown-semantic, additive-compatible-property, wrong-correlation, out-of-order and post-terminal stdout modes that preserve explicit EOF and exit control.
- [ ] 2.3 Add independently gated concurrent stdout protocol bursts and stderr floods exceeding pipe capacity and the 65,536-byte retained tail, including exit-before-final-bytes ordering.
- [ ] 2.4 Add cooperative and unresponsive cancellation modes, including a spawned descendant whose process-tree lifetime is observable, and drive the production ten-second grace exclusively with injected `TimeProvider`.
- [ ] 2.5 Add controlled CacheMutation workspace stages for preparation/transfer/export/validation/handle-close/pre-replace failure and before/after-publication cancellation using minimal local fixtures and no network access.

## 3. Pre-launch and lifecycle rows

- [ ] 3.1 Add the invalid deployment-mode canary asserting pre-host exit 2, bounded stderr only, no worker telemetry, no child, and unchanged isolated config/data roots.
- [ ] 3.2 Add invalid private protocol-version, v1/v2 envelope, payload and closed-selector rows for ProcessAssets, CoordinateLookup and CacheMutation, asserting pre-acceptance exit 2, no synthetic terminal/domain work, final drains and cleanup; separately add valid-startup unusable-configuration/dependency rows asserting infrastructure exit 5 and safe startup classification.
- [ ] 3.3 Add atomic Busy and shutdown-fenced Unavailable rows for all three job kinds, asserting no process/PID/ready/terminal/exit/cancel/lock/mutation, no queue/retry, and exact local-owner preservation/release.
- [ ] 3.4 Add spawn failure and readiness-timeout rows asserting no fabricated PID/ready/terminal, exact Failed finalization, expected 6610/6611/6612/6641 presence or absence, whole-tree containment, both drains and one coordinator release.
- [ ] 3.5 Add crash-before-ready, crash-after-ready, missing-terminal, mapped nonzero, unmapped death and valid-terminal/contradictory-exit rows for each applicable job, asserting raw facts, committed-terminal precedence and exactly one final classification.

## 4. Cancellation, shutdown, protocol, and pipe rows

- [ ] 4.1 Add cooperative cancellation during ProcessAssets asset work and CacheMutation work, plus applicable CoordinateLookup cancellation, asserting one cancel, valid Cancelled terminal, exit 130, no kill, events 6620/6621/6640/6641, drains and release.
- [ ] 4.2 Add forced cancellation rows that advance virtual time to 9,999 ms without early kill and then exactly to the 10,000 ms deadline, asserting one whole-tree escalation, 6622/6623 ordering, exact Cancelled outcome with `process_classification=forced-stop` for pre-existing Stop/shutdown intent, and no descendant.
- [ ] 4.3 Add active-parent-shutdown rows for all job kinds, asserting admission fences first, the same cancellation/deadline operation is joined, and host completion waits for child/streams/bridge/telemetry/disposal/owner finality.
- [ ] 4.4 Add the complete corrupt/truncated/oversized/out-of-order/unknown-semantic NDJSON table, plus a positive additive-unknown-property compatibility row, asserting first-fault atomicity, zero invalid projection, event 6630 safe category/redaction, one Failed finalization, containment, continued drains, and no violation for the compatible row.
- [ ] 4.5 Add simultaneous full stdout/stderr and exit-before-trailing-bytes rows using non-replaceable frames, asserting no deadlock within named watchdogs, terminal/protocol evidence discovered during drainage, correct bounded-tail truncation metadata, event 6641 after both pumps, no raw stderr telemetry, and no 6650; add one separately deliberate replaceable-progress pressure row asserting at most one exact 6650.

## 5. Cache failure, retry, and orphan evidence

- [ ] 5.1 Add CacheMutation pre-publication failure rows asserting old-final hash preservation, closed handles, removal of every attempt temp/download file, exact failed terminal/exit/telemetry, no automatic retry and one release.
- [ ] 5.2 Add explicit repaired retry asserting a new JobId, child PID, temporary path and launch only after old process/stream/handle/artifact finality and coordinator release, then validated atomic publication and independent successful finality.
- [ ] 5.3 Exercise real cache publication/cleanup through existing seams and add post-publication-cancellation coverage; keep any delayed exact-task-identity map continuation separate and file-free, and stop rather than add a production fault-injection seam.
- [ ] 5.4 Run descendant, handle, stream, coordinator and isolated-root cleanup assertions after every normal row rather than only in cancellation/crash tests.

## 6. PostgreSQL Integration rows

- [ ] 6.1 Reuse block 32's disposable-PostgreSQL preference or explicit dedicated `immich_reversegeo_test_` setup, serialize fixed-key tests, and categorize only these external-database rows as `Integration`.
- [ ] 6.2 Add ProcessAssets database-connect/session-loss rows asserting the established failed/infrastructure terminal and exit 5 where authoritative, closed sessions, no surviving child and exact safe telemetry.
- [ ] 6.3 Add fixed production advisory-lock contention asserting exactly one valid failed busy terminal, exit 3, zero count/domain/cache/skipped/write work, no unlock of the competing session, and eventual local cleanup.
- [ ] 6.4 Prove advisory-key reacquisition after normal completion, domain exit 4, output exit 6, cooperative exit 130 and abrupt worker death; cover unlock false/failure/ambiguity or disposal failure as exit 5 with physical-session quarantine and eventual independent-session reacquisition; retain block 32's narrow capability-based inconclusive rule only for an unavailable backend-loss primitive.
- [ ] 6.5 Add negative assertions that CoordinateLookup and CacheMutation never request the advisory lock or emit exit 3 across success, failure and cancellation.

## 7. Verification and scope

- [ ] 7.1 Run focused matrix groups repeatedly under the normal test settings and prove all hermetic process/protocol/cancellation/pipe/arbitration/cache rows execute without PostgreSQL, Docker, fixed ports, downloads or sleeps.
- [ ] 7.2 Run `npm run test` and confirm default Integration/Performance exclusions; when an external test database is available, run `npm run test:integration` and confirm Performance remains excluded.
- [ ] 7.3 Verify every finite wait is named, every platform skip reports a probed capability, every fixture marker is absent from block-66 structured state/rendering/scopes/exceptions, and no row contacts Overture/GADM services.
- [ ] 7.4 Run `openspec validate 67-add-process-level-failure-matrix --strict`, confirm OpenSpec status is 4/4 complete, and review a scoped diff proving only block 67, its four planning artifacts, and later test/fixture implementation files changed—never blocks 66/68 or production contracts.
