# processing/scheduled-child-worker-execution Specification

## Purpose

Routes admitted scheduled processing through child-worker isolation only when a pre-launch eligibility decision finds work, while preserving the established scheduler and processing-state lifecycle.

## Requirements

### Requirement: Scheduled detection follows local admission
Block 50 supersedes the original admission-first ordering. For a due scheduled occurrence, the system SHALL perform exactly one lightweight work-detection operation before creating a JobId, publishing a cancellation/owner handle, calling `ProcessingState.MarkPending()`, arming the processing adapter, or attempting coordinator admission. A normal no-work result SHALL close at the scheduler boundary with no identity, pending mutation, adapter, admission, backend resolution, or worker launch. A positive result SHALL then create the sole ProcessAssets RunId/JobId, atomically attempt shared admission, and, only after admission succeeds, publish the owner and call `MarkPending()` immediately before adapter arming and asynchronous launch. Manual processing SHALL continue to bypass scheduled detection and attempt admission directly.

#### Scenario: Scheduled occurrence finds no work before admission
- **WHEN** a due scheduled occurrence's detector reports no current eligible work
- **THEN** no JobId, owner handle, pending state, adapter, coordinator reservation, backend, or child is created

#### Scenario: Positive detection loses the admission race
- **WHEN** detection reports work but another heavy job wins coordinator admission before the scheduled request
- **THEN** scheduled processing follows its existing skipped/coalesced trigger semantics without pending state, queueing, reservation, or worker launch

#### Scenario: Positive detection is admitted
- **WHEN** detection reports work and the scheduled ProcessAssets request wins admission
- **THEN** one RunId/JobId and owner are created, pending is marked immediately after admission, the matching adapter is armed, and exactly one child launch path is eligible

#### Scenario: Manual processing is requested
- **WHEN** a valid manual ProcessAssets request is submitted
- **THEN** it does not invoke the scheduled detector and attempts shared admission directly

#### Scenario: Admitted scheduled occurrence reaches detection
- **WHEN** a due scheduled occurrence reaches its lightweight detector
- **THEN** detection instead occurs before identity, owner publication, pending state, adapter arming, coordinator admission, and backend resolution

#### Scenario: Scheduled occurrence is locally busy
- **WHEN** detection reports work but shared admission is already owned by any exclusive heavy job
- **THEN** the existing scheduled-contention outcome is recorded and no pending mutation, adapter, backend, worker, or queued reservation is started

### Requirement: Initial detector is advisory and count-backed
The scheduled detector SHALL expose only a work/no-work decision and SHALL initially implement it by evaluating the current exact eligibility count as greater than zero. It SHALL use the same database predicate as the executor count, SHALL perform no skipped-ID, processing-configuration, batch, protocol, resolver, cache, or geodata operation, and SHALL NOT publish its count as the authoritative run eligibility. The child worker's executor SHALL retain the authoritative exact count and eligibility event.

#### Scenario: Initial detector finds work
- **WHEN** the initial detector's exact query returns a positive count
- **THEN** the detector reports work without publishing that count as run eligibility

#### Scenario: Initial detector finds no work
- **WHEN** the initial detector's exact query returns zero
- **THEN** the detector reports no work without reading non-detector configuration or processing dependencies

### Requirement: Empty scheduled attempts complete locally
When the detector reports no work, the system SHALL resolve neither execution backend nor coordinator admission, SHALL create no processing identity or pending lifecycle, SHALL start no child process, and SHALL construct or access no in-process executor or processing geodata dependency. It SHALL record the established bounded scheduler no-work outcome without fabricating a processing run, worker terminal, or worker result. Work appearing afterward waits for a later ordinary trigger.

#### Scenario: Detector-empty scheduled occurrence
- **WHEN** the detector reports no eligible work before identity and admission
- **THEN** the occurrence closes locally with no ProcessingState run transition, backend resolution, child launch, worker event/result, skipped/config/batch access, or geodata work

#### Scenario: Empty admitted scheduled attempt
- **WHEN** a due scheduled occurrence's detector returns no eligible work
- **THEN** the detector-empty occurrence instead closes before admission with the same zero backend, child, protocol, skipped/config/batch, and geodata effects and with no processing lifecycle

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
The detector SHALL use the scheduler/host cancellation token before any run identity or admission owner exists. Cancellation SHALL close the occurrence through the established scheduler-level cancellation path, and an unexpected detector failure SHALL use the established bounded scheduler-level failure path. Neither outcome SHALL create a JobId, mark processing pending, arm an adapter, attempt admission, fabricate worker events, fall back, or automatically retry.

#### Scenario: Detector is cancelled before admission
- **WHEN** the detector observes scheduler or host cancellation
- **THEN** the occurrence closes without a processing lifecycle, coordinator owner, or worker launch

#### Scenario: Detector fails before admission
- **WHEN** the detector throws a non-cancellation failure
- **THEN** bounded scheduler failure presentation is recorded without a processing identity, pending state, coordinator owner, or worker launch

#### Scenario: Detector is cancelled
- **WHEN** a due scheduled occurrence's detector observes scheduler or host cancellation
- **THEN** cancellation is handled at the scheduler boundary before identity, admission, pending state, adapter arming, backend resolution, or worker launch

#### Scenario: Detector fails
- **WHEN** a due scheduled occurrence's detector throws a non-cancellation failure
- **THEN** bounded scheduler failure presentation is recorded before identity, admission, pending state, adapter arming, backend resolution, or worker launch

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
