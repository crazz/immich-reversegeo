## 1. Prerequisite reconciliation and policy

- [ ] 1.1 Re-read the applied block 15/16 v1 codec, exact-sequence validator, fixtures, and block 21 emitter; stop rather than coalesce raw reading, alter bytes, or weaken validation if the applied APIs differ materially.
- [ ] 1.2 Re-read the applied block 27 accepted-event bridge/ProcessingState adapter, block 44 status read model, block 47 v2 job descriptors/session/finality, and block 49/51 page-state generations; record the exact extension points without editing blocks 64 or 66.
- [ ] 1.3 Add a closed typed replaceability policy that defaults to lossless, opts in v1/v2 ProcessAssets absolute snapshots, and requires capability-specific proof before opting in Lookup/cache progress.
- [ ] 1.4 Add internal test-injectable policy values with provisional production defaults of a 256-entry lossless FIFO, one replaceable slot, and 100 ms notification cadence; expose no public setting or protocol field.

## 2. Sequence-aware bounded delivery

- [ ] 2.1 Add a per-session post-validation delivery stage bound to exact version, kind, identity, owner/session generation, and page generation where applicable.
- [ ] 2.2 Implement one bounded wait-on-full lossless FIFO plus one nonblocking latest-wins snapshot slot and a dedicated consumer that is independent from Blazor renderer, stderr drain, and process-exit observation.
- [ ] 2.3 Preserve raw source sequence and generate trusted contiguous suppression-span evidence only for superseded same-session declared snapshots; make every lossless event flush the prior pending snapshot as an ordering barrier.
- [ ] 2.4 Extend processing and v2 projection cursors to accept only exact-next delivery or authenticated replaceable-only suppression spans while retaining all correlation, kind, lifecycle, count, activity, terminal, and stale checks.
- [ ] 2.5 Add bounded numeric observation snapshots for accepted/replaced/delivered counts, FIFO high-water/waits, projection/flush timing, notifications, stale rejection, and abandonment, without adding block 66 instruments, names, exporters, dashboards, or identifiers.

## 3. Terminal, cancellation, shutdown, and disposal

- [ ] 3.1 Make validated terminal acceptance atomically close intake, flush the latest pre-terminal snapshot, drain prior lossless items, project terminal last, and settle one finality receipt only after the final notification boundary.
- [ ] 3.2 Compose the receipt with existing process exit, stdout/stderr EOF, protocol finalization, bridge cleanup, and exact admission release for every job kind; reject all post-terminal or stale callbacks.
- [ ] 3.3 Implement one idempotent asynchronous cancellation/shutdown/disposal path that wakes capacity waiters, stops timers, joins in-flight projection, suppresses stale rendering, and never fabricates or retries a terminal.
- [ ] 3.4 Preserve already-projected lossless facts and return bounded nonterminal/abandonment observations to the existing classifier for crash, malformed stream, missing terminal, forced stop, or uncertain projection.

## 4. TimeProvider-based UI cadence

- [ ] 4.1 Add a revisioned dirty-signal notification scheduler using injected TimeProvider with fixed-rate 100 ms default cadence and virtual-time test control; do not use sleeps or synchronous renderer waits.
- [ ] 4.2 Apply cadence to processing, block-44 worker status, Lookup, and cache read-model notifications without merging their ownership or dropping state mutations.
- [ ] 4.3 Add immediate deduplicated final notification for terminal, retained failure, cancellation-finality, and disposal-finality; ensure reconnecting/new components read the latest immutable snapshot immediately.
- [ ] 4.4 Preserve component InvokeAsync/disposed/generation guards so timer or stream callbacks cannot render a stale job, mutate a newer snapshot, or release another owner.

## 5. Deterministic correctness and compatibility tests

- [ ] 5.1 Add capacity-one gated burst tests proving bounded memory, latest-wins replacement, contiguous suppression spans, lossless FIFO order, barriers, asynchronous backpressure, and active independent stderr/exit tasks without sleeps.
- [ ] 5.2 Add raw sequence-gap, duplicate, regression, wrong-version/kind/identity, unknown event, invalid payload, post-terminal, and forged-suppression tests proving primary protocol/classifier behavior remains fail closed before state mutation.
- [ ] 5.3 Add interleaved Trace/Information/Warning/Error log, activity nesting, eligibility, snapshot, result, and terminal tests proving every nonreplaceable item is projected exactly once and terminal is last.
- [ ] 5.4 Add cancellation-before/after enqueue, terminal/cancel race, host shutdown under full capacity, crash/missing-terminal, projection fault, repeated disposal, stale generation, and next-job reuse tests proving no deadlock, orphan waiter, duplicate finality, or stale release.
- [ ] 5.5 Re-run all v1 canonical and additive-compatibility fixtures unchanged; add equivalent v1/v2 ProcessAssets burst tests proving final ProcessingState lifecycle/count/log/activity/terminal/classification parity.
- [ ] 5.6 Add CoordinateLookup and CacheMutation tests proving only explicitly declared full snapshots coalesce, their logs/activities/results/terminals remain lossless, page generations suppress stale callbacks, and neither projects transient state into ProcessingState.

## 6. Process, Blazor, and measurement verification

- [ ] 6.1 Extend the real child-worker fixture with controlled high-rate v1 and v2 progress, interleaved lossless events, blocked projection, valid terminal, malformed gap, crash, cancellation, and shutdown modes; assert stdout/stderr/process/protocol/bridge/coalescer finality settles.
- [ ] 6.2 Add virtual-time subscriber/component tests for Dashboard, Logs, NavMenu, Lookup, and cache status proving at most 10 ordinary notifications per second at the provisional cadence, newest-snapshot rendering, immediate single final render, reconnect snapshots, responsive controls, and disposal suppression.
- [ ] 6.3 Run a repeatable representative burst/process/Blazor measurement and record event rate, delivered rate, queue high-water/waits, replaced count, projection latency, notification rate, terminal-flush latency, allocations/retained memory, hardware/runtime, and test shape.
- [ ] 6.4 Confirm or revise the provisional 256-entry capacity and 100 ms cadence from the recorded evidence before production enablement, rerun correctness/performance tests with the selected values, and document the rationale in the block 65 implementation/test record.
- [ ] 6.5 Run focused tests repeatedly, then npm run test with default exclusions, and run openspec validate 65-coalesce-worker-progress-events --strict plus openspec status to confirm 4/4 planning artifacts; do not implement block 66 telemetry.
