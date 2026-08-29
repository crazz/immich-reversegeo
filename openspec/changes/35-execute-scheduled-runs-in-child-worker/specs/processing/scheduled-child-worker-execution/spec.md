## Purpose

Routes admitted scheduled processing through child-worker isolation only when a pre-launch eligibility decision finds work, while preserving the established scheduler and processing-state lifecycle.

## ADDED Requirements

### Requirement: Scheduled detection follows local admission
The system SHALL perform exactly one scheduled work-detection operation only after the scheduled request has acquired process-local admission. For an admitted request, the system SHALL publish its cancellable active handle, mark processing pending immediately, and arm the matching state adapter before detection. A locally rejected request SHALL perform no pending mutation, detection, backend resolution, or worker launch.

#### Scenario: Admitted scheduled occurrence reaches detection
- **WHEN** a due scheduled occurrence acquires process-local admission
- **THEN** its active cancellation handle is observable before pending state, pending state is published before adapter arming, and exactly one detector operation follows before backend resolution

#### Scenario: Scheduled occurrence is locally busy
- **WHEN** a due scheduled occurrence is rejected because a local processing attempt already owns admission
- **THEN** the existing scheduled-contention log is recorded and no detector, backend, worker, or new processing-state lifecycle is started

### Requirement: Initial detector is advisory and count-backed
The scheduled detector SHALL expose only a work/no-work decision and SHALL initially implement it by evaluating the current exact eligibility count as greater than zero. It SHALL use the same database predicate as the executor count, SHALL perform no skipped-ID, processing-configuration, batch, protocol, resolver, cache, or geodata operation, and SHALL NOT publish its count as the authoritative run eligibility. The child worker's executor SHALL retain the authoritative exact count and eligibility event.

#### Scenario: Initial detector finds work
- **WHEN** the initial detector's exact query returns a positive count
- **THEN** the detector reports work without publishing that count as run eligibility

#### Scenario: Initial detector finds no work
- **WHEN** the initial detector's exact query returns zero
- **THEN** the detector reports no work without reading non-detector configuration or processing dependencies

### Requirement: Empty scheduled attempts complete locally
When the detector reports no work, the system SHALL resolve neither execution backend, SHALL start no child process, SHALL receive no worker protocol event, and SHALL construct or access no in-process executor or processing geodata dependency through this route. It SHALL project eligibility zero and a local completed-zero lifecycle through the identity-checked state adapter, return to idle, set start and completion timestamps, clear the run counters and last error, and append the established lines in order: `Run started — nothing to process, all assets already have location data.` then `Run complete. Processed=0 Skipped=0 Errors=0`. This local closure SHALL release only the matching active handle and SHALL NOT fabricate a worker terminal event or worker result.

#### Scenario: Empty admitted scheduled attempt
- **WHEN** the detector reports no eligible work for the admitted scheduled request
- **THEN** the attempt reaches the established idle zero-run presentation without backend resolution, child launch, worker events, skipped-ID access, processing-config access, batches, or geodata work

### Requirement: Eligible scheduled attempts use one child backend
When the detector reports work and child-worker is the frozen temporary backend selection, the system SHALL lazily resolve and invoke exactly that child backend once with the admitted scheduled request, armed adapter, and coordinator-owned cancellation token. The scheduled caller SHALL remain awaiting the accepted attempt until authoritative terminal handling, process and stream finality, child cleanup, and matching coordinator-handle release have settled.

#### Scenario: Eligible child execution completes
- **WHEN** scheduled detection reports work and the selected child execution completes normally
- **THEN** one child is launched, its events drive the shared processing lifecycle, and schedule reevaluation occurs only after the accepted attempt has reached terminal cleanup

#### Scenario: Worker authoritative count finds no work
- **WHEN** detection reports work but the launched worker's authoritative exact count returns zero because eligibility changed before executor counting
- **THEN** the child completes through the executor's ordinary zero-work lifecycle and the Web host does not fall back, retry, or launch a replacement worker

#### Scenario: Worker advisory lock is busy
- **WHEN** the child starts but its PostgreSQL advisory run lock is held by another process
- **THEN** the worker produces the reserved failed busy outcome and exit code 3 with zero domain work, rather than treating the occurrence as a local scheduled skip or retrying it

### Requirement: Predispatch cancellation and failure finalize locally
The system SHALL use the matching admitted handle's cancellation token for detection. Active cancellation before backend dispatch SHALL close the pending attempt locally with the established `Run cancelled.` and completion-summary ordering, shall add no error, and SHALL launch no worker. An unexpected detector failure SHALL close the matching pending attempt locally through the established pre-eligibility failed presentation using bounded safe detail, SHALL return the coordinator to idle, and SHALL launch no worker. Neither outcome SHALL resolve a backend, fabricate worker protocol events, fall back to in-process execution, or automatically retry the occurrence.

#### Scenario: Detector is cancelled
- **WHEN** the admitted scheduled detector observes cancellation from the matching run or host token before backend dispatch
- **THEN** the attempt returns to idle through the existing pre-eligibility cancellation presentation without an error or worker launch

#### Scenario: Detector fails
- **WHEN** the admitted scheduled detector throws a non-cancellation failure
- **THEN** the attempt returns to idle through the existing pre-eligibility fatal-error and completion-summary presentation with safe detail and no worker launch

### Requirement: Existing snapshot and scheduling boundaries remain unchanged
The schedule enabled/cron snapshot SHALL remain pinned according to the existing scheduler contract, and the backend selection SHALL remain the immutable internal composition value frozen on the admitted handle. The detector SHALL read no AppConfig or processing settings. The worker request SHALL continue to contain only its established immutable request identity and scheduled trigger; credentials, schedule data, detector output, eligibility totals, work sets, and processing settings SHALL NOT be added. After its authoritative nonzero count, the worker executor SHALL take the existing single processing-config snapshot. Configuration changes SHALL NOT wake or replan an active schedule wait, and this change SHALL add no catch-up, fallback, replacement, or retry behavior.

#### Scenario: Configuration changes around a scheduled occurrence
- **WHEN** configuration changes after a schedule plan is pinned or while detection and child startup are in progress
- **THEN** the pinned occurrence remains unchanged and any processing settings are observed only at the worker executor's existing non-empty snapshot boundary

#### Scenario: Work appears after a no-work decision
- **WHEN** the detector reports no work and a matching asset becomes eligible immediately afterward
- **THEN** the completed occurrence is not reopened and the asset waits for a later ordinary trigger

## Audit Reconciliation

Scope is scheduled accepted execution only and consumes the established detector/local-finalizer contracts and prerequisites; it neither changes manual routing nor makes child-worker the default. The default remains in-process until block 37. Its detector-zero local path emits no worker producer event or worker result, while a canonical advisory Busy remains a child terminal distinct from local admission rejection.

