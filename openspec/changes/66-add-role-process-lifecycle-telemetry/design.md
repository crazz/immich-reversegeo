## Context

See proposal.md and `specs/worker-lifecycle-telemetry/spec.md`. This is a cross-cutting observability change over owners established by blocks 18–32, 40–51, 59, and 65. The role parser has a deliberately pre-logging failure path; the launcher owns the child process and streams; the cancellation owner already owns one 10-second grace/escalation operation; the evidence classifier alone reconciles terminal/exit facts; block 59 already emits detector event 5901; and block 65 alone owns coalescing counters and finality. Instrumentation must attach to those boundaries rather than introduce another lifecycle state machine.

## Goals / Non-Goals

**Goals:**
- Implement the exact EventId/name/field/level contract from the delta spec at existing owner transitions.
- Preserve one canonical JobId, exact landed job kind/origin, and unambiguous controller/worker PID fields across events.
- Make all durations deterministic under injected `TimeProvider` and make child working-set availability truthful.
- Keep diagnostic volume bounded and prove redaction across every logger representation.

**Non-Goals:**
- No protocol, terminal, exit-code, classifier, cancellation, coalescing, detector, scheduling, UI, retry, or deployment-mode behavior change.
- No external telemetry service, OpenTelemetry setup, `Meter`, exporter, metric dimension, trace baggage, public configuration, retention policy, or per-asset event.
- No block 67 failure matrix, block 68 soak thresholds, absolute/cgroup/process-tree memory claim, or public private-worker interface.

## Decisions

### 1. Central catalog, distributed emission at existing owners

Define one internal catalog of stable `EventId` constants and source-generated or equivalently static structured templates. The catalog contains exactly:

- 6601 `DeploymentModeSelected`
- 6602 `RoleProcessStarting`
- 6603 `RoleProcessReady`
- 6604 `RoleProcessStopping`
- 6605 `RoleProcessStopped`
- 6610 `WorkerJobLaunchStarted`
- 6611 `WorkerJobProcessStarted`
- 6612 `WorkerJobReady`
- 6620 `WorkerJobCancellationRequested`
- 6621 `WorkerJobCancellationGraceCompleted`
- 6622 `WorkerJobCancellationEscalated`
- 6623 `WorkerJobForcedStopCompleted`
- 6630 `WorkerProtocolViolation`
- 6640 `WorkerJobTerminalObserved`
- 6641 `WorkerJobProcessClassified`
- 6650 `WorkerEventCoalescingSaturated`

Emission remains distributed: resolved-mode composition emits 6601; each role-lifetime owner emits 6602–6605; launcher/session emits 6610–6612; the exact-session stop operation emits 6620–6623; validator/classifier receipt emits 6630, 6640, and 6641; and coalescer finality emits 6650. This keeps ordering beside authoritative state transitions and avoids a shadow event bus or telemetry state machine.

Alternative considered: one central observer subscribed to all services. Rejected because it would duplicate lifecycle state/correlation, race finality, and make exact-once boundaries harder to prove.

### 2. Role selection and private selector boundary

The invalid role/mode paths run before application logging and retain their existing constant-form stderr diagnostics. After successful public resolution and safe logger construction, emit 6601 once. Enforce exact role mappings: Standard/Web-only use Web, Run-once uses RunOnce, and only InternalWorker has null mode and no 6601; readiness kind follows that role. Stopping/stopped projection maps completed to completed, host shutdown to cancelled, and startup/fatal failure to failed. InternalWorker precedence never reads public mode and therefore emits no 6601; its role lifecycle uses nullable `deployment_mode=null`. Neither templates nor fields contain raw arguments, the private selector token, environment variable values, or command descriptors. `application_role=internal-worker` is a bounded role value, not disclosure of invocation syntax.

Alternative considered: bootstrap-console logging for every parse result. Rejected because it would duplicate established stderr behavior and risk echoing untrusted startup input before redaction infrastructure exists.

### 3. One immutable job log context, not a new identity

At admission/launch, construct a small immutable safe context from landed descriptor facts: canonical JobId, exact kind, normalized bounded origin, controller PID, and nullable worker PID. For ProcessAssets, canonical JobId is the existing RunId. The context never creates `run_id`, `attempt_id`, `session_id`, or a telemetry correlation ID. When OS start succeeds, replace only nullable worker PID in the owned session context; every later event from that owned process reuses the exact non-null value through finality, while only pre-ownership events/start failure and an early latched cancellation may keep it null. Do not put this context in ambient global scopes that could bleed across concurrent logs; pass it explicitly or use a tightly bounded owned scope whose sink tests inspect.

The stable telemetry origin normalization is:

| Landed operation | `job_origin` |
|---|---|
| Dashboard manual processing | `dashboard-manual` |
| Standard scheduled processing | `scheduler` |
| Lookup page/controller (`CoordinateLookup`) | `lookup-ui` |
| Cache mutation page/controller | `cache-ui` |

Run-once is not a child launcher job and is represented by role-process events with `deployment_mode=run-once`; it does not invent a WorkerJob origin. If landed descriptors introduce a different source before apply, reconcile the mapping explicitly and update the planning contract rather than silently passing arbitrary text.

Alternative considered: logging both JobId and RunId for searches. Rejected because they are the same ProcessAssets identity and duplicate fields invite divergent correlation.

### 4. Monotonic timing helper with explicit ownership

Use a narrow helper around the injected `TimeProvider.GetTimestamp()`/`GetElapsedTime(start,end)`. Each lifecycle owner captures its own start timestamp at the state transition it owns:

- role process: composition/lifetime entry, ready, stopping, stopped;
- launcher: launch-call entry, OS process ownership, accepted ready;
- cancellation: first exact-session request, grace expiration or cooperative exit, escalation attempt settlement;
- classifier: launch entry through complete classifier finality;
- coalescer: terminal flush start/end from block 65's landed facts.

Convert elapsed time to integral milliseconds by truncating sub-millisecond fractions. Clamp negative/faulty-provider results to zero and overflow to `long.MaxValue`; do not throw from telemetry. Never use `DateTimeOffset.UtcNow`, logger timestamps, or independently sampled clocks for durations. Existing domain timestamps remain untouched.

Alternative considered: `Stopwatch` directly. Rejected because owners already depend on `TimeProvider`, and deterministic virtual-time tests must drive timing, grace, and sampling with one clock abstraction.

### 5. Parent session owns best-effort worker memory sampling

Add one sampler beneath the launcher/session process abstraction, not inside the worker. It starts only after process ownership and a non-null worker PID, attempts current `WorkingSet64` immediately, then uses a `TimeProvider` timer every 1000 ms, and performs one final opportunistic sample before releasing the process handle. Calls are serialized so timer and final sampling cannot corrupt the maximum/count. Disposal cancels and joins the timer callback before session release.

A successful non-negative sample updates a checked/saturating maximum and successful-sample count. Individual failures are retained only as closed reasons and never logged separately. If any sample succeeds, final 6641 says `available`; otherwise it says `unavailable` using order-independent precedence `not-supported` > `access-denied` > `sample-failed` > `process-exited` > `no-sample`; `no-sample` means no observation attempt was possible. `succeeded` memory state never implies every sample succeeded. The observation covers only that child process at sampling instants; descendants, controller, container/cgroup, allocator/native categories, and the platform's absolute peak remain unknown.

Parent ownership is chosen because it survives long enough to report crash/finality and already owns the process abstraction. Child self-reporting was rejected because it can disappear on pre-ready crash, conflicts with stdout purity, and would create two samplers. OS-specific peak APIs were rejected as the sole contract because support and semantics differ; the explicit method name records a periodic observed maximum instead.

### 6. Cancellation events decorate the single existing stop operation

Place 6620 at creation of the shared exact-session stop task, not each caller. Joiners emit nothing. Record the established 10000 ms value but do not create a new timer. Cooperative process exit before escalation captures the request-to-exit duration but delays 6621 until stdout/stderr finality fixes whether a terminal was observed; drain latency is not added to `cancellation_duration_ms`. The single grace-expiry branch emits 6622 before invoking the existing one whole-tree kill. Event 6623 waits for the attempt plus owned exit/drain observation; `kill_result=succeeded` means the platform termination request was accepted and owned exit/drain finality confirmed it, while `failed` and `not-supported` remain bounded facts and retain existing ownership. Final outcome still comes only from 6641.

Alternative considered: one generic cancellation-completed event. Rejected because it cannot distinguish cooperative grace from escalation/kill failure, which blocks 67–69 need to diagnose.

### 7. Protocol and classifier logs never use raw diagnostic text

Map the landed validator/classifier's typed failures into a finite bounded `violation_code`; never interpolate raw input, exception messages, raw/stored stderr, command descriptors, or protocol payloads. Emit 6630 at the first retained protocol violation for the job. Emit 6640 only after an accepted terminal receipt; it does not fabricate terminal observations for crash/missing-terminal paths. Emit exactly one 6641 after classifier-required process/stream/bridge finality. Its projection enforces the spec's exact agreeing managed terminal/exit/classification tuples, null-terminal exit mapping, mapped/unmapped/unavailable presence rules, readiness implications, forced-stop facts, and PID continuity; every other terminal-plus-exit tuple becomes `terminal-exit-mismatch`, and invalid combinations fail tests rather than being logged. A pre-ready crash therefore has no 6612/6640 but does have 6641 with `ready_observed=false`, null terminal, and the actual available/unavailable memory fields.

The final event's level is Information only for `completed` and `cancelled`; every busy, failed, missing, mismatch, forced, crash, protocol, transport, startup, or infrastructure classification is Warning. This deliberately treats advisory-lock Busy as operationally noteworthy while preserving its existing terminal/exit semantics.

Alternative considered: attach exceptions to logs for debugging. Rejected because most relevant exceptions can include paths, environment/configuration, protocol content, or process diagnostics; bounded codes and separate existing safe diagnostics are the contract.

### 8. Detector 5901 is consumed, never wrapped

Do not add detector instrumentation. Tests assert block 59 still emits exactly one `EventId(5901, "ProcessingWorkDetectorCompleted")` per scheduled detector call and that no 66xx detector event exists. Block 66 may document/cross-reference the event but cannot change fields, 1000 ms level elevation, sampling, redaction, or exception/result propagation.

Alternative considered: emit a generic lifecycle event around the detector to align IDs. Rejected as duplicate telemetry with ambiguous duration ownership.

### 9. Coalescer emits one final saturation summary only

Consume block 65's exact bounded internal observation snapshot after coalescer finality. Emit 6650 only when `enqueue_wait_count>0` or `replaced_count>0`, and at most once for the job. Copy its accepted replaceable/lossless counts, replaced and delivered-snapshot counts, FIFO high-water, enqueue-wait count/duration, projection duration, cadence notification count, stale rejection count, and abnormal abandonment count without inventing capacity or suppressed-sequence fields. Add only `finality_kind=terminal|nonterminal`; copy terminal-flush duration only for terminal finality and use null for nonterminal finality. It does not instrument every replacement, producer wait, progress event, UI callback, or notification tick. No event is emitted for an unsaturated job.

Alternative considered: log on each transition into full state. Rejected because burst oscillation can recreate the overload this change is intended to diagnose.

### 10. Log-only cardinality and redaction boundary

The catalog remains `ILogger` only. Do not create a metrics/tracing bridge or add job IDs/PIDs as dimensions to any existing instrument. Do not mirror these events into the ProcessingState user log ring or worker protocol. Static templates enumerate exactly the spec's common-plus-event application fields and no others; logger framework metadata such as `{OriginalFormat}` remains provider-owned, and calls pass no exception object. A hostile-value sink test inspects EventId, template/original format, key/value state, scopes, rendered message, and exception slot.

Redaction is allow-list based, not heuristic secret scanning. Allowed values are closed enum-like tokens, GUID identity, numeric PID/sequence/count/duration/exit/working-set observations, booleans, and nulls. Coordinates, request/result bodies, country/cache/asset values, CLI/private selector, environment/configuration, paths, SQL, credentials, tokens, raw protocol/streams, raw stderr/tails, exception text, and stacks never enter the call.

## Risks / Trade-offs

- [Stable EventIds constrain later renames] → Keep names/fields centralized and test exact compatibility; add new IDs for materially new meanings.
- [Origin normalization can diverge from landed descriptors] → Reconcile at apply start and stop if the closed mapping cannot be proven without arbitrary text.
- [Working-set sampling can fail or miss a short-lived peak] → Report method/scope/sample count and explicit unavailable semantics; make no absolute-peak claim.
- [A 1000 ms timer adds small per-worker overhead] → One parent-owned timer only, no child sampler, no per-sample log, and deterministic disposal/join.
- [Warning-only abnormal outcomes may increase logs] → Exactly one final classification plus first protocol/single escalation facts; no retries or repeated polling logs.
- [Role stop logging can itself fail during teardown] → Logging remains best-effort and cannot alter stop/disposal/outcome precedence.

## Migration Plan

1. At apply start, verify the exact landed role/mode enums, descriptor origin metadata, job kinds, launcher/session process abstraction, cancellation shared task, classifier outcomes, detector event 5901, and block-65 observation snapshot. Stop for reconciliation rather than create parallel seams.
2. Add catalog/context/timing primitives and structured-sink contract tests before attaching emitters.
3. Instrument role, launcher, cancellation, protocol/terminal/classifier, and coalescer boundaries in owner order; add parent memory sampling last so it composes with session disposal.
4. Run focused unit/event-sink tests and the bounded existing process-fixture extensions, then normal tests and strict OpenSpec validation.
5. Roll back by removing only catalog emission and sampler registration. No protocol, settings, database, cache, or persisted telemetry migration exists.
