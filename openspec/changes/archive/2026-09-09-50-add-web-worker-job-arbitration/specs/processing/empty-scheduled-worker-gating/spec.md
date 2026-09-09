## MODIFIED Requirements

### Requirement: Accepted empty schedule performs one advisory detection
Block 50 supersedes the accepted/admitted-empty premise. The automated test suite SHALL verify that a due scheduled occurrence invokes its advisory detector exactly once before JobId creation, `ProcessingState.MarkPending()`, adapter arming, or coordinator admission. A normal no-work result MUST create none of those objects or transitions. Detector cancellation, detector failure, or detector-positive admission loss MUST remain distinct outcomes.

#### Scenario: Detector reports no work before admission
- **WHEN** a due scheduled occurrence's detector completes normally with a no-work decision
- **THEN** exactly one detector call occurs and identity, pending, adapter, admission, backend, and worker observations remain zero

#### Scenario: Admitted detector reports no work
- **WHEN** a due scheduled occurrence's detector returns no work before admission
- **THEN** detection instead completes before admission and exactly one detector operation is observed without creating an admitted request

#### Scenario: Empty outcome boundary remains distinct
- **WHEN** the focused regression is executed
- **THEN** it starts with detector access available and distinguishes normal no-work from detector cancellation, detector failure, and detector-positive admission loss

### Requirement: Empty schedule materializes no worker or heavy graph
The automated test suite SHALL deterministically verify that detector no-work does not create a processing identity, mutate ProcessingState, arm an event adapter, resolve the coordinator/backend, build a worker command, start a process, access protocol/session bridging, resolve geodata, or construct any forbidden heavy dependency. Verification MUST use fail-on-use fakes, sentinels, or counters rather than inference from external symptoms.

#### Scenario: Detector returns no work before all heavy boundaries
- **WHEN** the detector returns a normal no-work decision
- **THEN** JobId creation, pending mutation, adapter arming, admission, backend/command/launcher/process/protocol access, and all heavy dependency counts remain zero

#### Scenario: Detector returns no work before backend resolution
- **WHEN** the detector returns a normal no-work decision before identity and admission
- **THEN** backend resolution, command construction, launcher and process-start calls, protocol/session and worker-event bridge access, and worker event/result input all remain zero

#### Scenario: Heavy dependencies remain unmaterialized
- **WHEN** the scheduler-local no-work closure completes
- **THEN** skipped/config/batch collaborators and Overture, GADM, airport, country-index, resolver, and in-process execution dependencies have zero resolution, construction, and operation counts

### Requirement: Empty schedule completes through the exact local zero lifecycle
Block 50 replaces the former pending-to-zero ProcessingState lifecycle with a scheduler-local no-work closure because no processing run identity or admission exists. The test suite SHALL verify the established bounded no-work schedule/log presentation, clean trigger completion, no processing counters/error/activity/timestamps fabricated for a run, and no cancellation owner, callbacks, coordinator handle, worker event, or worker result.

#### Scenario: Detector-empty closure leaves processing idle
- **WHEN** the normal no-work result is finalized before identity and admission
- **THEN** ProcessingState remains idle and unchanged while the scheduler records its bounded no-work outcome and retains no owner or worker residue

#### Scenario: Local zero finalization reaches clean idle state
- **WHEN** a due scheduled occurrence's normal no-work decision closes locally
- **THEN** it is finalized at the scheduler boundary without a ProcessingState run, active request, cancellation owner, activity, callback, coordinator handle, start timestamp, or completion timestamp
