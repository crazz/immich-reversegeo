# Repeated worker memory soak

This optional check runs real disposable workers through the production job pipeline while one test process keeps the production Standard Web composition alive. It checks resource ownership between jobs and records memory observations. It does not establish a memory budget for a deployed Immich ReverseGeo image.

## Run a soak

From the repository root:

```sh
npm run test:performance:worker-memory
```

The command explicitly selects one Performance test in the Web test project. Defaults are seed `6801`, five warmup jobs, and ten measured jobs. Each five-job round contains three ProcessAssets jobs, one CoordinateLookup job, and one CacheMutation job; the seed determines their order.

Each invocation creates a unique directory under `_out/performance/worker-memory-soak/`. The runner also preserves its console log under `_out/agent-tests/` and uses `_out/performance/worker-memory-soak/test-results` for test-platform output. Normal tests, Integration tests, and required CI exclude Performance.

To run the complete harness validation matrix, including deliberate failure cases:

```sh
npm run agent:test -- --performance --filter TestCategory=Change68
```

This second command creates multiple bundles. Some tests deliberately produce failed soak results and pass by verifying that failure; use the test runner's exit code for the validation matrix. A normal configured soak succeeds only when its own `test-result.json` says `passed: true`.

## Configure repetition

Save a configuration file under `_out/`, for example:

```json
{
  "seed": 6801,
  "warmupIterations": 10,
  "measuredIterations": 50,
  "processingWeight": 3,
  "lookupWeight": 1,
  "cacheWeight": 1,
  "cancellation": true,
  "failure": true
}
```

Then select it with an absolute path from the repository root. The test process may run from a different working directory:

```sh
IMMICH_REVERSEGEO_SOAK_CONFIG="$(pwd)/_out/soak-config.json" npm run test:performance:worker-memory
```

On PowerShell:

```powershell
$env:IMMICH_REVERSEGEO_SOAK_CONFIG = (Resolve-Path '_out/soak-config.json').Path
try {
    npm run test:performance:worker-memory
}
finally {
    Remove-Item Env:IMMICH_REVERSEGEO_SOAK_CONFIG
}
```

Counts must be positive multiples of the sum of the weights, with at most 100,000 jobs per phase. Each weight must be between 1 and 100; Processing must exceed the other two weights combined. Failure cycles require at least two measured CacheMutation jobs so a successful peer remains. Warmup jobs always succeed. When enabled, every third measured Processing job is cooperatively cancelled and every second measured CacheMutation job fails before publication. Neither is retried automatically.

Optional `outputRoot` must remain beneath the fixed soak output tree. `inputDirectory` selects external local fixture files, and `thresholdProfile` selects an external calibration file. Unknown or duplicate configuration keys are rejected. Paths and the contents of configuration files are not copied into evidence.

## Workload and measurement boundaries

The existing process fixture supplies a closed descriptor to `ChildWorkerLauncher`. Its apphost invokes the real `InternalWorkerHost`, job handlers, processing/lookup operations, protocol, and terminal generation. The test harness controls external data adapters and command selection. It does not execute an unchanged deployed apphost or the production command builder.

ProcessAssets uses a local SQLite asset fixture, real processing execution, local GADM queries, and writes to an isolated result database. Lookup uses the same local geodata through the real lookup operation. CacheMutation uses the existing local GeoPackage/export/validation/publication fixture. The default inputs are small; this validates ownership rather than representative production memory usage. No job downloads geodata or performs a network export.

The Web composition is checked against the existing control-plane policy before fixture adapters are installed. Existing counting sentinels reject worker-only factories, indexes, geodata services, and in-process execution. Warmup uses the same finality checks; measured sentinel and memory baselines begin after it.

Every next launch waits for the previous job's authoritative outcome, native exit, stdout/stderr drains, event bridge, best-effort telemetry join, process disposal, registered process-tree checks, released file handles, and workspace checks. Reused PIDs, surviving registered descendants, fallback cleanup, and attempt-owned files fail the run. Fixture inputs, validated final databases, and the fixture's closed diagnostic files are accounted for separately. Closed descendant modes use the existing fixture registration marker; a future fixture that launches descendants must preserve that registration contract.

Parent `WorkingSet64` covers the whole MSTest process containing Web composition, including harness overhead. Child observations come from block 66's sampler and cover the fixture apphost plus the production pipeline. It samples after start, every second, and opportunistically at finality. A short worker may have only start/finality observations. Its sampled maximum is not an OS peak or process-tree, container, cgroup, or system memory measurement.

Parent and cgroup observations are taken at job boundaries. Child records retain the complete available/unavailable shape and successful sample count. Timestamps use the monotonic clock and the frequency recorded in the manifest. Summary groups separate warmup/measured phases, job kinds, and memory sources. Without a compatible explicit profile, memory growth remains diagnostic and cannot fail the run.

## External local fixtures

For a larger local workload, `inputDirectory` must contain three nonempty regular files:

| File | Required shape |
|---|---|
| `assets.db` | SQLite `assets` table with `id`, `latitude`, `longitude`, `country`, `state`, and `city`; canonical GUID IDs and unprocessed rows with null country. |
| `gadm.db` | A valid exported GADM `CHE` division cache matching the asset and lookup points. |
| `source.gpkg` | Local GADM GeoPackage source accepted by the existing exporter for `CHE`, used by CacheMutation. |

Each worker receives copies. The originals remain unchanged, input hashes are recorded, and immutable worker copies are checked after each job. Inputs must already exist locally; the soak does not prepare them through a live download. Keep generated or large data outside Git. The fixed local lookup point and country make this a fixture profile, not a general arbitrary-country import tool.

## Optional numeric profiles

Calibrate against a representative successful run on the same runtime, inputs, platform, and container context. Keep the calibration report outside the evidence bundle and reference its SHA-256. There is no built-in recommended RSS limit.

The profile is a JSON object with these fields:

| Field | Value |
|---|---|
| `platform`, `architecture`, `runtime`, `container` | Exact values from the manifest's `capabilities`. |
| `workerExecutableSha256` | Exact value from the manifest. |
| `workerRuntimeSha256`, `inputProfileSha256` | Exact values from `capabilities`; the runtime hash covers ordered runtime file identities and contents, including DLLs and native dependencies. |
| `calibrationSha256` | Uppercase SHA-256 of the calibration report. |
| `scope` | `TestHostWorkingSet`, `FixtureWorkerWorkingSet`, or `LinuxCgroupV2MemoryCurrent`. |
| `aggregation` | `Maximum` or `FirstLastDelta`, using ordered available measured observations. |
| `minimumSamples` | At least 2 available observations from the selected source. |
| `excludeWarmup` | Must be `true`. |
| `limitBytes` | The explicitly chosen nonnegative byte limit for that aggregation. |

For child working set, each observation is one worker's block-66 sampled maximum, with its separate successful-sample count. For cgroup v2, the harness probes the current test-host membership in the standard unified hierarchy and reads `memory.current` when available. That scope includes descendants and any other members of the same cgroup; it is not per-worker RSS. Unsupported mount layouts remain unavailable instead of falling back to a guessed path.

An incompatible profile or unavailable capability produces a bounded not-applied decision. Too few samples also leaves the numeric check unapplied. Invalid profile content fails configuration. A compatible profile can add a numeric failure; it cannot excuse a structural failure.

## Inspect evidence

| File | Contents |
|---|---|
| `manifest.json` | Resolved non-secret configuration, normalized proportions, capabilities, input/runtime identities, and sampling limits. |
| `starts/000001.json` | Atomic record of the iteration and native PID before startup/finality waits. |
| `jobs/000001.json` | Outcome, finality, artifact hashes, sentinel counts, and sourced memory observations for a settled job. |
| `summary.json` | Per-phase/job-kind/source trends, threshold decision, and structural failures. |
| `test-result.json` | Run result, completed count, last iteration, and bounded execution stage. |

Partial evidence survives a failure. Records exclude coordinates, asset/cache/request/result payloads, raw protocol, raw stderr, credentials, arbitrary environment/configuration values, external paths, and exception text. Artifact identities and contents are represented by hashes. The `negative-harness/` subtree contains deliberate redaction/ownership self-test artifacts.

A later optional full-load or cgroup job can retain these files and check the configured run's `test-result.json` before interpreting trends. Change 68 does not add a workflow or make the soak a required CI check.
