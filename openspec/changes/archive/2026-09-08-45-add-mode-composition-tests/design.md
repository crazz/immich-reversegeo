## Context

See [proposal.md](proposal.md) and [the deployment-mode composition test specification](specs/deployment-mode-composition-tests/spec.md). Finalized blocks 40–43 define the strict immutable startup decision and the Standard, Web-only, Run-once, and InternalWorker roots. Block 44 is a parallel implementation prerequisite whose landed Web status registrations must be consumed but not edited here. Blocks 41–43 already require focused per-mode descriptor and behavior tests; block 45 must add comparative evidence, not copy every predecessor edge case.

The matrix must inspect real production registrations far enough to detect graph drift while remaining independent of PostgreSQL, geodata files/downloads, Docker, child processes, HTTP sockets, and fixed ports. The composition roots and exact type names may differ when blocks 40–44 land, so apply begins with an inventory and stops rather than introducing parallel scheduler, coordinator, launcher, executor, status, or host abstractions.

## Goals / Non-Goals

**Goals:**
- Drive the authoritative pre-host decision and real role-specific registration roots through one table-shaped harness.
- Compare descriptors, provider identities, construction behavior, trigger observations, startup readiness, and disposal across all four roots.
- Prove missing/default and exact environment-value behavior, invalid early failure, and private-role precedence without process-global leakage.
- Prove scheduler, Web host, coordinator/launcher, executor/geodata, private protocol, and one-shot placement by both positive assertions and fail-on-construction sentinels.
- Keep normal rows parallel-safe and prove independent snapshots/providers do not leak state.

**Non-Goals:**
- Do not change public mode behavior, production registrations except for the smallest testability seam, persisted settings, UI, worker protocol, outcomes, or lifecycle policy.
- Do not reimplement exhaustive block-40 parser/redaction/persistence cases, block-41/42 focused lifecycle races, block-43 outcome/signal matrix, or block-44 rendering/transitions.
- Do not start a real Kestrel listener, call real HTTP, spawn the application/worker, open PostgreSQL, load/download geodata, or run Docker.
- Do not verify the production image, entrypoint, real port 8080, container UID, mounts, or process exit; block 46 owns those smoke tests.

## Decisions

### 1. Inventory and reuse the landed composition seam

Start by mapping block 40's one-read startup snapshot and the block 41–44 registration helpers/roots to four test inputs: Standard, Web-only, Run-once, and InternalWorker. Prefer an already landed internal factory that separates startup selection, descriptor registration, host-kind selection, validation/readiness, endpoint mapping, and role execution. If no such seam exists, extract only a side-effect-free composition-plan or registration callback from the executable entry point and keep production behavior unchanged.

The harness must invoke the real registration helpers. A test-only duplicate service list is rejected because it would remain green when production composition drifts. A full executable fixture is also rejected for the normal matrix because it would combine block-46 packaging/process concerns with hermetic graph assertions.

### 2. Use two evidence layers: descriptors first, controlled providers second

Descriptor assertions prove registrations and lifetimes without constructing services. They compare host kind and the presence/absence of Web/server/UI, scheduler concrete/hosted alias, coordinator, launcher/backend, detector, lifecycle/validator, executor, processing-geodata, block-44 Web status, and private controller-protocol contracts.

Controlled providers then verify alias identity, lazy construction, and selected behavior. Replace external/heavy leaves through explicit test overrides or landed interfaces: fake detector, child boundary, executor, startup prerequisites, host lifetime, clock/wait, paths, reporter, and geodata/database sentinels. Forbidden factories throw and increment counters so absence means both “not registered/reachable” and “not accidentally constructed.” Build a fresh provider per row and dispose it asynchronously where applicable.

Checking descriptors alone is rejected because a shared helper may activate a forbidden service indirectly. Starting every host is rejected because server and background-host activation obscures which registration caused the side effect.

### 3. Represent the matrix explicitly

Use one data model whose expected columns are:

| Root/input | Host kind | Scheduler | Coordinator/launcher | Executor/geodata | Protocol | Trigger observation |
|---|---|---|---|---|---|---|
| missing → Standard | Web | one singleton + hosted alias | present | absent from processing root | child client only | manual child; scheduled false/true gate |
| exact `standard` | Web | same as default | present | absent from processing root | child client only | equivalent to default |
| exact `web-only` | Web | absent | present | absent from processing root | child client only | manual child; zero automatic activity |
| exact `run-once` | non-Web one-shot | absent | absent | present directly | absent | one RunOnce executor call; no child/second pass |
| exact private token | non-Web worker | absent | absent | present | private controller transport | one controller-owned request boundary |

For Standard and Web-only, transitional Lookup/Data registrations from blocks 41–42 may still include heavy service descriptors. Classify them separately and assert they cannot be reached from scheduler/coordinator asset-processing roots; do not use “no geodata anywhere in Web” as an expectation before Phase 7.

The InternalWorker row is a private role-composition row, not a fourth public deployment mode. Test its public-mode read count with missing, accepted, and invalid values, but do not document or expose it as operator syntax.

### 4. Test startup selection without sharing process environment

Make the normal matrix call the same startup resolver through its narrow source abstraction using immutable dictionary-backed sources and read counters. This supplies the semantic states that a process environment exposes: missing/null, exact values, and exact invalid strings. Build and execute rows concurrently to prove each snapshot is read once and retained independently.

If the final executable boundary cannot be covered without `Environment.SetEnvironmentVariable`, isolate that small entrypoint fixture: capture whether the variable was missing versus its exact prior value, restore it in `finally`, serialize it with a dedicated non-parallel MSTest group, and never let it overlap other environment-sensitive tests. Do not mark the entire matrix non-parallel as a convenience; that would hide static caches and provider leakage.

A global lock around every row is rejected because it weakens the requested parallel-isolation evidence. Direct environment mutation in data rows is rejected because MSTest scheduling and failures can leak state across cases.

### 5. Separate pre-host failure from graph construction

Give the startup seam counters/sentinels for builder creation, provider creation, application logging, path resolution, settings reads, filesystem access, listener access, and work. Invalid public values must return the block-40 typed failure/exit classification with every counter at zero. The private preflight must return InternalWorker—or its existing reserved-syntax failure—with mode read count zero even when the supplied mode source would fail.

Do not duplicate every invalid-input assertion from block 40. Use representative matrix rows for each invalid class to prove the boundary, while block 40 remains authoritative for exact diagnostic text, canary redaction, persistence exclusion, and the exhaustive parser table.

### 6. Prove host type without binding a port

Host-kind evidence comes from the selected builder/factory path and service descriptors, not from listening sockets. Web rows may build descriptors/providers and, only if required by the landed seam, use an in-memory server substitute; they must never call production `RunAsync`, bind Kestrel, query a fixed/ephemeral port, or make HTTP requests. Non-Web rows assert that server features are absent and that Web URL/port settings are never accessed.

This differs deliberately from block 46: only a production-container run can prove actual port 8080 reachability or HTTP absence at the image boundary.

### 7. Compare structural scheduling and alias identity

For Standard, count scheduler descriptors and resolve the concrete singleton plus all applicable `IHostedService` aliases; the expected scheduler alias must be reference-identical to the concrete instance, with no duplicate scheduled trigger source. Verify the same alias rule for other finalized singleton/hosted lifecycle services where their landed contracts intentionally expose multiple service types.

For Web-only, Run-once, and InternalWorker, assert the scheduler concrete type and hosted alias are both absent. Also use throwing clock/wait, schedule-source, detector, and scheduled-pending fakes to catch a no-op scheduler or indirect activation. A runtime mode guard inside a registered scheduler is not acceptable evidence of structural exclusion.

### 8. Exercise only comparative trigger slices

Use one deterministic scenario set:
- Standard manual admission launches one fake child and never calls the detector.
- Standard detector-empty scheduling launches no child; detector-positive scheduling launches one.
- Web-only starts with enabled-valid, disabled, empty, and invalid saved schedules and records zero waits, detector calls, scheduled pending transitions, automatic children, or persistence changes; one manual admission launches one child.
- Run-once creates one fresh request with RunOnce trigger, invokes one fake executor once, and resolves no detector, coordinator, launcher, child bridge, or private protocol. Cover eligible and authoritative zero-work results only as needed to prove one attempt/no second pass.

Do not repeat contention, crash, detailed cleanup, full outcome precedence, real signal, or status rendering combinations already owned by blocks 41–44. The InternalWorker behavior row may verify the private controller/executor boundary is present, but must not spawn a process or retest protocol framing.

### 9. Treat startup validation and disposal as graph contracts

For both Web modes, expose the finalized validation/readiness transition to the harness. Success records one prerequisite/shutdown-budget validation and zero child, executor, geodata, or listener construction. Failure prevents acceptance readiness and work. For Run-once and InternalWorker, call only their landed required initialization with all foreign-root sentinels armed.

Provider construction alone must remain lazy. Dispose each scope/provider/host object exactly once and assert independent providers retain no static mode snapshot, singleton, fake counter, or owned resource. Detailed active-child and signal cleanup remains in predecessor suites.

## Risks / Trade-offs

- [The test seam becomes a second production composition root] → Extract only orchestration/registration decisions and require every row to call the same helpers as the executable.
- [Descriptor names differ after blocks 40–44 land] → Inventory exact applied types/lifetimes first and update expectations to those contracts rather than adding aliases solely for tests.
- [Transitional Lookup/Data geodata causes a false “Web is heavy” failure] → Assert processing-root reachability and construction, not blanket descriptor absence, until Phase 7.
- [Provider tests accidentally start background services or Kestrel] → Separate descriptor/provider construction from host start and use in-memory lifecycle seams only where behavior requires activation.
- [Environment tests are flaky under parallel MSTest] → Use immutable injected sources for normal rows; isolate and restore any unavoidable real-environment fixture.
- [Block 45 duplicates focused suites and becomes expensive] → Keep only comparative representative paths; link each omitted detail to its owning block.
- [Hermetic success is mistaken for deployability] → State the block-46 image/process/port/UID/volume boundary in test names and documentation.

## Migration Plan

1. Confirm blocks 40–44 are applied and inventory exact startup, composition, status, host, lifetime, and alias seams; stop if a prerequisite is missing.
2. Extract the smallest side-effect-free shared composition seam only if the landed code does not already expose one.
3. Add reusable descriptor expectations, provider overrides, construction sentinels, and immutable mode sources.
4. Add selection/precedence, composition/identity, trigger, validation, and parallel-isolation matrix rows.
5. Run focused block-45 tests, the normal repository test command, strict OpenSpec validation/status, and a scope review excluding block 44 and block 46 work.

No deployment or data migration exists. Reverting removes the test seam/tests without changing runtime configuration, settings, database, geodata, image, ports, or volumes.
