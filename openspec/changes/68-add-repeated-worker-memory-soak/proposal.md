## Why

Worker isolation is only credible if repeated jobs using the real production worker path leave no process, handle, candidate artifact, or worker-only geodata initialization in the long-lived Web control plane. Existing in-process performance tests and single-job process cases do not establish that boundary over a sustained mixed workload.

## What Changes

- Add an explicitly selected `Performance` soak that reuses the block-67 production process fixture and exact production launcher, executable, protocol, and Standard/Web-only composition.
- Run configurable warmup and measured iterations with a seeded, recorded mix led by ProcessAssets and including nonzero CoordinateLookup and CacheMutation coverage, using deterministic no-network database/cache/geodata fixtures.
- Require a fresh worker PID and complete terminal, exit, stream, disposal, process-tree, handle, temp, and candidate cleanup before each next job; optionally repeat cooperative-cancellation and controlled-failure cycles under the same rules.
- Keep one Web control process alive and combine block-55/56 heavy-initialization sentinels with block-66 worker observations and control-plane memory-trend reporting.
- Keep structural leaks as the portable failure contract. Numeric host-RSS or Linux cgroup-v2 limits apply only from an explicitly selected external platform profile; no universal memory number or slope is asserted.
- Categorize the harness as `Performance`, preserve default and Integration exclusions, provide an explicit runsettings/command, and retain redacted run evidence beneath `_out/performance/worker-memory-soak/`.
- Produce only an optional evidence handoff for block 69; do not add or modify block-69 CI wiring.

## Capabilities

### New Capabilities
- `worker-memory-soak-verification`: Opt-in repeated production-worker verification of process/filesystem cleanup, Web control-plane isolation, and profile-aware memory trend evidence.

### Modified Capabilities
- None.

## Impact

Implementation is limited to Performance tests, extensions of the existing block-67 test fixture/helpers, deterministic local fixtures, performance runsettings/task-runner documentation, and gitignored `_out` artifacts. It consumes but does not change block-55/56 boundary sentinels, block-66 telemetry, block-67 process contracts, production worker protocol/launcher/composition, normal test categories, deployment behavior, or block 69.
