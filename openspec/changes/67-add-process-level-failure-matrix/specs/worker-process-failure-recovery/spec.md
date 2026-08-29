## Purpose

Defines deterministic process-boundary evidence that every supported worker job fails visibly, preserves authoritative finality, cleans up owned resources, and retries only through a new explicit admission.

## ADDED Requirements

### Requirement: Failure matrix declares applicability and deterministic evidence
The verification suite SHALL use the established real child-process fixture and SHALL declare each row's protocol version, job kind, trigger, raw-exit expectation as Absent, ExactManaged, or PresentPlatformRaw, expected terminal authority, expected block-66 telemetry, and cleanup assertions. ExactManaged SHALL assert the applicable portable value 0, 2, 3, 4, 5, 6, or 130; PresentPlatformRaw SHALL retain the observed abrupt/forced platform value without normalizing or inferring meaning from its number, which may coincide numerically with a managed value. It SHALL cover v1 `ProcessAssets` and v2 `CoordinateLookup` and `CacheMutation` only where the underlying contract applies, SHALL use asynchronous gates, explicit EOF/exit controls, injected `TimeProvider`, bounded watchdogs, and isolated run roots, and SHALL NOT use fixed sleeps, live downloads, or inferred process state.

#### Scenario: Matrix row is executed
- **WHEN** a failure row is selected
- **THEN** the test records its protocol/job applicability and proves the declared exit, terminal, telemetry, child, stream, coordinator, lock, and temporary-artifact observations before the row completes

#### Scenario: Contract does not apply to a job kind
- **WHEN** a row concerns the ProcessAssets-only PostgreSQL advisory lock or another job-specific effect
- **THEN** the matrix marks other job kinds not applicable and does not manufacture equivalent behavior for them

### Requirement: Pre-launch validation and arbitration start no child
Invalid deployment-mode or private protocol-version selection, invalid protocol envelope or payload, and unknown closed job/source/operation selectors SHALL fail at their established invalid-input boundary. Syntactically valid worker startup with unusable runtime configuration or a required dependency SHALL remain infrastructure failure rather than invalid input. A local Busy or Unavailable arbitration result SHALL produce no child, process exit, protocol terminal, cancellation capability, queue entry, retry, lock attempt, cache mutation, or coordinator leak.

#### Scenario: Deployment mode is invalid before host construction
- **WHEN** mode input is empty, whitespace, case-varied, or unknown
- **THEN** startup writes only the bounded pre-host diagnostic, exits 2, emits no worker lifecycle telemetry, starts no child, and leaves isolated config/data roots unchanged

#### Scenario: Private protocol-version selector is invalid
- **WHEN** the private worker protocol selector is empty, whitespace, unsupported, or otherwise noncanonical
- **THEN** startup fails before host construction or readiness with exit 2, no job terminal or lifecycle event is fabricated, and no configuration or dependency initialization occurs

#### Scenario: Worker request or selector is invalid
- **WHEN** a started worker receives an invalid envelope, payload, job-kind selector, CacheMutation source/operation selector, coordinate payload, or ProcessAssets request
- **THEN** it emits no domain work, maps the invalid request to exit 2 under the applicable v1/v2 contract, emits no synthetic terminal before acceptance, and closes all streams and process ownership

#### Scenario: Runtime configuration or required dependency is unusable
- **WHEN** syntactically valid worker startup cannot load required configuration or initialize a mandatory dependency
- **THEN** it exits 5 as infrastructure failure rather than exit 2, emits no terminal when failure precedes request acceptance, reports the safe startup/infrastructure classification, and releases every initialized resource

#### Scenario: Local admission is Busy or Unavailable
- **WHEN** a valid heavy-job request loses the process-local slot or reaches a shutdown-fenced coordinator
- **THEN** the result is respectively Busy or Unavailable and process-start, ready, terminal, exit, cancellation, advisory-lock, and mutation observations remain absent

### Requirement: Launch, readiness, crash, and nonzero-exit failures retain exact finality
The suite SHALL cover command/spawn failure, bounded readiness timeout, crash before ready, crash after ready, each mapped nonzero exit relevant to the job kind, unmapped abrupt death, missing terminal, and terminal/exit contradiction. Classification SHALL wait for raw process exit and both redirected-stream pumps. One committed valid terminal SHALL remain authoritative and later process anomalies SHALL be supplementary only; without a committed terminal, the control plane SHALL finalize exactly once according to the established classifier.

#### Scenario: Spawn fails
- **WHEN** the resolved child command cannot be started
- **THEN** no PID or ready/terminal event is fabricated, one Failed control-plane outcome is finalized, launch/classification telemetry uses the established safe category, coordinator ownership is released exactly once, and no child exists

#### Scenario: Readiness deadline expires
- **WHEN** a live child does not emit an accepted ready frame before the finite injected deadline
- **THEN** fault containment terminates the exact process tree, one Failed outcome is finalized after exit and both drains, ready telemetry is absent, classification records `ready_observed=false`, and no child survives

#### Scenario: Child crashes before ready
- **WHEN** a child starts and exits abruptly before an accepted ready frame
- **THEN** launch/process-start and one abnormal classification are observed, no ready or terminal is fabricated, the outcome is Failed, and PID, pumps, process tree, and coordinator ownership are finalized

#### Scenario: Child crashes after ready without terminal
- **WHEN** a ready child exits before a valid terminal
- **THEN** the unmapped or mapped raw exit is preserved, one missing-terminal/crash Failed outcome is committed, both streams reach finality, and no automatic retry starts

#### Scenario: Valid terminal is followed by contradictory exit
- **WHEN** an accepted terminal is committed before the child exits with a contradictory nonzero code or trailing anomaly
- **THEN** the terminal outcome, counts, timestamps, and activities remain unchanged and one supplementary abnormal classification reports the preserved raw exit

#### Scenario: Managed exit is interpreted with its lifecycle phase
- **WHEN** an orderly path selects a managed nonzero exit
- **THEN** pre-acceptance invalid exit 2 and startup/configuration exit 5 have no worker terminal; accepted domain exit 4 and cooperative exit 130 retain respectively the established Failed and Cancelled terminal; and output exit 6 has no terminal unless one committed before later transport failure, in which case that terminal remains authoritative and the transport mismatch is supplementary

### Requirement: Database unavailability and lock contention preserve job boundaries
Database-unavailable coverage SHALL run only for operations that actually use PostgreSQL. ProcessAssets advisory-lock contention SHALL acquire the fixed production key on a dedicated session, then prove one valid failed busy terminal, exit 3, zero domain work, release, and reacquisition. CoordinateLookup and CacheMutation SHALL NOT acquire that lock and SHALL NOT use exit 3.

#### Scenario: ProcessAssets database is unavailable
- **WHEN** ProcessAssets cannot open or retain the PostgreSQL session needed for count, work, or advisory-lock ownership
- **THEN** it produces the established failed/infrastructure outcome and exit 5 where no earlier committed terminal controls, closes database resources, releases local ownership, and leaves no child

#### Scenario: Advisory lock is held by another session
- **WHEN** ProcessAssets reaches the fixed advisory key while another live PostgreSQL session owns it
- **THEN** the worker emits exactly one valid failed busy terminal, exits 3, performs zero eligible-count/domain/cache/skipped/write work, and does not release the other session's lock

#### Scenario: Lock owner terminates
- **WHEN** ProcessAssets finishes, fails with exit 4, loses output with exit 6, cooperatively cancels with exit 130, or dies abruptly after acquiring the advisory lock
- **THEN** PostgreSQL releases that worker session's lock and a later explicit ProcessAssets attempt can reacquire the same fixed key

#### Scenario: Unlock or lease disposal is ambiguous
- **WHEN** unlock returns false, unlock fails, session ownership is uncertain, or lease disposal fails
- **THEN** the worker selects infrastructure exit 5 absent higher precedence, quarantines rather than reuses the physical session, retains ownership through bounded cleanup, and a later independent session can eventually acquire the fixed key

#### Scenario: Non-processing job executes
- **WHEN** CoordinateLookup or CacheMutation runs, fails, or is cancelled
- **THEN** no PostgreSQL advisory-lock acquisition is attempted and exit 3 is never observed

### Requirement: Cancellation and shutdown converge on one cleanup operation
The suite SHALL cover cooperative cancellation during ProcessAssets asset work and CacheMutation cache work, an unresponsive child, cancellation before and after readiness where valid, and parent host shutdown. The first accepted Stop or shutdown SHALL use the existing exact-session operation and production 10-second grace measured by injected `TimeProvider`; at most one cancel frame, deadline, and whole-tree kill SHALL occur. Final completion SHALL wait for process exit, stdout/stderr finality, protocol/bridge finality, disposal, and matching coordinator release.

#### Scenario: Cooperative cancellation completes before grace
- **WHEN** an accepted job observes the correlated cancel token and exits before ten monotonic seconds
- **THEN** one valid Cancelled terminal and exit 130 remain authoritative, no tree kill occurs, cancellation/grace/classification telemetry carries the canonical job identity, and all resources release

#### Scenario: Child ignores cancellation for ten seconds
- **WHEN** the exact child remains alive through the injected ten-second deadline
- **THEN** advancing virtual time to 9,999 ms does not kill early, reaching the 10,000 ms deadline attempts one whole-tree kill, finalizes exactly one Cancelled control-plane outcome with `process_classification=forced-stop` from the pre-existing exact-session Stop intent, and leaves no descendant alive

#### Scenario: Parent shuts down with an active worker
- **WHEN** host shutdown fences admission while one job owns the coordinator
- **THEN** no new job is admitted, shutdown joins the same cancellation/deadline/kill operation, and clean host completion is not reported until the child tree, streams, bridge, telemetry finality, disposal, and owner release settle

### Requirement: Protocol corruption fails closed while drains remain live
The suite SHALL inject malformed JSON, invalid UTF-8 or framing, EOF-truncated JSON, a line over 1,048,576 bytes, unknown semantic protocol/version/direction/category/type values, duplicate properties, payload invariant failures, wrong correlation, sequence gaps/regressions/duplicates, illegal lifecycle order, post-terminal frames, and stdout non-protocol text. It SHALL separately prove that otherwise valid same-version frames with additive unknown object properties are accepted and projected using only known fields. The first typed protocol fault SHALL be retained, invalid frames SHALL cause no projection, and fault containment SHALL continue draining both streams before one Failed finalization.

#### Scenario: Frame is malformed, truncated, or oversized
- **WHEN** stdout contains malformed JSON, EOF before a complete JSON object, invalid encoding/framing, or a frame over the byte limit
- **THEN** no partial event is accepted, `WorkerProtocolViolation` 6630 reports only the bounded closed category, one Failed outcome is finalized, and raw bytes are absent from telemetry

#### Scenario: Stream is out of order
- **WHEN** ready is missing/repeated, sequence is not exactly next, correlation changes, lifecycle cardinality is illegal, or data follows terminal
- **THEN** validation rejects the first offending frame without advancing accepted state or replaying later frames and cleanup follows the same bounded fault-containment path

#### Scenario: Semantic discriminator is unknown
- **WHEN** a frame uses an unsupported protocol, version, direction, category, type, job kind, or known category/type mismatch
- **THEN** it fails closed without coercion to a log, generic job, or terminal and telemetry exposes no raw frame or payload

#### Scenario: Additive same-version property is unknown
- **WHEN** an otherwise valid supported-version envelope or payload includes a non-duplicate unknown object property
- **THEN** the known event is accepted and projected, the additive property is omitted from canonical reserialization, and no protocol violation is emitted

### Requirement: Redirected pipes cannot deadlock finality
The fixture SHALL independently saturate stdout and stderr beyond ordinary pipe capacity, shall start both pumps immediately, and shall combine sustained valid protocol output with bounded stderr flooding. The controller SHALL not wait for readiness, terminal, or process exit before draining either pipe, and completion SHALL wait for process exit plus both drains while retaining only the established bounded stderr-tail metadata.

#### Scenario: Both redirected pipes fill concurrently
- **WHEN** a child emits enough stdout frames and stderr bytes to fill either OS pipe if read serially
- **THEN** ready and terminal or the injected protocol fault are observed within named deadlines, the child exits, both drains complete without deadlock, bounded stderr truncation metadata is correct, and no raw stderr enters block-66 telemetry

#### Scenario: Exit precedes trailing bytes
- **WHEN** the process exit signal is observed before all buffered stdout and stderr bytes are consumed
- **THEN** classification and coordinator release wait for both pumps and retain the terminal/protocol and stderr-tail facts found during final drainage

### Requirement: Cache failure cleanup permits only new-identity retry
CacheMutation failure and cancellation before atomic publication SHALL preserve the prior final cache, close provider handles, remove unique same-directory temporary and download artifacts, finalize and release the original job, and start no retry automatically. A later explicit request SHALL begin only after the first process, streams, handles, attempt artifacts, finality, and coordinator release have completed, and SHALL receive a new canonical JobId and new temporary path. Cancellation after atomic publication SHALL preserve the published cache while reporting no fabricated success.

#### Scenario: Cache preparation fails before publication
- **WHEN** controlled download, export, validation, metadata, readability, or atomic-replace preparation fails
- **THEN** the old final cache remains byte-for-byte intact, all attempt-owned temp/download files are absent, the job fails with its exact exit/terminal evidence, and no retry child starts

#### Scenario: Explicit retry follows repaired failure
- **WHEN** the failure is repaired and the user explicitly retries after prior cleanup and coordinator release
- **THEN** the retry uses a different JobId and temporary path, performs one new child launch, and may publish only its own validated cache before reporting success

#### Scenario: Cancellation occurs after atomic publication
- **WHEN** cancellation is observed after validated atomic replacement but before success finality
- **THEN** the published cache remains readable, attempt-owned temporary artifacts are absent, and the outcome does not claim a success that was not authoritatively finalized

### Requirement: Lifecycle telemetry is exact, bounded, and redacted
Every launched-job row SHALL assert the applicable block-66 catalog: 6610 `WorkerJobLaunchStarted`, 6611 `WorkerJobProcessStarted` when a PID exists, 6612 `WorkerJobReady` only after accepted readiness, 6620–6623 for the cancellation path actually taken, 6630 for protocol violation, 6640 only for an observed valid terminal, exactly one 6641 `WorkerJobProcessClassified`, and conditional 6650 `WorkerEventCoalescingSaturated`. Events SHALL reuse the canonical JobId, exact job kind and bounded origin, separate controller/worker PIDs, monotonic non-negative durations, fixed levels, and safe closed categories. Every 6641 SHALL assert the complete finalized available-or-unavailable memory field shape without a memory threshold. Each row SHALL declare whether replaceable progress can pressure the coalescer: ordinary failure/pipe rows SHALL use non-replaceable events and assert 6650 absent, while a deliberate pressure row SHALL assert at most one Warning 6650 with exactly `finality_kind`, `accepted_replaceable_count`, `accepted_lossless_count`, `replaced_count`, `delivered_snapshot_count`, `fifo_high_water`, `enqueue_wait_count`, `enqueue_wait_duration_ms`, `projection_duration_ms`, `cadence_notification_count`, nullable `terminal_flush_duration_ms`, `stale_rejection_count`, and `abnormal_abandonment_count`; terminal finality requires terminal-flush duration and nonterminal finality requires null. Structured state, rendering, scopes, and attached exceptions SHALL contain no payload, coordinate, asset/cache/request/result data, frame, stream tail, path, environment, configuration, command, SQL, credential, secret, arbitrary exception text, or stack.

#### Scenario: Failure row is classified
- **WHEN** a launched worker reaches process/stream finality
- **THEN** exactly one 6641 event correlates the canonical identity and exact job kind with the expected ready/terminal/exit/classification facts and contains no forbidden value

#### Scenario: No child is launched
- **WHEN** validation, Busy, or Unavailable resolves before process creation
- **THEN** events 6610–6650 are absent because validation/admission finalizes before launcher entry

#### Scenario: Committed terminal is followed by anomaly
- **WHEN** 6640 records an accepted terminal and later exit or stream evidence contradicts it
- **THEN** 6641 reports the anomaly once without a second 6640 event or any change to the committed terminal

#### Scenario: Coalescer pressure is absent or deliberate
- **WHEN** a matrix row reaches coalescer finality
- **THEN** a non-pressure row emits no 6650, while a deliberately saturated replaceable-progress row emits at most one 6650 containing only the finalized bounded aggregate fields

### Requirement: Test gating, platform capability, and isolation are explicit
Hermetic process, protocol, cancellation, pipe, arbitration, and filesystem/cache rows SHALL run in the normal test suite under default Integration and Performance exclusions. Only rows requiring an external PostgreSQL server SHALL carry the `Integration` category and run under the existing integration settings. Every fixture SHALL use a unique root and registered-process cleanup, disable live Overture/GADM access, name every finite deadline, and fail on leaked descendants or artifacts. A platform-dependent row MAY skip only after a named capability probe reports the unavailable OS/process-tree primitive, and the skip reason SHALL identify the missing capability.

#### Scenario: Normal test suite runs
- **WHEN** `npm run test` uses the default runsettings
- **THEN** all hermetic matrix rows execute and no external PostgreSQL, Docker daemon, network download, fixed port, or live geodata service is required

#### Scenario: PostgreSQL matrix runs
- **WHEN** `npm run test:integration` supplies the dedicated disposable or explicitly configured test database
- **THEN** database-unavailable and fixed-key lock rows execute serially with isolated state while Performance tests remain excluded

#### Scenario: Required platform capability is absent
- **WHEN** a supported test host cannot provide a specifically probed process-tree or signal behavior
- **THEN** only the dependent row reports an explicit skip containing that capability name while portable cleanup assertions continue to run
