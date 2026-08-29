## Context

See `proposal.md` for motivation and `specs/worker-memory-soak-verification/spec.md` for the behavior contract. Finalized blocks 55–56 own the production control-plane boundary and deterministic constructor/index/native/geodata sentinels. Block 66 owns lifecycle telemetry and parent-side best-effort child `WorkingSet64` sampling. Block 67 owns the production-launcher-backed hermetic OS-process fixture, unique roots, PID/tree registration, stream drainage, controlled outcomes, and unconditional cleanup. Block 68 must compose those landed seams, not create a second launcher, fake child executable, telemetry owner, or production fault-injection path.

The soak is intentionally opt-in and potentially long. It needs enough repetition to expose accumulation while remaining reproducible and independent of live Overture/GADM downloads. Memory measurements are noisy and platform-dependent; structural ownership evidence is the cross-platform contract.

## Goals / Non-Goals

**Goals:**

- Sustain one production Standard/Web-only control process across warmup and measured mixed worker jobs.
- Exercise ProcessAssets as the majority workload and both v2 worker jobs at declared nonzero proportions through the real production process path.
- Gate each next launch on complete prior worker/process/stream/handle/filesystem finality.
- Produce reviewable structural and memory-trend evidence under one run-unique `_out` root.
- Permit explicit host-RSS and Linux cgroup-v2 profiles without hiding portable regressions behind noisy universal numbers.

**Non-Goals:**

- Define a universal RSS ceiling, expected slope, or monotonic-memory assertion.
- Claim block-66 samples are OS peak, process-tree, cgroup, container, or system memory.
- Benchmark resolver throughput, change production protocol/telemetry/composition, or add runtime soak controls.
- Contact live geodata services, test real downloads, or add another process/Docker harness.
- Edit block 67 or block 69, change required PR CI, or require a block-69 workflow; only an optional evidence handoff is produced.

## Decisions

### 1. Reuse the real production process path and landed fixture ownership

Extend block 67's process fixture orchestration so the parent is the exact production Standard/Web-only composition and each worker uses the production command builder, shell-free launcher, executable role, stdin request, stdout protocol reader, classifier, telemetry, and disposal path. Fixture seams replace external data/dependency inputs only; they do not substitute a synthetic child or branch production behavior on a test selector. At apply start, bind to landed names and stop if this would require a second launcher or production fault seam.

Alternative: loop a lightweight helper process. Rejected because process cleanup evidence would not cover the worker's production composition and geodata ownership.

### 2. Make iteration shape explicit, seeded, and auditable

A run configuration requires positive warmup and measured counts, a deterministic seed, and integer job weights. Validation requires ProcessAssets to exceed 50 percent and each v2 job kind to exceed zero; the manifest records normalized proportions and the exact generated sequence. Warmup uses the same mix and finality checks, but trend baselines/counters reset after warmup so JIT and fixture initialization do not silently become measured growth. CLI/environment names are bounded harness inputs and their resolved non-secret values are copied to the manifest.

Successful mixed jobs are mandatory. Cooperative cancellation and controlled pre-publication CacheMutation failure are separately enabled cycle types, interleaved deterministically after warmup, never automatically retried, and subject to identical cleanup gates. This adds ownership stress without turning every basic smoke run into a failure matrix duplicate.

Alternative: fixed unreported loop counts or random job selection. Rejected because trends and regressions would not be reproducible or comparable.

### 3. Use deterministic no-network fixtures while loading real worker composition

Create run-local immutable/minimal fixtures accepted by the landed production process seams: a bounded ProcessAssets data set, local validated country/division/airport/GADM or disabled-source cache inputs as applicable, CoordinateLookup points with deterministic expected local results, and CacheMutation source/candidate bytes that exercise validation and atomic publication without transport. Install a fail-fast no-network/download/export sentinel. Every job receives isolated config/data/work paths under its run root; immutable fixture inputs may be copied or hard-linked only when the final evidence can distinguish source files from attempt artifacts.

The soak validates resource ownership rather than geodata correctness breadth. Fixture size/profile is recorded. An optional full-load local fixture may be supplied externally, but it remains no-network and does not change the structural contract.

Alternative: pre-populate shared developer caches or download during the run. Rejected because results become non-hermetic and cleanup ownership becomes ambiguous.

### 4. Treat job finality as a launch barrier

Register each process before start and require a PID not previously observed in the run. After the authoritative terminal/failure observation, wait through the existing bounded production termination path, both stream drains, event bridge/telemetry finality, launcher/session disposal, process-tree absence, and closed fixture handles. Snapshot the isolated workspace before/after and reject remaining attempt-owned temp, download, candidate, staging, journal, or sidecar artifacts. Only expected immutable/final fixture outputs may remain. Starting iteration N+1 before iteration N passes this barrier is a harness failure.

The fixture's unconditional cleanup remains a safety net, but needing fallback kill/delete is itself a soak failure and is recorded. This prevents teardown from making a leak look like success.

Alternative: poll only the direct PID. Rejected because descendants, pipes, handles, and candidate files can outlive it.

### 5. Keep structural leak failures portable and immediate

The mandatory failure set is: duplicate/reused PID in the run, missing bounded exit/drain/disposal, fallback orphan cleanup, surviving direct child or descendant, leaked handle, accumulated attempt-owned artifact, a live-network sentinel, or any forbidden block-55/56 Web constructor/factory/index/native/geodata/mutation/in-process-execution count. The control process remains alive for the entire phase; sentinel and memory observation baselines reset after warmup, then are emitted after every measured job and at finality.

RSS growth alone never substitutes for a constructor sentinel and a flat RSS line never excuses a structural leak. This anchors leak detection to ownership facts rather than allocator behavior.

### 6. Report memory trends with source-specific caveats

Collect monotonic-time-stamped Web `WorkingSet64` observations at phase boundaries and after each job. Consume, do not duplicate, block 66's child memory observation: successful samples and explicit unavailable reasons, sample count, method/scope, and its 1000-ms interval. For short-lived workers, reports call out that only immediate-start and opportunistic-finality samples may exist. Summaries include counts, min/median/max where available, first/last and simple deltas/trend description, but default assertions do not fail on these values or a fitted slope.

An optional external JSON platform profile declares an id/version/provenance, OS/architecture/runtime/container expectations, measurement source, units, warmup treatment, minimum sample/iteration conditions, and numeric limits. Supported sources are controller host RSS and Linux cgroup-v2 files such as `memory.current`/`memory.peak` when capability probes confirm the intended cgroup scope. A mismatch or unavailable source produces a bounded not-applied decision, not a guessed fallback. Structural checks always remain active.

Alternative: check one global maximum or percentage. Rejected because runtime, allocator, workload, host, and cgroup semantics differ.

### 7. Separate the soak from ordinary test selection

Mark all cases `Performance`. Preserve `.runsettings` exclusion of Integration and Performance and `integration.runsettings` exclusion of Performance. Add a dedicated `performance.runsettings` selecting only Performance plus an explicit task-runner command targeting the Web test project, for example:

`dotnet test --project tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj --settings performance.runsettings --filter "TestCategory=Performance" --results-directory _out/performance/worker-memory-soak/test-results`

Document required harness inputs and a short validation profile. The command must be opt-in and must not be called from `npm run test`, `npm run test:integration`, or existing required CI in this change.

### 8. Emit one redacted evidence bundle

Use `_out/performance/worker-memory-soak/<run-id>/` as the only output root. Emit a manifest, ordered NDJSON/CSV job and memory observations, process/finality and artifact snapshots, sentinel counts, trend/profile decision summary, a concise human-readable summary, and test results. Atomic/flush-safe writing ensures partial failure evidence remains readable. The report records relative fixture/artifact identities, bounded codes, PIDs, counts, durations, sizes, and threshold decisions; it excludes raw requests/protocol/stderr, coordinates, credentials, arbitrary environment/config values, paths outside the run root, secrets, and exception text.

The bundle is sufficient for an optional later block-69 scheduled/full-load/cgroup consumer, but block 68 neither chooses CI cadence nor edits workflows.

## Risks / Trade-offs

- [Prerequisite fixture/sentinel names differ after apply] → Re-read applied 55–56 and 66–67, bind to their exact owners, and stop instead of duplicating seams.
- [Short workers provide sparse child samples] → Preserve explicit sample counts/unavailable reasons and the 1-second caveat; rely on process/artifact/sentinel facts for portable failure.
- [JIT/cache warmup distorts trends] → Use explicit warmup with the same mix, reset measured baselines, and record both phase counts.
- [A teardown safety net hides a leak] → Mark any fallback kill/delete/handle recovery as failure before cleanup completes.
- [A profile is accidentally treated as portable] → Require explicit selection, exact capability match, provenance, and a recorded applied/not-applied decision.
- [Full-load local fixtures are large] → Keep them external and optional; the committed/default soak fixture stays minimal and deterministic.
- [Evidence leaks sensitive data] → Use bounded structured fields and redaction scans matching block 66; never retain raw payload or stderr.

## Migration Plan

1. Re-read the applied block-55/56 policy/sentinels, block-66 memory event shape, and block-67 fixture/finality APIs; document exact extension points and stop on an ownership mismatch.
2. Add dedicated Performance selection, validated run configuration, and run-unique `_out` evidence ownership while preserving existing default/Integration exclusions.
3. Add deterministic mixed local fixtures and no-network sentinels, then production-path warmup/measured orchestration with the per-job finality barrier.
4. Add Web sentinel/memory observations, block-66 child observation ingestion, trend reports, and optional capability-checked host-RSS/cgroup-v2 profiles.
5. Add optional deterministic cancellation/failure cycles and verify they use existing block-67 controls with no retry or production changes.
6. Run a short focused profile and the explicit soak command, inspect retained evidence, run default/Integration exclusion checks, strict validation/status, and a block-68-only diff review.

Rollback removes only the Performance harness, its runsettings/task-runner entry, and maintainer invocation notes. No production process, protocol, data, or deployment migration is involved; `_out` remains disposable local output.
