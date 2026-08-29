## Why

Change 57 deliberately leaves scheduled full-eligibility detection count-backed even though the Web control plane needs only an advisory yes/no launch decision. Replacing that temporary adapter with a bounded existence probe removes the unnecessary pre-launch aggregate while preserving the worker's independent authoritative count and every existing eligibility rule.

## What Changes

- Replace only change 57's count-backed full-eligibility detector implementation with a PostgreSQL existence probe that returns a boolean and cannot expose an exact count.
- Preserve the exact current predicate: an inner join from `asset` to `asset_exif` on quoted `"assetId"`, both city and country null, both latitude and longitude non-null, and asset `"deletedAt"` null.
- Keep the probe independent of processing/overwrite settings and skipped-asset storage. The current product has no overwrite eligibility setting; skipped IDs remain included in database eligibility and are still consumed from one worker-owned snapshot later in processing.
- Preserve change 57's request/result contract, cancellation and failure distinction, safe bounded diagnostics, advisory race semantics, local finalization, and Standard-only scheduled call site. Query failure has no false/no-work fallback.
- Keep Dashboard statistics and the processing worker on their existing exact-count operation; do not change worker totals, batching, or parallelism.
- Add repository correctness, real-PostgreSQL integration, and opt-in EXPLAIN ANALYZE/performance coverage that records useful evidence without asserting unstable plan nodes, costs, timings, or buffer counts.
- Add no schema object, index, geodata access, public setting, worker-protocol field, or telemetry owned by adjacent numbered changes.

## Capabilities

### New Capabilities
- `scheduled-work-gating`: Uses a count-free full-eligibility existence observation for internal scheduled launch gating while preserving eligibility, authority, error, cancellation, and race semantics.

### Modified Capabilities
- None.

## Impact

The implementation follows applied change 57 and is limited to its concrete full-eligibility detector adapter, the lightweight PostgreSQL repository boundary that already owns the exact count, Standard scheduled-detector composition if the concrete registration name changes, and focused tests. Dashboard counting, worker execution and authoritative counting, skipped storage, processing configuration, Immich schema/indexes, geodata services, worker protocol, and blocks 59 and later remain unchanged.
