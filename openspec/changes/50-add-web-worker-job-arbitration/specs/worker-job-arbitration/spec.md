## Purpose

Defines one process-local Web control-plane policy that prevents concurrent heavy worker jobs while preserving exact worker identity, capability-owned status, cancellation/finality ownership, and processing-only cross-process locking.

## ADDED Requirements

### Requirement: Heavy descriptors share one exclusive resource
The Web control plane SHALL classify registered worker jobs from immutable typed descriptor metadata containing exact job kind, safe category, cancellability, heavy/geodata flags, and admission resource class. `ProcessAssets`, `CoordinateLookup`, and future `CacheMutation` descriptors marked for the exclusive heavy-geodata resource MUST contend for one process-local slot, and the system MUST NOT admit two such jobs concurrently.

#### Scenario: Different heavy kinds contend
- **WHEN** any exclusive heavy-geodata job owns the slot and a different heavy kind requests it in the same Web process
- **THEN** the second job is not admitted or launched

#### Scenario: Descriptor metadata is inconsistent
- **WHEN** composition registers an unknown resource class, duplicate kind, or descriptor whose heavy flags conflict with its declared resource class
- **THEN** startup validation fails before request acceptance or worker launch

### Requirement: Admission has three fail-fast outcomes
The shared admission boundary SHALL return exactly `Admitted`, `Busy`, or `Unavailable`. `Admitted` SHALL carry the sole owner handle; `Busy` SHALL carry a safe immutable active snapshot; and `Unavailable` SHALL carry a bounded safe reason for a pre-launch condition such as shutdown fencing or unavailable launch capability. Busy and unavailable requests MUST launch no process. Admission SHALL be atomic first-successful-request-wins with no wait queue, retry, preemption, priority promotion, fairness guarantee, or starvation guarantee.

#### Scenario: Two requests race for idle
- **WHEN** two heavy requests race for an idle coordinator
- **THEN** exactly one receives Admitted, the other receives Busy naming the admitted owner's safe category/lifecycle, and at most one worker can start

#### Scenario: Coordinator is fenced for shutdown
- **WHEN** a valid request arrives after shutdown has closed admission
- **THEN** it receives Unavailable and no worker process is created

#### Scenario: Busy owner finishes
- **WHEN** the active owner reaches authoritative finality and releases its handle
- **THEN** a later request may be admitted, but the rejected request is not automatically retried or queued

### Requirement: Active identity and lifecycle are exact and monotonic
An admitted request SHALL use one canonical `JobId` from admission through launch, events, cancellation, classification, the internal owner lifecycle record, and release. For `ProcessAssets`, it MUST equal the processing `RunId`. The coordinator MUST NOT mint a lease, attempt, run, or correlation identity. Its internal owner record SHALL contain exact job identity and kind, category/origin, cancellability, monotonic lifecycle, timestamps, and a nullable child PID that is absent before successful process creation. Exact identity and PID exist only for controller correlation, cancellation, stale-update rejection, and cleanup. Busy results and generic read-only diagnostics SHALL expose only bounded friendly category/origin/cancellability/lifecycle/timing facts and MUST NOT expose JobId or PID to UI.

#### Scenario: Processing is admitted
- **WHEN** a manual or scheduled ProcessAssets request receives Admitted
- **THEN** the coordinator identity, processing RunId, v2 JobId, session identity, cancellation target, and final release identity are the same value

#### Scenario: Stale or mismatched mutation arrives
- **WHEN** a stale handle or an update/release with the wrong job identity or kind is presented
- **THEN** it cannot alter or clear the current active owner

#### Scenario: Launch has not created a process
- **WHEN** an admitted job is starting but no child process has been created
- **THEN** the internal owner record has no PID and safe diagnostics show only the starting lifecycle without an identity or PID field

### Requirement: Processing, Lookup, and scheduled interactions are non-preemptive
Manual ProcessAssets, CoordinateLookup, and future CacheMutation requests SHALL contend equally under first-admitted-wins. A scheduled ProcessAssets trigger MUST NOT interrupt or cancel an active manual or interactive job, and an interactive request MUST NOT preempt an already-admitted scheduled job. Duplicate manual processing MUST receive Busy while any exclusive heavy job owns the slot.

#### Scenario: Lookup owns the slot
- **WHEN** manual or scheduled processing requests admission during an active CoordinateLookup
- **THEN** Lookup continues unchanged, processing launches no worker, and the processing request observes the appropriate busy/skipped trigger outcome

#### Scenario: Scheduled processing owns the slot
- **WHEN** Lookup is submitted after scheduled ProcessAssets has been admitted
- **THEN** scheduled processing continues unchanged and Lookup receives Busy without a worker or local fallback

#### Scenario: Duplicate manual processing is submitted
- **WHEN** manual processing is active and another manual processing request arrives
- **THEN** the duplicate receives Busy and does not start a second worker

### Requirement: Scheduled no-work detection precedes heavy reservation
When the scheduler has a lightweight local eligibility/no-work detector, it SHALL run that detector before creating the scheduled job identity, marking processing pending, or attempting heavy admission. A no-work result SHALL neither reserve the slot nor launch a worker. A positive result SHALL then attempt atomic admission and MAY lose to a concurrent request without creating a queue. Manual processing SHALL retain its detector-bypass behavior. After successful ProcessAssets admission, processing SHALL mark pending immediately before asynchronous launch.

#### Scenario: Scheduled detector reports no work
- **WHEN** a scheduled trigger's local detector reports no eligible work
- **THEN** the coordinator remains idle, no JobId is created, processing is not marked pending, and no child starts

#### Scenario: Another request wins after positive detection
- **WHEN** scheduled detection reports eligible work but Lookup acquires the slot before scheduled admission
- **THEN** scheduled processing does not launch or reserve a future place and follows its existing skipped/coalesced trigger semantics

#### Scenario: Manual processing is requested
- **WHEN** a valid manual processing request is submitted while idle
- **THEN** it bypasses the scheduled detector, attempts admission directly, and marks pending immediately after admission

### Requirement: Owner-controlled cancellation and authoritative release
Only the admitted owner handle SHALL control normal cancellation and release for its exact job. A busy/unavailable caller SHALL receive no cancellation capability. The coordinator MUST NOT release on terminal observation alone; startup failure, completion, cancellation, crash, protocol/transport failure, forced stop, or disposal SHALL be classified and, when a process exists, reach process exit plus stdout/stderr/bridge finality before exact-once release. The coordinator MUST NOT fabricate worker terminals or duplicate launcher kill ownership.

#### Scenario: Cancellation races completion
- **WHEN** owner cancellation races a valid completed terminal
- **THEN** the existing session finalization rules choose one authoritative outcome, all process/stream cleanup finishes, and admission releases exactly once

#### Scenario: Worker crashes before terminal
- **WHEN** an admitted worker exits unexpectedly without a valid terminal
- **THEN** the controller classifier finalizes failure, drains owned streams, releases once, and a later request can be admitted

#### Scenario: Non-owner tries to cancel or release
- **WHEN** a rejected caller or stale handle targets the active job
- **THEN** the active worker continues and its admission remains owned by the original handle

### Requirement: Shutdown fences admission before stopping work
Web-host shutdown SHALL atomically and permanently close admission before requesting stop of the current owner. A raced request SHALL either be admitted before the fence and then be stopped/drained as that exact owner, or receive Unavailable and launch nothing. Shutdown SHALL delegate to the owner-bound session stop path, await bounded authoritative finality/release, and make repeated shutdown requests join the same operation.

#### Scenario: Shutdown starts while a job is active
- **WHEN** host shutdown begins with an admitted heavy job
- **THEN** new requests receive Unavailable, the exact active session receives one shared stop operation, and the slot is not released before final cleanup

#### Scenario: Admission races shutdown
- **WHEN** admission and the shutdown fence occur concurrently
- **THEN** exactly one linearized result occurs: either shutdown owns stopping the admitted handle or the request is unavailable and no worker starts

### Requirement: Generic diagnostics remain separate from capability-owned UI state
The coordinator SHALL expose an immutable read-only idle/active diagnostic projection containing only bounded friendly arbitration facts and no PID or JobId. It MUST NOT copy Lookup results/diagnostics, cache results, worker logs/activities, or processing counts into that projection. `ProcessingState` and block 44's Dashboard/NavMenu lifecycle card SHALL remain ProcessAssets-specific and MUST NOT receive CoordinateLookup or CacheMutation lifecycle events. Lookup and cache pages SHALL retain their own capability state. Generic diagnostic observers MUST NOT gain admission, release, cancellation, or automatic card-rendering authority.

#### Scenario: Lookup runs
- **WHEN** CoordinateLookup is active
- **THEN** generic diagnostics identify only an active friendly Lookup category while ProcessingState and the ProcessAssets card receive no Lookup lifecycle, log, activity, count, terminal, PID, or JobId update and the Lookup page retains its own state

#### Scenario: Processing runs
- **WHEN** ProcessAssets is active
- **THEN** generic diagnostics expose only safe arbitration lifecycle without PID/JobId while processing events, counts, and the ProcessAssets card remain owned by the processing projection

### Requirement: Local arbitration does not replace distributed processing exclusion
The coordinator SHALL be singleton and process-local within each interactive Web host. Only ProcessAssets SHALL retain the established PostgreSQL advisory run lock, and exit code 3 SHALL remain exclusive to that cross-process processing-busy outcome. CoordinateLookup and future CacheMutation SHALL NOT acquire that lock merely for local arbitration. Busy and unavailable admission SHALL fabricate no worker exit code. The system MUST document that multiple Web containers can concurrently admit Lookup/cache work and can overlap those jobs with processing in another container.

#### Scenario: Local ProcessAssets is admitted but advisory lock is busy
- **WHEN** the ProcessAssets worker loses the cross-process PostgreSQL advisory-lock race
- **THEN** it returns the established exit 3 outcome, reaches authoritative cleanup, and releases the local coordinator slot

#### Scenario: Lookup is locally busy
- **WHEN** CoordinateLookup receives Busy before launch
- **THEN** no process or exit code is created and exit 3 is not reported

#### Scenario: Two Web containers are deployed
- **WHEN** separate Web processes each have an idle local coordinator
- **THEN** each can admit a heavy Lookup/cache job, and status does not claim distributed exclusion

### Requirement: Block 49 temporary gate is replaced without caller changes
Block 50 SHALL replace and delete block 49's temporary lookup-only gate implementation and registration behind the existing closed admission/launch contract. It MUST NOT retain nested temporary and shared gates, change Lookup page behavior, mint a second identity, or require block 49 to be reapplied as a prerequisite. Future CacheMutation SHALL consume the same descriptor/resource and outcome model without this change implementing block 51's operations.

#### Scenario: Lookup uses shared arbitration after migration
- **WHEN** block 50 is applied and Lookup requests admission
- **THEN** the existing Lookup controller receives the same Admitted, Busy, or Unavailable contract from the shared coordinator and requires no Razor behavior change

#### Scenario: Temporary implementation is inspected
- **WHEN** composition and source are inspected after migration
- **THEN** only the shared coordinator owns heavy admission and no lookup-only gate or nested registration remains
