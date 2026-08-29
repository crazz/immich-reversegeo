## MODIFIED Requirements

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

### Requirement: Empty scheduled attempts complete locally
When the detector reports no work, the system SHALL resolve neither execution backend nor coordinator admission, SHALL create no processing identity or pending lifecycle, SHALL start no child process, and SHALL construct or access no in-process executor or processing geodata dependency. It SHALL record the established bounded scheduler no-work outcome without fabricating a processing run, worker terminal, or worker result. Work appearing afterward waits for a later ordinary trigger.

#### Scenario: Detector-empty scheduled occurrence
- **WHEN** the detector reports no eligible work before identity and admission
- **THEN** the occurrence closes locally with no ProcessingState run transition, backend resolution, child launch, worker event/result, skipped/config/batch access, or geodata work

### Requirement: Predispatch cancellation and failure finalize locally
The detector SHALL use the scheduler/host cancellation token before any run identity or admission owner exists. Cancellation SHALL close the occurrence through the established scheduler-level cancellation path, and an unexpected detector failure SHALL use the established bounded scheduler-level failure path. Neither outcome SHALL create a JobId, mark processing pending, arm an adapter, attempt admission, fabricate worker events, fall back, or automatically retry.

#### Scenario: Detector is cancelled before admission
- **WHEN** the detector observes scheduler or host cancellation
- **THEN** the occurrence closes without a processing lifecycle, coordinator owner, or worker launch

#### Scenario: Detector fails before admission
- **WHEN** the detector throws a non-cancellation failure
- **THEN** bounded scheduler failure presentation is recorded without a processing identity, pending state, coordinator owner, or worker launch
