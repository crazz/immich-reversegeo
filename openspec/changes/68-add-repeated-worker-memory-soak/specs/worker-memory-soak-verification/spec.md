## Purpose

Provides repeatable, opt-in evidence that real production worker processes release owned resources while a long-lived Web control process remains free of worker-only geodata state.

## ADDED Requirements

### Requirement: Explicit and reproducible soak selection
The worker memory soak SHALL run only when the `Performance` category is explicitly selected. A run SHALL record its seed, warmup count, measured iteration count, job-mix proportions, enabled cycle types, platform capabilities, and selected threshold profile. Default and Integration test selections MUST continue to exclude the soak.

#### Scenario: Explicit performance invocation
- **WHEN** an operator invokes the dedicated performance runsettings and command with a valid run configuration
- **THEN** the harness records the resolved configuration and runs the selected soak without changing default or Integration test selection

#### Scenario: Ordinary test invocation
- **WHEN** the normal or Integration test command runs
- **THEN** no worker memory soak case is selected

### Requirement: Production-path mixed worker workload
The soak SHALL keep one exact production Standard or Web-only control process alive and SHALL launch jobs through the production launcher, worker executable, composition, and protocol path exposed by the existing process fixture. After configurable warmup, the measured seeded mix SHALL make ProcessAssets the declared majority and SHALL assign nonzero declared proportions to both v2 job kinds, CoordinateLookup and CacheMutation. Workloads SHALL use deterministic local database/cache/geodata fixtures and MUST NOT perform a live download, remote geodata access, or network export.

#### Scenario: Measured mixed workload
- **WHEN** warmup completes and measured iterations begin
- **THEN** the recorded job sequence follows the resolved seed and proportions, exercises all three job kinds, and uses only deterministic local fixture inputs

#### Scenario: Network attempt
- **WHEN** any soak job attempts a live geodata download or network export
- **THEN** a no-network sentinel fails the run and identifies the job without retaining payload or secret data

### Requirement: Per-job process and artifact finality
Every launched job SHALL receive a fresh PID distinct from all earlier job PIDs in the run. Before another job starts, the harness SHALL observe protocol finality as applicable, worker exit, completion of both redirected-stream drains, telemetry and launcher disposal, process-tree termination, handle release, and removal of attempt-owned temporary, download, candidate, and staging artifacts. The harness SHALL fail on PID reuse, missing bounded termination, fallback orphan cleanup, a surviving descendant, an unreleased fixture handle, or an accumulated attempt artifact.

#### Scenario: Successful job reaches finality
- **WHEN** a worker job completes successfully
- **THEN** its PID and finality evidence are recorded and no next worker starts until all process, stream, handle, and artifact checks pass

#### Scenario: Orphan or artifact leak
- **WHEN** a worker or descendant survives the termination wait or an attempt-owned artifact or handle remains
- **THEN** the soak fails structurally and retains the bounded cleanup evidence for that job

### Requirement: Optional cancellation and controlled-failure cycles
A run configuration MAY enable deterministic cooperative-cancellation and controlled pre-publication failure cycles using the existing closed process-fixture controls. Enabled cycles SHALL receive fresh PIDs and SHALL satisfy the same process, stream, handle, candidate, and temporary-artifact finality requirements as successful cycles. They MUST NOT trigger an automatic retry.

#### Scenario: Enabled cancellation and failure cycles
- **WHEN** the run enables cancellation or controlled-failure cycles
- **THEN** each selected cycle reports its authoritative outcome and completes all structural cleanup before the sequence continues

### Requirement: Web control-plane isolation sentinel
The Web control process SHALL remain alive across warmup and measured phases and SHALL expose the block-55/56 counting sentinels for forbidden heavy constructors/factories, country-index loading, native or DuckDB initialization, geodata open/query/export/cache mutation, and in-process execution. The soak SHALL reset measured observations after warmup and SHALL fail if any forbidden sentinel is touched by the Web process.

#### Scenario: Isolated control plane
- **WHEN** repeated workers initialize and release worker-only geodata state
- **THEN** every measured Web forbidden-initialization sentinel remains zero

#### Scenario: Heavy initialization reaches Web
- **WHEN** any forbidden constructor, index, native, geodata, mutation, or local-execution sentinel is observed in Web
- **THEN** the soak fails regardless of reported RSS trend

### Requirement: Memory observations and profile-specific limits
The harness SHALL report ordered Web working-set observations and the complete block-66 per-worker memory availability shape. Reports SHALL state that the block-66 sampler observes `WorkingSet64` after start, at one-second intervals, and opportunistically at finality; a short worker may therefore have only start/finality observations, and these values are neither an OS absolute peak nor process-tree/cgroup/system memory. Without an explicit compatible platform profile, memory values, deltas, slopes, and trends SHALL be diagnostic only and MUST NOT cause failure. An external profile MAY enable host-RSS or Linux cgroup-v2 numeric limits only after its platform/capability match, provenance, units, aggregation, warmup exclusion, and thresholds are recorded.

#### Scenario: Unprofiled run reports a trend
- **WHEN** no compatible threshold profile is selected
- **THEN** the harness reports observations, missing-sample reasons, and trend summaries without a numeric memory failure

#### Scenario: Compatible external profile is selected
- **WHEN** an explicit host-RSS or cgroup-v2 profile matches the observed platform and capabilities
- **THEN** the harness records the profile and threshold decision and fails only on that profile's declared numeric conditions

#### Scenario: Profile is incompatible
- **WHEN** the selected profile requires an unavailable or mismatched platform capability
- **THEN** the numeric check is reported as not applied with a bounded reason while structural leak checks remain mandatory

### Requirement: Run evidence under the output root
Each invocation SHALL create one run-unique directory beneath `_out/performance/worker-memory-soak/`. It SHALL contain a configuration/capability manifest, ordered job/PID/outcome/finality records, artifact snapshots, Web sentinel counts, raw memory observations with source and timestamps, a trend/threshold summary, and test-result output. Failure evidence SHALL be retained, but artifacts MUST NOT contain credentials, environment/configuration values, coordinates, asset/cache/request/result payloads, raw protocol frames, raw stderr, secrets, or arbitrary exception text.

#### Scenario: Completed or failed run emits evidence
- **WHEN** the soak reaches normal completion or a leak/profile failure
- **THEN** its run directory contains sufficient bounded evidence to identify the iteration and failed condition without exposing prohibited data
