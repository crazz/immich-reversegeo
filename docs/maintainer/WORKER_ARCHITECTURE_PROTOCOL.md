# Worker architecture and protocol

This repository-only reference describes landed contracts and their enforcing evidence. Start with the [architecture overview](ARCHITECTURE.md) for the project map and main execution flows. Supported operator configuration and recovery are in the [deployment-mode guide](../website/deployment-modes.md). Private controls below belong to the controller; they are not operator commands.

## Composition and ownership

| Role | Composition root | Owns | Excludes |
| --- | --- | --- | --- |
| Standard | [Web composition](../../src/ImmichReverseGeo.Web/Composition/WebServiceCollectionExtensions.cs) | Web UI, lightweight control state, full current-eligibility scheduled checks, temporary worker clients | Heavy geodata/executor initialization in the Web host |
| Web-only | Same Web composition, scheduler registration omitted | The same UI, manual processing, Lookup and heavy Data clients; saved schedules remain intact | Internal scheduler and heavy Web execution |
| InternalWorker | [Internal worker composition](../../src/ImmichReverseGeo.Worker/Composition/InternalWorkerServiceCollectionExtensions.cs) | One private protocol job, its heavy handlers and owned resources | Web UI/listener and recursive child launch |
| Run-once | [Direct composition](../../src/ImmichReverseGeo.Worker/Composition/RunOnceServiceCollectionExtensions.cs) | One authoritative processing attempt in the invoking process | Web listener, child worker, detector precheck, second pass or internal retry |

[Host dispatch](../../src/ImmichReverseGeo.Host/Program.cs) selects the root before startup. Core contains shared lightweight contracts; the Web library builds as `ImmichReverseGeo.Web.ControlPlane`, while Host supplies the runnable `ImmichReverseGeo.Web` artifact. Public modes are exactly `standard`, `web-only`, `run-once`; only an absent public mode variable selects Standard. Selection is startup-only.

The Web boundary permits these policy categories: **transport, job client, inventory, metadata SQLite, skipped SQLite, maintenance, deletion, configuration, UI state, identity, country display, profiles, lazy PostgreSQL, lazy provider**. Metadata SQLite access is bounded schema/metadata inspection without pooling, and skipped SQLite stores control state. A permitted contract is an entry into dependency traversal, not an exemption for its implementation.

The Web boundary forbids **in-process executor, administrative resolver, country index, Overture query/download/export/mutation, airport infrastructure, GADM query/geometry, GADM download/export/mutation, processing handler, lookup handler, cache mutation handler**. Worker/Overture/Gadm implementation assemblies, DuckDB, NetTopologySuite, GeoJSON and opaque activation cannot enter the Web dependency closure. Worker and Run-once roots may compose the heavy execution services they need, but exclude Web presentation/control ownership.

[Dependency policy](../../tests/ImmichReverseGeo.Tests/ApplicationComposition/ControlPlaneDependencyPolicy.cs), [role checks](../../tests/ImmichReverseGeo.Tests/ApplicationComposition/ControlPlaneRolePolicyTests.cs) and [runtime sentinels](../../tests/ImmichReverseGeo.Tests/ApplicationComposition/BoundaryRuntimeSentinel.cs) enforce this through project/package/assembly closure, factory IL inspection without executing the factories, and counted runtime initialization. Adjacent negative controls prove that forbidden paths are detected. See the [boundary reference](web-control-plane-boundary.md) for the detailed inspection model.

## Selection, jobs and identity

| Wire generation | Selection owned by the controller | Closed jobs | Canonical identity |
| --- | --- | --- | --- |
| Frozen v1, version `1` | Private protocol-version variable absent | `ProcessAssets` | `runId`, the processing RunId |
| v2, version `2` | Private protocol-version variable exactly `2` | `ProcessAssets`, `CoordinateLookup`, `CacheMutation` | `jobId`; ProcessAssets reuses its RunId |

Both use protocol identifier `immich-reversegeo.worker` and the sole exact private argument `--internal-worker`. The private variable is `IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION`: absence selects v1; every present value except exact `2` fails before readiness. It is not negotiation, public configuration, a persisted setting or a logging field. Do not construct private invocations manually.

The [selector](../../src/ImmichReverseGeo.Core/WorkerJobs/InternalWorkerProtocolVersion.cs) and [role parser](../../src/ImmichReverseGeo.Core/ApplicationRole/ApplicationRoleSelection.cs) define these rules. The [invocation builder](../../src/ImmichReverseGeo.Web/WorkerCommandInvocation/WorkerCommandInvocation.cs) owns shell-free argument construction; the [process factory](../../src/ImmichReverseGeo.Web/ChildWorkerLaunching/SystemChildProcessFactory.cs) removes the inherited reserved selector for v1 and sets it to `2` for v2. The selected trusted application artifact is reused for the child.

[Job contracts](../../src/ImmichReverseGeo.Core/WorkerJobs/WorkerJobContracts.cs) use closed origins `Manual`, `Scheduled`, `RunOnce`. All three descriptors require `ExclusiveHeavyWorker`. Only ProcessAssets absolute-count progress is replaceable. There is no extra attempt, lease, cancellation or telemetry identity. [Registry/selection tests](../../tests/ImmichReverseGeo.Tests/WorkerJobs/WorkerJobRegistryAndSelectionTests.cs) and [v2 contract tests](../../tests/ImmichReverseGeo.Tests/WorkerJobs/WorkerJobProtocolV2Tests.cs) enforce the matrix.

## Streams, validation and delivery

The controller writes child stdin and drains child stdout/stderr. The worker reads stdin, owns managed stdout exclusively for protocol frames, and sends human diagnostics to stderr. Stderr is not a protocol or automation API. The controller starts draining both output streams without waiting for process exit.

Both generations use strict UTF-8 without a BOM and one JSON object per NDJSON frame. Output ends each frame with LF; input accepts CRLF. The maximum is **1,048,576 content bytes**, excluding the line delimiter, not a character count. Oversized, partial, malformed, incorrectly encoded or invalid lifecycle input produces bounded typed failure, never echoed payload content.

The worker emits and flushes `ready` first, at output sequence 1. The controller validates readiness and capabilities before sending one `execute`; its input sequence starts independently at 1, and a correlated `cancel` is sequence 2. Sequences advance by exactly one and cannot overflow; timestamps cannot regress. After readiness, the accepted start establishes the job identity and ordering. For ProcessAssets, eligibility precedes absolute progress; activity/log events obey their lifecycle rules. Wrong correlation, duplicate starts, reordered events or events after terminal are rejected. Validator rejection does not advance accepted validation state.

The emitter has a bounded FIFO of 256 candidates and a single writer. Admission transfers ownership to that writer; each frame is serialized, written and flushed in order. Ready is usable only after its flush. Terminal acceptance closes intake, drains earlier accepted work and leaves terminal last. Transport failure breaks the stream; it does not retry a frame or fabricate a successful terminal. The emitter's flushed sequence advances only after successful write and flush.

On the Web side, every raw frame is validated before any progress replacement. A bounded FIFO of 256 lossless events and a latest absolute-progress snapshot preserve ordering across lossless barriers. Terminal seals intake and is delivered after prior accepted events; delivery is joined before settlement releases admission. UI notifications use a 100 ms cadence; renderer completion is not wire finality. See [event delivery](worker-event-delivery.md).

**EOF is not Cancel.** Before a request is accepted, stdin EOF is invalid input (managed exit 2). After accepted execute, complete stdin EOF closes the control channel without cancelling the job. Partial EOF, codec, validator and read faults have typed input finality; none synthesizes a Cancel request. A job still needs its proper output terminal. The parent treats missing stdout terminal as a separate failure, rather than interpreting EOF as success. Terminal finality stops the worker's input pump.

v1 preserves its additive-property compatibility. v2 rejects unknown envelope/payload properties as `InvalidEnvelope`/`InvalidPayload`; do not generalize v1 compatibility into v2. These are independent frozen rules.

Evidence: [v1 constants](../../src/ImmichReverseGeo.Core/WorkerProtocol/WorkerProtocolV1.cs), [v2 constants](../../src/ImmichReverseGeo.Core/WorkerJobs/WorkerJobProtocolV2.cs), [sequence validation](../../src/ImmichReverseGeo.Core/WorkerProtocol/WorkerProtocolSequence.cs), [v1 lifecycle validator](../../src/ImmichReverseGeo.Core/WorkerProtocol/WorkerProtocolEventStreamValidator.cs), [emitter](../../src/ImmichReverseGeo.Worker/WorkerHost/WorkerNdjsonOutput/WorkerNdjsonEmitter.cs), [framing and encoding tests](../../tests/ImmichReverseGeo.Tests/WorkerProtocol/Compatibility/FramingAndEncodingTests.cs), [compatibility tests](../../tests/ImmichReverseGeo.Tests/WorkerProtocol/Compatibility/DecodeCompatibilityTests.cs), [stdin finality tests](../../tests/ImmichReverseGeo.Tests/WorkerStdinRequestLoop/WorkerStdinControlsAndFinalityTests.cs), and [raw delivery boundary tests](../../tests/ImmichReverseGeo.Tests/ChildWorkerLaunching/WorkerEventDeliveryRawBoundaryTests.cs).

## Managed exits and parent finality

| Managed code | Worker outcome |
| --- | --- |
| 0 | Completed, including no work |
| 2 | Invalid invocation or input |
| 3 | ProcessAssets advisory-lock Busy; no pass ran |
| 4 | Executor/domain failure |
| 5 | Startup, dependency, infrastructure or cleanup failure |
| 6 | Output transport failure |
| 130 | Orderly cancellation/shutdown |

The [outcome ranks](../../src/ImmichReverseGeo.Core/WorkerProcessExitOutcomes/WorkerProcessExitOutcome.cs) combine facts in exact precedence **6 > 5 > 2 > 3 > 4 > 130 > 0**. These are managed worker exits, not a promise that every OS termination yields one of them. Abrupt death may have a raw platform status. Public Run-once has no NDJSON output-transport exit 6; its supported automation contract is in the public guide.

Keep three observations distinct: accepted wire terminal, process exit/stream evidence, and controller settlement. A valid Failed terminal plus exit 3 is the expected ProcessAssets lock-busy combination. Once a valid terminal commits, later exit or drain evidence cannot rewrite it. Without a committed valid terminal, the parent classifies startup, protocol, transport, missing-terminal, crash or forced-stop evidence and finalizes the request exactly once. There is no automatic replacement, replay or retry. Committed database/cache effects are retained.

Evidence: [terminal authority matrix](../../tests/ImmichReverseGeo.Tests/WorkerProcessFailureMatrix/ProcessFailureMatrixTerminalAuthorityTests.cs), [failure precedence matrix](../../tests/ImmichReverseGeo.Tests/WorkerProcessFailureMatrix/ProcessFailureMatrixFailurePrecedenceTests.cs) and [lock executor tests](../../tests/ImmichReverseGeo.Tests/ProcessingRunLocking/ProcessingRunLockExecutorTests.cs).

## Cancellation and resource lifetime

One parent-owned session/handle retains process, streams and admission through finality. Repeated stop requests share the same stop task and first deadline. Cooperative cancellation is eligible only for a cancellable job after readiness and successful execute write/flush; it uses the same canonical identity. A failed write or flush is not proof that the worker did not accept bytes: fault containment closes input while continuing to drain valid output.

The fixed grace is **10 seconds**, measured with `TimeProvider` from the first termination request. At its deadline, a still-live worker is subject to whole-process-tree escalation. Cancellation does not get a fresh grace period on repeated requests. An observed exit avoids needless killing. Failure to kill is classified, never treated as successful cleanup.

The parent observes exit, completes both drains and accepted-event delivery, disposes owned resources and releases admission only at settled finality. EOF, force-stop and disposal cannot manufacture a terminal; a previously accepted terminal remains authoritative. Production shutdown does not depend on best-effort log delivery. Test harnesses separately join telemetry before making log assertions.

Evidence: [cancellation policy](../../src/ImmichReverseGeo.Web/ChildWorkerLaunching/ChildWorkerCancellationContracts.cs), [session cancellation](../../src/ImmichReverseGeo.Web/ChildWorkerLaunching/ChildWorkerSession.Cancellation.cs), [deadline tests](../../tests/ImmichReverseGeo.Tests/ChildWorkerCancellation/SessionDeadlineTests.cs), [descendant matrix](../../tests/ImmichReverseGeo.Tests/WorkerProcessFailureMatrix/ProcessFailureMatrixDescendantTests.cs), [pipe matrix](../../tests/ImmichReverseGeo.Tests/WorkerProcessFailureMatrix/ProcessFailureMatrixPipeTests.cs) and [host shutdown matrix](../../tests/ImmichReverseGeo.Tests/WorkerProcessFailureMatrix/ProcessFailureMatrixHostShutdownTests.cs).

## Exclusion and cache publication

| Mechanism | Scope | Lifetime and limitation |
| --- | --- | --- |
| Local heavy-work arbitration | One Web process; all three jobs and coordinated maintenance | First owner wins, Busy immediately, no queue; held through settled cleanup |
| PostgreSQL advisory run lock | ProcessAssets passes using the same Immich database and key, including direct Run-once | Dedicated session from successful acquisition through protected execution/terminal reporting, then explicit release/disposal |
| Atomic cache publication | One destination cache file | Validated same-directory candidate replaces the destination atomically; not a distributed lock |

The advisory key is **-7970420658158250032**, hexadecimal **0x916360A3F80AD7D0**. It derives from the first eight SHA-256 bytes of `immich-reversegeo/postgresql-advisory-run-lock/v1`, interpreted as a signed big-endian 64-bit value. The full digest is `916360a3f80ad7d0ae2a32661692f1381e43b2f336f19a58491ce5582ffb9dbf`. Acquisition uses nonblocking `pg_try_advisory_lock`; release uses `pg_advisory_unlock`. A busy lock skips domain execution and produces exit 3. Loss of the dedicated session is an infrastructure failure, not permission to continue unlocked.

[Lock commands](../../src/ImmichReverseGeo.Web/ProcessingRunLocking/ProcessingRunLockCommands.cs), [session ownership](../../src/ImmichReverseGeo.Web/ProcessingRunLocking/PostgresqlProcessingRunLock.cs), [real PostgreSQL integration tests](../../tests/ImmichReverseGeo.Tests/ProcessingRunLocking/PostgresqlRunLockIntegrationTests.cs) and [local arbitration tests](../../tests/ImmichReverseGeo.Tests/WorkerJobArbitration/WorkerJobCoordinatorContractTests.cs) enforce these scopes. The global processing lock does not coordinate cache maintenance, resets, multiple Web instances or independent writers. Operate one Web controller without independent mutation of its shared volumes; do not infer a distributed maintenance lock.

Cache exporters create a unique owned candidate beside the destination, close writer handles and validate it before publication. The [atomic publisher](../../src/ImmichReverseGeo.Core/WorkerJobs/AtomicCacheFilePublisher.cs) uses replacement rename on Unix and `MoveFileEx` replacement on Windows, with no delete-then-move fallback. Pre-publication failure or cancellation preserves the previous cache and cleans only the owned candidate. Cancellation after publication may leave the valid new cache in place. [Publication tests](../../tests/ImmichReverseGeo.Tests/WorkerJobs/AtomicCacheFilePublisherTests.cs), [candidate ownership tests](../../tests/ImmichReverseGeo.Tests/WorkerJobs/CacheCandidateOwnershipTests.cs) and the [cache failure matrix](../../tests/ImmichReverseGeo.Tests/WorkerProcessFailureMatrix/ProcessFailureMatrixCacheTests.cs) cover those boundaries.

## Telemetry and safe investigation

| EventIds | Meaning |
| --- | --- |
| 5901 | Scheduled full current-eligibility observation; separate from a worker attempt |
| 6601, 6602, 6603 | Mode selected, role starting, role ready |
| 6604 | Initial reason for stopping, recorded once |
| 6605 | Final role outcome after cleanup; a late failure can make it failed without a second 6604 |
| 6610, 6611, 6612 | Launch requested, child process started, worker ready |
| 6620, 6621, 6622, 6623 | Cancellation requested, grace completed, escalation, forced-stop result |
| 6630 | Bounded protocol violation category/position |
| 6640 | Accepted terminal observation |
| 6641 | Process classification after exit and drains, with bounded memory observation |
| 6650 | One final saturation observation if FIFO enqueue waited or a snapshot was replaced, including lossless-only waits |

Use the canonical identity with closed job kind/origin and available process IDs to correlate these events. [Lifecycle catalog](../../src/ImmichReverseGeo.Web/LifecycleTelemetry/LifecycleEventCatalog.cs), [scheduled observation](../../src/ImmichReverseGeo.Web/Services/InstrumentedProcessingWorkDetector.cs) and [telemetry tests](../../tests/ImmichReverseGeo.Tests/LifecycleTelemetry/) define the bounded vocabulary.

Event 6641's memory value is a child-process-only maximum of successful working-set samples at a 1-second interval. Missing samples are explicitly unavailable. It is not an absolute, process-tree, OS/container or cgroup peak, and does not establish a universal RSS drop or total-memory saving.

1. Record only the existing bounded/redacted telemetry for the canonical job identity, kind/origin, readiness, terminal and process classification.
2. Separate initial stop reason 6604 from final outcome 6605; separate terminal 6640 from later process classification 6641. Inspect 6630 for a closed failure category, without capturing a frame.
3. Match cancellation observations to the single grace deadline and the final drain/disposal evidence. Treat 6650 as evidence of waiting/replacement, not lost terminal data or a retry signal.
4. Reproduce with the narrowest linked compatibility, failure-matrix, lock or composition test. For container/environment failures, use the canonical Docker producer below. Keep attempt outputs separate, including failed attempts.
5. Compare image, platform, fixture and profile identities before carrying a result into release wording. A plan or unrun profile is not evidence.

Never hand-edit or replay protocol frames, invoke private selectors as public operations, or capture raw streams/tails. Diagnostics must exclude arguments, environment/configuration, payloads, coordinates, runtime paths, SQL, credentials, connection strings, tokens, arbitrary exception text and stacks. Static repository links in this guide are source references, not permission to log runtime paths.

## Evidence producers and verification

| Evidence | Producer and retained boundary |
| --- | --- |
| Protocol compatibility | [Compatibility suite](../../tests/ImmichReverseGeo.Tests/WorkerProtocol/Compatibility/) and [v2 tests](../../tests/ImmichReverseGeo.Tests/WorkerJobs/WorkerJobProtocolV2Tests.cs); retain each version's own unknown-field rules |
| Process failures/finality | [Failure matrix](../../tests/ImmichReverseGeo.Tests/WorkerProcessFailureMatrix/); build-output and published fixtures across the CI OS matrix |
| Production image modes | [Canonical script](../../scripts/docker-mode-smoke.sh), [SQL fixture](../../tests/docker-mode-smoke/fixture.sql), [required CI job](../../.github/workflows/ci.yml); command `npm run test:docker-smoke`, one build/immutable image ID across modes, bounded diagnostics and resource cleanup |
| Memory/lifetime soak | [Selected soak guide](worker-memory-soak.md) and its linked tests; record fixture, profile, platform and sampler limits |
| Public operation | Final deployment-mode guide linked above; `npm run docs:build` verifies its generated route |

The Docker producer retains results under `_out/docker-mode-smoke/`. The CI workflow uploads that producer's bounded `evidence/` files on failure, with seven-day retention; a successful CI job does not imply a downloadable artifact. Use its job log, and retain local receipts separately when needed. There is no separate `_out/docker-mode-integration/` producer. Selected soak outputs live under `_out/performance/worker-memory-soak/`. These are gitignored local evidence roots, not portable source links or proof that every profile ran. Use the produced receipt/run link with its image identity when reporting a result.

For edits to this guide, compare the documented selector/version, job vocabulary, frame limit, exit ranks, advisory key/derivation, grace, EventIds and dependency categories against the linked source/tests. Resolve all repository links and the public cross-link, then build the site. Keep `docs/maintainer/` outside `mkdocs.yml` navigation (`docs_dir` remains `docs/website`). Correct documentation from landed evidence; do not change a runtime contract to make a documentation check pass.
