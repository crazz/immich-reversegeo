## Why

Block 31 proves PostgreSQL advisory-lock semantics between in-process sessions, but it does not prove that independently launched workers preserve the busy terminal, exit, release, projection, and cleanup contracts. Process-level evidence is required before the Web coordinator can rely on cross-process exclusion and recovery. The real production-descriptor smoke has also shown that an accepted terminal can be projected while the child remains alive in a pending standard-input read; finality needs one private transport completion action that is independent of callback delivery.

## What Changes

- Add real-PostgreSQL, `Integration`-category tests that launch two independent OS worker processes against the same database and exact production advisory-lock key.
- Add a block-32-only PostgreSQL-aware integration worker apphost that reuses block 26's staging/handshake/reaper architecture and composes the real block-31 worker lock path; route it from the parent through the applied block-30 finalizer/coordinator path, while keeping block 26's hermetic fixture and production worker CLI unchanged.
- Hold the first worker deterministically only after accepted `run-started`, successful real lock acquisition, and the required zero-eligibility event; then prove the second emits one valid Failed busy terminal, exits 3, and performs no domain work or mutation.
- Prove release and fresh-process reacquisition after success, executor/domain failure, cooperative cancellation, abrupt process death, and privilege-gated PostgreSQL connection loss.
- Verify exact-once terminal projection, activity cleanup, no retry, and coordinator return to idle for busy and every owner outcome.
- Immediately after one fully validated, exact-correlated terminal is recorded and before sink callback admission, seal new controller stdin writes, let an already admitted canonical write/flush settle under the existing writer owner, then half-close controller input exactly once so a child blocked in its native control read can observe peer EOF and exit. A pre-exit physical close failure records the narrow typed `TerminalInputCloseFailed` fact, preserves the accepted terminal and receipt, and joins the existing bounded fault-containment lifecycle with existing InputTransport classification; post-exit cleanup remains best effort. The successful private completion action does not latch Stop, cancel the worker, create a grace deadline, kill a process, or replace terminal/raw-exit authority; its failure invokes only the existing containment deadline and escalation owner.
- Define fail-fast PostgreSQL configuration, fixed-key database isolation, time-bounded handshake, unique-resource, secret-handling, cleanup, and no-orphan contracts.
- Preserve default `npm run test` exclusion and explicit `npm run test:integration` inclusion; defer CI orchestration to block 69.

## Capabilities

### New Capabilities
- `cross-process-run-lock-verification`: Verifies real advisory-lock contention, terminal/exit mapping, release, reacquisition, control-plane projection, and cleanup across independent worker processes.

### Modified Capabilities
- `child-worker-launching`: Completes the controller-to-worker input transport after an accepted terminal while preserving writer ownership, process ownership, event delivery, and disposal finality.
- `child-worker-cancellation`: Keeps cooperative terminal finality and the Stop lifecycle distinct from terminal-owned input completion.
- `worker-stdin-request-loop`: Lets the child command pump complete through peer EOF after terminal, while retaining structured cancellation/disposal for remaining control work.
- `worker-failure-recovery`: Contains a pre-exit terminal-input close failure through the existing bounded fault lifecycle while preserving an already committed terminal and reporting InputTransport as a supplementary anomaly.

## Impact

Planning affects the integration-test project, a test-only staged PostgreSQL worker apphost, PostgreSQL test provisioning/maintainer setup, and tests around the applied worker launcher, coordinator, projection, classifier, advisory-lock lease, and private stdin transport ownership. The only production-adjacent support permitted is the existing narrow internal post-lock operation seam, compatible terminal-evidence classification of the already-reserved Failed exits 3 (busy), 4 (domain), and 5 (infrastructure), and the paired terminal-owned input half-close. Receipt origin gates only terminal-derived exit/result and post-terminal protocol/projection anomalies: a control-plane receipt never becomes worker testimony, while independent late input/output transport, kill, cleanup, and shutdown anomalies remain reportable. The half-close uses the existing memoized close owner after an accepted terminal; its pre-exit physical-close failure uses the existing bounded containment owner and existing InputTransport classification, while post-exit cleanup remains best effort. Successful completion adds no detached pump, native-platform-I/O subsystem, CLI/wire/schema/key/default-backend change, Stop state, cancellation token, grace deadline, kill action, terminal replacement, retry, or parallel evidence owner; the failure path only joins the existing containment deadline and escalation owner. No public protocol, public internal-worker CLI, database schema, lock key, retry policy, default test filter, CI workflow, or broader runtime refactor is authorized.

## Audit Reconciliation

The real-process Busy assertion must require the canonical sequence: `run-started`, no eligibility event, one failed Busy terminal whose four counts are all zero, and reserved exit evidence 3. It must also prove no domain, heavy, or producer work inside the already-invoked executor, rather than accepting a merely zero aggregate result.
