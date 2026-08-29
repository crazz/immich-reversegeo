## Context

See `proposal.md` for motivation and `specs/worker-job-envelope/spec.md` for required behavior. Applied blocks 15–30 define a strict v1 processing protocol and a one-shot child lifecycle. Important invariants are: v1's closed vocabulary and canonical framing; `runId` as its sole correlation identity; process-scoped `ready`; exactly one worker-emitted terminal only after an accepted execute; controller-side classification for startup, crash, transport, and protocol failures; and session ownership through process exit plus stdout/stderr finality.

This change generalizes those seams after they exist. It must not reinterpret v1, alter ProcessingState behavior, add a queue, or implement the coordinate/cache jobs planned in blocks 48 and 51.

## Goals / Non-Goals

**Goals:**

- Supply a closed, typed job model that can add capability-specific variants without optional-field ambiguity or untyped JSON bags.
- Reuse launcher, session, cancellation, bridge, and classifier ownership/finality rules for all jobs.
- Preserve one identity from admission through request, events, result, session, cancellation, and classification.
- Keep v1 processing operable while introducing v2 and prove equivalent `ProcessAssets` behavior before cutover.
- Publish stable metadata that block 50 can use for admission decisions without embedding arbitration policy here.

**Non-Goals:**

- Implement `CoordinateLookup` (block 48), `CacheMutation` operations (block 51), WebUI routing, queuing, priority, or shared admission/arbitration (block 50).
- Change processing trigger, executor snapshots, eligibility/count semantics, Dashboard/Logs projection, advisory-lock behavior, cancellation grace, or terminal classification.
- Accept plugins, arbitrary job names, generic `object`, dictionaries, `JsonElement`, polymorphic fallback payloads, or reflection-based dispatch.

## Decisions

### 1. Introduce protocol v2 while preserving the exact sole worker argument and frozen v1

V1 remains exactly the processing-specific contract and codec defined by blocks 15–23. V2 uses the same protocol name, strict UTF-8 NDJSON framing, canonical scalar formats, duplicate rejection, size limits, sequence rules, and fail-closed parsing, but has a worker-job vocabulary and typed payload unions. A v1 decoder never receives v2 as if it were an additive v1 message.

Block 18's role grammar remains immutable: private worker mode is selected only by the exact sole argument `--internal-worker`. Protocol selection is separate child-process transport metadata. The one reserved environment entry is exactly `IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION`; absence means legacy v1 and the only accepted present value is the exact ASCII string `2` with no whitespace or alternate spelling. Any present empty, `1`, signed/numeric variant, whitespace-padded, or other value is invalid input and exits 2 before host construction, DI, ready, or work. The environment entry alone never selects worker role.

The controller's pure command builder receives an internal protocol-version descriptor. For a v1 child it removes `IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION` from the child environment even if the parent process inherited an ambient value. For a v2 child it replaces any ambient value with exact `2`. This is a narrow child-descriptor override: all other environment entries retain block 24's unchanged inheritance behavior, and the builder never mutates the parent environment. Thus user/container/service-manager ambient values cannot silently choose the protocol of controller-launched workers.

The worker validates this entry immediately after the sole-argument role decision and before host construction or ready emission. The entry is private implementation transport, not a supported deployment variable: it is absent from AppConfig, configuration binding, public environment-variable documentation, Docker Compose examples, UI, status surfaces, and general deployment validation. It is not logged or copied into events/errors. Directly launching the private role with the exact internal entry remains unsupported internal behavior, not a public configuration surface.

Alternative: add a second command-line argument. Rejected because it contradicts block 18's finalized exact-sole-argument contract. Alternative: trust the ambient inherited entry. Rejected because external deployment state could silently alter child protocol selection. Alternative: encode selection in the first stdin request. Rejected because ready is emitted before stdin and must already have the correct protocol version. Alternative: add optional v1 fields and types or remove v1 immediately. Rejected because v1 is closed and staged rollback requires preserving it.

Compatibility and trust-boundary matrix:

| Sole argument | Child environment entry | Envelope | Result |
|---|---|---|---|
| `--internal-worker` | absent | v1 | Existing v1 behavior and bytes unchanged |
| `--internal-worker` | exact `IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION=2` | v2 | Typed worker-job behavior |
| `--internal-worker` | present with any other value | none | Reject before host/ready/work, exit 2 |
| `--internal-worker` | absent or exact `2` | version differs from selected mode | Reject before job acceptance/work, exit 2 |
| Any non-exact argument vector | any | none | Existing block 18 invalid-invocation behavior |
| Normal Web invocation | any ambient value | n/a | Does not select worker role or change Web configuration |

There is no in-stream negotiation, ambient fallback, or silent downgrade. Protocol goldens are version-specific. V1 remains supported for processing for at least this migration; removing it requires a later explicit change.

### 2. Use one identity, with an explicit cross-version name mapping

The domain abstraction uses `JobId` as the universal lower-case GUID-D identity. For `ProcessAssets`, `JobId` is exactly the already-admitted `ProcessingRunRequest.RunId`; no new GUID is generated by the launcher, worker, registry, codec, bridge, or classifier. V1 serializes that value as `runId`; v2 serializes it as `jobId`. It is the same value, not an attempt ID plus a run ID.

`ChildWorkerSession.JobId` becomes the common property. A transitional `RunId` alias may remain only on the processing-facing adapter and must return the identical value. Cancellation targets `JobId`; the v1 adapter maps it to `runId`. Arbitration metadata and logs may carry the same identity but never mint another one.

Alternative: add `jobId` beside `runId`. Rejected because two correlation IDs create ambiguous cancellation, terminal, and cleanup ownership.

### 3. Model a closed discriminated union with capability-specific DTOs

V2 has a case-sensitive `jobKind` discriminator. The initial closed model knows three architectural variants:

- `ProcessAssets`: implemented here. Its typed request wraps the immutable processing request snapshot (identity remains envelope-level); its success result carries the existing trigger/timestamps and eligibility/progress counts needed to produce the same processing terminal projection. Its job-specific events are eligibility and progress.
- `CoordinateLookup`: reserved for block 48. Block 47 supplies only the named kind/registration seam; block 48 must add the concrete request, result, diagnostic event schema, codec goldens, validation, and handler before the kind is advertised as supported.
- `CacheMutation`: reserved for block 51. Block 51 must add a closed operation/source discriminator and concrete request/result/progress schema plus handler. No path/string-keyed options bag is reserved here.

A known-but-unregistered reserved kind is unsupported and is rejected before acceptance or heavy initialization. The ready payload advertises the protocol version and the actually registered/supported kinds, not every enum member. Duplicate handler kinds or a handler whose declared typed request/result do not match its descriptor fail composition/startup deterministically.

This balances future extensibility with fail-closed behavior. Alternative: use six top-level cache/job names from the old outline. Rejected for the foundation because download/refresh are operations within the cache-mutation capability and their exact semantics belong to block 51. Alternative: a generic payload/result bag. Rejected because it moves validation to runtime and permits cross-capability field leakage.

### 4. Separate common lifecycle frames from typed capability events and results

V2 retains the common envelope fields and independent stdin/stdout sequence domains. `ready` is process-scoped and has null `jobId`; every accepted-job frame has the exact job identity and kind. Common worker output consists of:

- `ready`: selected protocol version plus registered supported kinds; emitted and flushed before stdin reading.
- `job-started`: accepted kind, origin/trigger metadata, and start timestamp.
- `log-emitted`: bounded level/message, with existing secret-safe logging rules.
- `activity-started` / `activity-ended`: scoped activity ID and bounded label, preserving balanced/nestable lifecycle validation.
- `terminal`: one outcome (completed, cancelled, failed), start/end timestamps, exactly one kind-specific success result when completed, or one bounded structured error when failed. Cancelled carries neither success result nor failure error unless the existing classifier maps a cleanup failure to failed.

The structured error is a closed DTO with stable code, safe bounded message, and failure category. It excludes exception objects, stack traces, raw input, stderr, secrets, and arbitrary details. Job-specific event and result payloads are selected by `jobKind` and represented by concrete types. Envelope-kind/payload mismatches fail before delivery and do not produce partial values.

For `ProcessAssets`, the v2 adapter maps job-started, eligibility, progress, log, activity, and terminal to the exact existing v1 bridge/ProcessingState transitions. In particular it preserves UpdatedCount projection, handled per-asset failures, trigger, timestamps, counts, activity closure, and terminal uniqueness. Wire shape changes only when explicitly launched in v2 mode; user-visible processing behavior does not.

### 5. Generalize lifecycle ownership, not its policies

Introduce job-oriented launcher/session inputs while retaining the existing process mechanics: descriptor creation, immediate stdout/stderr/exit pumps, readiness timeout, execute flush gate, bounded stderr tail, process-tree cancellation escalation, wait-only caller cancellation, idempotent disposal, and completion only after exit/EOF/drain.

The generic session exposes `JobId`, `JobKind`, startup, completion, wait methods, stop/cancel, and async disposal. Protocol-version-specific codecs sit behind a session codec interface. The launcher does not interpret UI meaning. A capability event sink/bridge validates correlation, kind, ordering, and payload type before invoking a typed observer. The processing adapter remains the only bridge to `ProcessingState` in this block.

Cancellation keeps the block-28 contract: at most one cancel frame after execute flush, exact active identity, shared stop operation/deadline, cooperative token, then one process-tree kill if required. Cancellation is supported only when handler metadata says so; `ProcessAssets` remains cancellable. Reserved future kinds define their policy when implemented.

### 6. Dispatch through an explicit DI handler registry

Use a typed handler contract equivalent to `IWorkerJobHandler<TRequest,TResult>` plus a non-generic descriptor adapter generated/declared explicitly per supported kind. The registry is built from DI registrations, validates uniqueness and request/result compatibility at startup, and resolves by the closed `WorkerJobKind`. Only `ProcessAssets` is registered by block 47.

The codec validates envelope and concrete request before registry lookup. Registry lookup happens before heavy handler initialization/work. The handler receives the exact `JobId`, typed immutable request, typed event reporter, and cancellation token, and returns a typed result; terminal emission remains the host's single-owner responsibility. Handlers cannot emit terminal frames directly.

Alternative: a central switch that directly invokes services. Rejected because each future job would couple protocol dispatch to capability composition. Alternative: service-provider lookup/reflection from wire type names. Rejected because it weakens startup validation and makes untrusted input select runtime types.

### 7. Publish arbitration metadata but defer admission policy

Each registered descriptor exposes immutable, non-wire policy facts: kind, capability family, whether it is heavy/geodata-bearing, cancellability, and an admission resource class. `ProcessAssets` declares the same exclusive heavy-worker resource class later consumed by block 50. Origin/trigger remains request metadata so block 50 can preserve manual-versus-scheduled policy.

Block 47 neither owns the active slot nor decides busy/queue/priority. A controller-side busy rejection launches no process and therefore has no worker exit code. The PostgreSQL advisory-lock busy outcome remains exit 3 and is not repurposed for local arbitration.

### 8. Preserve exit mapping and distinguish wire terminal from controller final outcome

Both versions retain block 23's managed exits and precedence: 0 completed/no work, 2 invalid invocation/request/input, 3 global advisory-lock busy only, 4 handler/domain execution failure, 5 startup/config/dependency/host lifecycle failure, 6 output protocol/stdout transport failure, and 130 cooperative cancellation/shutdown. Abrupt termination remains raw platform evidence.

Exactly one terminal frame is required only after a job execute has been accepted and the host owns terminal emission. Invalid kind/version/payload before acceptance emits no job terminal and exits 2. Startup failure, pre-ready EOF, crash, framing/transport failure, sink failure, forced kill, or missing terminal is finalized once by the existing controller classifier; it must not fabricate a worker terminal frame. A committed valid terminal remains authoritative subject to the existing finalization gate, with later contradictions recorded only as bounded anomalies.

### 9. Migrate processing in observable, reversible stages

1. Land v2 DTOs, codecs, goldens, exact child-environment selection/sanitization, handler registry, and generalized lifecycle interfaces while leaving the exact sole worker argument and production processing launch on v1.
2. Adapt v1 processing execute/events/results through the common `ProcessAssets` handler/session model and prove existing v1 goldens remain byte-for-byte unchanged.
3. Add v2 `ProcessAssets` fixture coverage and run the same bridge/coordinator/classifier contract suite against v1 and v2.
4. Switch the production processing command selection to v2 only after parity, cancellation, crash, exit, and ProcessingState tests pass. Keep an explicit rollback to v1 during the compatibility window.
5. Blocks 48 and 51 add their concrete typed variants/handlers independently; block 50 consumes metadata and owns shared admission. No implementation from those blocks is pulled forward.

Rollback makes the controller remove the reserved child entry and selects the unchanged v1 processing codec; the worker argument remains `--internal-worker` in both modes. Database/data migrations are not required.

## Risks / Trade-offs

- [Two codecs increase temporary maintenance cost] → Share framing/scalar primitives, keep semantic validators version-specific, and lock both with byte goldens.
- [Internal protocol selection weakens the sole-argument or environment boundary] → Keep `--internal-worker` as the exact sole argument, accept only absent or exact child value `2`, sanitize inherited ambient values in every controller-built descriptor, validate before ready, and add cross-platform negative/security tests.
- [A second identity appears through adapters] → Assert equality from admitted processing RunId through v1/v2 wire, session, cancel, events, terminal, classifier, and handle release.
- [Reserved kinds appear usable before their handlers exist] → Advertise only registered kinds and reject unregistered kinds before acceptance/heavy initialization.
- [Generic lifecycle erases typed payload safety] → Use explicit per-kind DTO adapters and reject kind/payload mismatches; ban untyped payload carriers.
- [Controller outcome is confused with worker terminal] → Test no synthetic terminal on pre-acceptance, crash, protocol, transport, kill, or shutdown paths.
- [Processing behavior drifts during cutover] → Run identical behavioral suites through v1 and v2 and retain v1 rollback until parity is demonstrated.
