## Purpose

Provides a stable, safe, log-only contract for correlating deployment roles and temporary worker processes through startup, cancellation, protocol/finality handling, and bounded resource observations without exposing workload data or creating high-cardinality metrics.

## ADDED Requirements

### Requirement: Stable role and process event catalog
The system SHALL emit the following exact structured `ILogger` events at their owning lifecycle boundaries. Field names and closed values are part of the compatibility contract; implementations MUST NOT reuse these EventIds for another meaning. Each event MUST contain exactly the fields declared for it (including the common worker-job fields where applicable) and no additional application field; logger-framework metadata such as `{OriginalFormat}`, timestamp, category, and EventId/name is outside this field set.

| EventId | Event name | Level | Required structured fields |
|---:|---|---|---|
| 6601 | `DeploymentModeSelected` | Information | `deployment_mode`, `application_role`, `process_id` |
| 6602 | `RoleProcessStarting` | Information | `application_role`, nullable `deployment_mode`, `process_id` |
| 6603 | `RoleProcessReady` | Information | `application_role`, nullable `deployment_mode`, `process_id`, `readiness_kind`, `startup_duration_ms` |
| 6604 | `RoleProcessStopping` | Information | `application_role`, nullable `deployment_mode`, `process_id`, `stop_reason` |
| 6605 | `RoleProcessStopped` | Information for completed/cancelled; Warning for failed | `application_role`, nullable `deployment_mode`, `process_id`, `process_outcome`, `process_duration_ms`, `stop_duration_ms` |

`deployment_mode` MUST be exactly `standard|web-only|run-once`; event 6601 is emitted only for a successfully resolved public mode after a safe application logger exists. `standard|web-only` require `application_role=web`, while `run-once` requires `application_role=run-once`; public Web/RunOnce role events require their non-null resolved mode. `application_role=internal-worker` requires null `deployment_mode`, bypasses public mode resolution, and emits no 6601. `readiness_kind` MUST map exactly as `web` to `web-listening`, `internal-worker` to `worker-protocol-ready`, and `run-once` to `run-once-initialized`. `stop_reason` MUST be exactly `completed|host-shutdown|startup-failure|fatal-failure`, and `process_outcome` MUST be exactly `completed|cancelled|failed`; `completed` requires `completed`, `host-shutdown` requires `cancelled`, and `startup-failure|fatal-failure` require `failed`.

#### Scenario: Web mode becomes ready
- **WHEN** a valid Standard or Web-only process completes its owned host-readiness boundary
- **THEN** events 6601, 6602, and 6603 identify the same process and selected mode with exact stable names, fields, levels, and a monotonic startup duration

#### Scenario: Private worker starts
- **WHEN** the exact private worker role is selected and reaches flushed protocol readiness
- **THEN** role-process events identify `application_role=internal-worker` and `readiness_kind=worker-protocol-ready` with null `deployment_mode`, without event 6601 or the private selector text

#### Scenario: Selection fails before logging
- **WHEN** private-role syntax or public mode validation fails before host/application logging exists
- **THEN** the existing bounded stderr diagnostic and exit behavior remain authoritative and no lifecycle event is fabricated

### Requirement: Canonical worker-job correlation and launch catalog
Every worker-job event SHALL carry exactly the common fields `job_id`, `job_kind`, `job_origin`, `controller_process_id`, and nullable `worker_process_id`, plus exactly the additional fields listed for that event and no other application field. `job_id` MUST be the existing protocol JobId; for `ProcessAssets` it is the exact existing RunId, and the telemetry MUST NOT mint or emit a second run, attempt, launch, cancellation, session, or telemetry identifier. `job_kind` MUST be exactly `ProcessAssets|CoordinateLookup|CacheMutation`. `job_origin` MUST be exactly `dashboard-manual|scheduler|lookup-ui|cache-ui`. Controller and worker PIDs MUST use their distinct named fields and MUST NOT be collapsed into an ambiguous `process_id`. `worker_process_id` is null before OS-process ownership (6610, a start failure, or a cancellation latched before ownership) and MUST be non-null with the exact 6611 value on every later event from that owned process, including 6612, 6621–6623, 6630, 6640, 6650, and 6641; only 6620 may be either, and 6641 may be null only for a no-process start failure.

| EventId | Event name | Level | Additional required fields |
|---:|---|---|---|
| 6610 | `WorkerJobLaunchStarted` | Information | none; `worker_process_id` is null |
| 6611 | `WorkerJobProcessStarted` | Information | non-null `worker_process_id`, `process_start_duration_ms` |
| 6612 | `WorkerJobReady` | Information | non-null `worker_process_id`, `readiness_duration_ms`, `startup_duration_ms` |

#### Scenario: Child reaches readiness
- **WHEN** the controller starts a child and accepts its valid flushed ready frame
- **THEN** events 6610, 6611, and 6612 retain the same canonical identity, kind, origin, controller PID, and child PID while reporting process-start and ready timing

#### Scenario: Processing identity is not duplicated
- **WHEN** a ProcessAssets child is launched
- **THEN** `job_id` equals the established RunId and no separate `run_id` or attempt-like field is present

#### Scenario: Child crashes before ready
- **WHEN** a started child exits before valid readiness
- **THEN** 6610 and 6611 may exist, 6612 and terminal event 6640 do not, and final classification 6641 reports `ready_observed=false` without inventing readiness or a terminal

### Requirement: Stable cancellation lifecycle catalog
The exact-session cancellation owner SHALL emit the following worker-job events. The established 10000 ms internal grace remains policy; telemetry observes it and MUST NOT add or reset a deadline.

| EventId | Event name | Level | Additional required fields |
|---:|---|---|---|
| 6620 | `WorkerJobCancellationRequested` | Information | `cancellation_reason`, `cancellation_phase`, `grace_period_ms` |
| 6621 | `WorkerJobCancellationGraceCompleted` | Information | `cancellation_duration_ms`, `terminal_observed`, `exit_observed` |
| 6622 | `WorkerJobCancellationEscalated` | Warning | `grace_period_ms`, `grace_elapsed_ms`, `escalation_action` |
| 6623 | `WorkerJobForcedStopCompleted` | Warning | `escalation_duration_ms`, `kill_result` |

`cancellation_reason` MUST be exactly `user|web-host-shutdown|controller-fault`; `cancellation_phase` MUST be exactly `starting|ready|running|finalizing`; `escalation_action` MUST be `kill-process-tree`; and `kill_result` MUST be exactly `succeeded|failed|not-supported`. Concurrent/repeated requests that join the existing exact-session operation MUST NOT emit another 6620. Event 6621 is emitted only when the process exits before escalation and only after stdout/stderr finality fixes `terminal_observed`; its `cancellation_duration_ms` measures from the first accepted cancellation request to observed process exit, not the later drain or event-emission time, and `exit_observed` is true. Event 6622 is emitted once when grace expires while the process is alive; 6623 is emitted once after the one escalation attempt and owned exit/drain observation settle.

#### Scenario: Cooperative cancellation completes in grace
- **WHEN** the first exact-session request is accepted and the child exits before the grace deadline
- **THEN** one 6620 and one 6621 are emitted, and no 6622 or 6623 is emitted

#### Scenario: Unresponsive child is escalated
- **WHEN** the owned child remains alive at the established grace deadline
- **THEN** exactly one 6622 records the 10000 ms grace and exactly one 6623 records the bounded result of the existing process-tree termination attempt

### Requirement: Protocol, terminal, and exit classification catalog
The existing protocol validator, terminal receipt, and pure evidence classifier SHALL remain authoritative and SHALL emit the following observations without changing protocol bytes, terminal authority, exit precedence, or cleanup.

| EventId | Event name | Level | Additional required fields |
|---:|---|---|---|
| 6630 | `WorkerProtocolViolation` | Warning | `protocol_direction`, `protocol_phase`, `violation_code`, nullable `sequence` |
| 6640 | `WorkerJobTerminalObserved` | Information for completed/cancelled; Warning for failed | `terminal_outcome`, `terminal_sequence` |
| 6641 | `WorkerJobProcessClassified` | Information for completed/cancelled; Warning otherwise | `ready_observed`, nullable `terminal_outcome`, `process_classification`, `exit_observation`, nullable `exit_code`, `forced_stop`, `total_duration_ms`, memory fields defined below |

`protocol_direction` MUST be exactly `worker-output|controller-input`; `protocol_phase` MUST be exactly `ready|execute|events|terminal|drain`; and `violation_code` MUST be a bounded safe code from the landed validator/classifier, never parser input or exception text. `terminal_outcome` MUST be exactly `completed|cancelled|failed`. `process_classification` MUST be exactly `completed|cancelled|busy|worker-failed|startup-failed|protocol-failed|transport-failed|missing-terminal|terminal-exit-mismatch|forced-stop|crashed|infrastructure-failed`. `exit_observation` MUST be exactly `managed|unmapped|unavailable`. `managed` requires a non-null mapped `exit_code` in `0|2|3|4|5|6|130`; `unmapped` requires a non-null observed code outside that set; and `unavailable` requires null `exit_code`. `terminal_outcome` is non-null if and only if event 6640 was accepted. With a non-null terminal, the only agreeing managed combinations are `completed/0/completed`, `cancelled/130/cancelled`, `failed/3/busy`, `failed/4/worker-failed`, and `failed/5/infrastructure-failed` in terminal/exit/classification order; every other observed exit with a terminal requires `terminal-exit-mismatch`. With a null terminal, managed code 2 requires `protocol-failed`, code 3 is invalid, code 5 requires `startup-failed|infrastructure-failed`, code 6 requires `transport-failed`, and code 130 requires exact-session `cancelled`; code 0 or 4 after readiness requires `missing-terminal`. An unmapped exit with null terminal requires `crashed` unless an attempted forced stop makes `forced-stop` more specific. `exit_observation=unavailable` cannot classify `completed|busy|worker-failed|terminal-exit-mismatch`. `missing-terminal` requires `ready_observed=true`; `startup-failed` requires `ready_observed=false`; `completed|busy|worker-failed` require true; `cancelled` may be false only for exact-session cancellation before readiness. `forced_stop=true` if and only if escalation attempted whole-tree termination; it can accompany a more specific mismatch/infrastructure classification. `transport-failed`, `forced-stop`, and `infrastructure-failed` may have a terminal only when the existing committed-terminal owner accepted it before the independent failure, in which case any disagreeing exit still selects `terminal-exit-mismatch`; the terminal remains authoritative in `terminal_outcome`. One 6641 SHALL be emitted only after the existing process, stdout, stderr, protocol, bridge, and classifier finality needed for these invariants.

#### Scenario: Successful terminal and exit agree
- **WHEN** a valid completed terminal is durably accepted and final exit/drain evidence classifies normally
- **THEN** 6640 and 6641 are Information events with the same canonical job fields and `process_classification=completed`

#### Scenario: Protocol input is hostile
- **WHEN** framing, decoding, sequence, correlation, lifecycle, or terminal validation fails
- **THEN** one bounded 6630 identifies only safe direction/phase/code/sequence facts and 6641 records the final conservative classification without raw frame or error content

#### Scenario: Terminal and exit contradict
- **WHEN** a committed terminal conflicts with later exit or forced-stop evidence
- **THEN** the terminal remains authoritative to its existing owner while 6641 is Warning with `process_classification=terminal-exit-mismatch`

### Requirement: Monotonic duration semantics
Every `*_duration_ms` and `*_elapsed_ms` field in this capability SHALL be a non-negative integral millisecond value derived from paired timestamps supplied by the same injected `TimeProvider` monotonic timestamp source. Conversion MUST truncate sub-millisecond fractions, and a negative result from a faulty test provider or overflow condition MUST be clamped to the representable range rather than emitted as a negative value. Logger wall-clock timestamps MUST NOT be used to calculate durations.

#### Scenario: Wall clock changes during a job
- **WHEN** wall-clock time moves while monotonic time advances normally
- **THEN** lifecycle durations reflect only the monotonic elapsed interval

### Requirement: Parent-owned best-effort working-set observation
The parent launcher/session SHALL be the sole owner of child memory sampling. It SHALL attempt child `WorkingSet64` immediately after process start, every 1000 ms using a `TimeProvider` timer while the child is owned, and once opportunistically at finality. Event 6641 SHALL include `memory_scope=worker-process-only`, `memory_sampling_method=parent-periodic-working-set-max-v1`, `memory_sample_interval_ms=1000`, `memory_observation`, nullable `peak_working_set_bytes`, `memory_sample_count`, and nullable `memory_unavailable_reason`.

If at least one sample succeeds, `memory_observation` MUST be `available`, `peak_working_set_bytes` MUST be the maximum successful non-negative sample, `memory_sample_count` MUST count successful samples, and `memory_unavailable_reason` MUST be null. If none succeeds, `memory_observation` MUST be `unavailable`, bytes MUST be null, count MUST be zero, and reason MUST be exactly `not-supported|access-denied|process-exited|sample-failed|no-sample`. Mixed failed attempts select one reason deterministically by highest precedence `not-supported` > `access-denied` > `sample-failed` > `process-exited` > `no-sample`, independent of attempt order; `no-sample` applies only when no observation attempt could be made. Sampling failure MUST NOT affect job behavior or classification. The value is an observed best-effort child-process maximum, not an OS absolute peak, process-tree sum, container/cgroup value, or system-memory measure.

#### Scenario: Some memory samples fail
- **WHEN** at least one parent-owned child working-set sample succeeds and other attempts fail
- **THEN** final classification reports `available` with the maximum successful value and successful sample count

#### Scenario: Memory is unavailable
- **WHEN** no child working-set sample succeeds, including a crash before the first sample can be read
- **THEN** final classification explicitly reports `unavailable`, null bytes, zero samples, and one bounded unavailable reason without fabricating zero memory

### Requirement: Existing detector event is reused exactly
Work-detector telemetry SHALL remain exactly `EventId(5901, "ProcessingWorkDetectorCompleted")` with block 59's existing fields, one-event-per-call behavior, Information level for below-1000-ms `HasWork|NoWork|Cancelled`, and Warning for `Failed` or elapsed time at least 1000 ms. Block 66 MUST NOT add a detector wrapper event, duplicate 5901, or change its sampling, redaction, result, exception, or bypass semantics.

#### Scenario: Scheduled detector completes
- **WHEN** the finalized detector emits event 5901
- **THEN** no 66xx detector event is emitted for the same invocation

### Requirement: Bounded coalescing saturation observation
After block 65's validated per-session coalescer reaches finality, the telemetry owner SHALL emit at most one `EventId(6650, "WorkerEventCoalescingSaturated")` Warning event for that job, and only when block 65's exact snapshot has `enqueue_wait_count>0` or `replaced_count>0`. In addition to common worker-job fields, it SHALL contain exactly `finality_kind`, `accepted_replaceable_count`, `accepted_lossless_count`, `replaced_count`, `delivered_snapshot_count`, `fifo_high_water`, `enqueue_wait_count`, `enqueue_wait_duration_ms`, `projection_duration_ms`, `cadence_notification_count`, nullable `terminal_flush_duration_ms`, `stale_rejection_count`, and `abnormal_abandonment_count`. `finality_kind` MUST be `terminal|nonterminal`; terminal finality requires a non-null measured `terminal_flush_duration_ms`, while nonterminal finality requires null and MUST NOT fabricate a terminal flush. All numeric facts are the bounded non-negative aggregates supplied by block 65's exact observation snapshot; block 66 derives only the saturation predicate and `finality_kind`, and no per-progress-item telemetry is allowed.

#### Scenario: Burst saturates projection
- **WHEN** validated replaceable snapshots are coalesced or a producer waits on the full lossless FIFO
- **THEN** one final Warning copies block 65's exact bounded aggregate snapshot for the canonical job and reports terminal versus nonterminal finality without fabricating terminal-flush timing

#### Scenario: No saturation occurs
- **WHEN** a job reaches coalescer finality without a full-FIFO wait or snapshot replacement
- **THEN** event 6650 is not emitted

### Requirement: Telemetry remains log-only and redacted
All events in this capability SHALL use structured application logs only. The system MUST NOT create a `Meter`, metric instrument/exporter, metric label, tracing baggage/tag, protocol field, ProcessingState/UI-ring entry, persisted setting, or per-asset telemetry event for this catalog. Job IDs and PIDs MUST remain log correlation fields and MUST NOT become metric dimensions.

Event templates, structured state, scopes, rendered messages, and attached exceptions MUST NOT contain coordinates; asset, country, cache, request, result, or arbitrary payload values; raw stdin/stdout/protocol frames; raw stderr or retained stderr tails; command-line arguments or the private selector; environment/configuration values; filesystem paths; SQL; credentials; connection strings; tokens; secrets; arbitrary exception messages; or stacks. Only catalog fields, bounded closed codes, and bounded numeric observations are permitted.

#### Scenario: Failure carries secret-bearing data
- **WHEN** launcher, sampler, protocol, stderr, or classifier failures contain coordinates, credentials, CLI/environment values, payloads, or canary secrets
- **THEN** neither structured state, scopes, rendered output, nor attached exceptions expose those values

#### Scenario: Metrics are inspected
- **WHEN** lifecycle events include canonical job IDs and process IDs
- **THEN** those values exist only in logs and no metric or trace dimension is created
