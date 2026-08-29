## Why

Block 35's scheduled pre-launch gate is intentionally temporary and exposes only a bare boolean operation. Phase 8 needs one stable, lightweight Web control-plane contract that preserves the same advisory launch decision while allowing the count-backed adapter to be replaced by the finalized full-eligibility existence strategy without coupling scheduling to SQL or weakening the worker's authoritative eligibility count.

## What Changes

- Replace or alias the temporary scheduled-only gate with one immutable-request processing-work detector contract and one result whose only launch decision is `HasWork` plus bounded, low-cardinality diagnostic metadata.
- Keep the detector strictly advisory: it observes current eligibility for scheduled launch gating but does not reserve a work set, promise an exact count, or make the Web observation authoritative.
- Supply a stateless count-backed adapter first. It calls the existing exact eligibility query once and maps a positive count to `HasWork = true`, preserving block 35 behavior until change 58 substitutes a bounded existence implementation.
- Preserve the existing eligibility predicate: both city and country are null, latitude and longitude are present, and the asset is not deleted.
- Define cancellation, failure, and race behavior explicitly. Cancellation and faults remain distinct from a no-work result; a positive Web observation may be followed by worker zero, and work appearing after a negative observation waits for a later ordinary trigger.
- Keep exact counts where they remain authoritative or user-facing: the processing worker/executor owns the run eligibility count and Dashboard statistics continue to use the repository count. Manual Dashboard runs and public Run-once execution bypass scheduled detection.
- Keep detection read-only and lightweight in Web: no Immich mutation, skipped-store access, processing-settings read, batch/geodata/cache work, worker request enrichment, or backend/worker launch side effect.
- Establish a DI and test-fake seam that change 58 can extend with the finalized full-eligibility existence strategy without exposing SQL, schema columns, credentials, row identities, cursor values, or work sets to the scheduler; finalized changes 61–64 add no incremental strategy, reconciliation cadence, or NAS control.

## Capabilities

### New Capabilities
- `processing-work-detection`: Provides a stable advisory work/no-work contract for scheduled launch gating, including safe result metadata, failure semantics, authority boundaries, and future strategy extension.

### Modified Capabilities
- None.

## Impact

The change applies after blocks 35–55 are landed and reconciles their scheduler/coordinator, deployment-mode, child-worker, and lightweight Web composition contracts. Expected implementation areas are the dependency-light processing control-plane contracts, the existing scheduled gate call site, the lightweight PostgreSQL repository adapter, Standard-mode DI composition, and focused tests/fakes. Dashboard counting, worker execution, worker protocol, processing eligibility, manual and Run-once dispatch, settings, geodata, skipped storage, and Immich schema remain unchanged. Changes 58–60 consume and observe the full-eligibility seam; finalized changes 61–64 preserve it and authorize no watermark, incremental detector, separate reconciliation cadence, or NAS-specific control.
