## Why

Blocks 33–36 establish one coordinator contract, exercise both trigger paths through child execution, and protect the empty scheduled gate. Block 37 can now make process isolation the internal production default without exposing a user-facing backend choice before block 38 removes the transition path.

## What Changes

- Change only the temporary internal backend-selection default from InProcess to ChildWorker for manual and eligible scheduled processing.
- Keep the existing explicit InProcess selection as a code-only composition seam for tests and an emergency transition rebuild/revert until block 38; do not expose it through settings, environment variables, command-line arguments, endpoints, or UI.
- Preserve the block-35 empty scheduled gate: a no-work result completes locally without resolving either backend or launching a process.
- Validate the selected backend's prerequisites during startup without constructing a per-run backend or worker-only geodata graph; missing child prerequisites fail startup visibly rather than selecting in-process execution.
- Keep the selected backend fixed for each admitted run. Child startup, protocol, cancellation, projection, crash, or cleanup failures use the established terminal path with no automatic fallback, retry, or resubmission.
- Preserve manual and scheduled cancellation, ProcessingState, terminal, cleanup, and retrigger behavior across the default change.
- Package and launch the internal child role from the same application assembly and deployment image; do not add a second worker artifact or image.

## Capabilities

### New Capabilities

- `processing/child-worker-default`: makes child-worker execution the internal default while preserving empty-schedule gating, visible prerequisite failures, lifecycle parity, and the temporary code-only fallback.

### Modified Capabilities

- None.

## Impact

The change is limited to the block-33 internal selection/composition boundary and its startup, coordinator, scheduler, packaging, and test coverage. It depends on passing blocks 33–36 and the Phase 4 process integration suite. It changes no public configuration contract, deployment mode, worker protocol, Immich schema, processing eligibility, geodata algorithm, or UI surface.

## Audit Reconciliation

Block 36 must be applied first. Preserve four distinct outcomes: authoritative committed worker terminals; local admission rejection (no child); advisory Busy (the canonical failed child terminal with no eligibility and four zero counts); and forced raw kill, which is transport evidence classified through block 30 and is not itself a terminal. No fallback, retry, replay, or in-process execution follows any of them.

