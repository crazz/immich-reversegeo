## MODIFIED Requirements

### Requirement: Accepted empty schedule performs one advisory detection
Block 50 supersedes the accepted/admitted-empty premise. The automated test suite SHALL verify that a due scheduled occurrence invokes its advisory detector exactly once before JobId creation, `ProcessingState.MarkPending()`, adapter arming, or coordinator admission. A normal no-work result MUST create none of those objects or transitions. Detector cancellation, detector failure, or detector-positive admission loss MUST remain distinct outcomes.

#### Scenario: Detector reports no work before admission
- **WHEN** a due scheduled occurrence's detector completes normally with a no-work decision
- **THEN** exactly one detector call occurs and identity, pending, adapter, admission, backend, and worker observations remain zero

### Requirement: Empty schedule materializes no worker or heavy graph
The automated test suite SHALL deterministically verify that detector no-work does not create a processing identity, mutate ProcessingState, arm an event adapter, resolve the coordinator/backend, build a worker command, start a process, access protocol/session bridging, resolve geodata, or construct any forbidden heavy dependency. Verification MUST use fail-on-use fakes, sentinels, or counters rather than inference from external symptoms.

#### Scenario: Detector returns no work before all heavy boundaries
- **WHEN** the detector returns a normal no-work decision
- **THEN** JobId creation, pending mutation, adapter arming, admission, backend/command/launcher/process/protocol access, and all heavy dependency counts remain zero

### Requirement: Empty schedule completes through the exact local zero lifecycle
Block 50 replaces the former pending-to-zero ProcessingState lifecycle with a scheduler-local no-work closure because no processing run identity or admission exists. The test suite SHALL verify the established bounded no-work schedule/log presentation, clean trigger completion, no processing counters/error/activity/timestamps fabricated for a run, and no cancellation owner, callbacks, coordinator handle, worker event, or worker result.

#### Scenario: Detector-empty closure leaves processing idle
- **WHEN** the normal no-work result is finalized before identity and admission
- **THEN** ProcessingState remains idle and unchanged while the scheduler records its bounded no-work outcome and retains no owner or worker residue
