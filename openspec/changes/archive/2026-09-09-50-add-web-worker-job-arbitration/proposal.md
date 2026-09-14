## Why

Block 49 deliberately leaves Lookup behind a temporary lookup-only gate, while processing still has its own local lock and later cache mutation will add another heavy entry point. Block 50 replaces that transition state with one Web control-plane arbitrator so heavy worker jobs cannot overlap inside a Web process and every caller receives the same admission and lifecycle contract.

## What Changes

- Replace block 49's temporary lookup-only gate/registration with one process-local coordinator shared by exact v2 `ProcessAssets`, `CoordinateLookup`, and future `CacheMutation` descriptors; all heavy/geodata-bearing jobs use one exclusive resource class.
- Define a closed admission result of `Admitted`, `Busy`, or `Unavailable`: only the admitted owner carries exact identity, while Busy exposes identity-free safe category/lifecycle metadata. Admission is atomic, fail-fast, first-successful-request wins, never preempts, and adds no queue, priority promotion, fairness, or starvation guarantee.
- Reconcile manual and scheduled processing with Lookup: manual/interactive jobs contend equally, scheduled work never interrupts an owner, and any lightweight scheduled no-work detection happens before reserving the heavy slot.
- Keep cancellation/session/process ownership with the admitted caller and launcher, release only after authoritative classification and process/stream finality, and add a shutdown fence that rejects new work while stopping and draining the current owner.
- Keep coordinator diagnostics separate from processing-specific `ProcessingState` and capability-owned Lookup/cache page state. Retain exact JobId/PID only in the internal owner record for correlation and cleanup; safe Busy/diagnostic projections expose bounded category/origin/lifecycle facts and never render PID or JobId in UI. The existing block-44 card remains ProcessAssets-focused.
- Preserve the PostgreSQL advisory lock exclusively for `ProcessAssets` cross-process exclusion and document that the Web coordinator does not prevent Lookup/cache overlap across multiple Web containers.
- Add deterministic admission, migration, lifecycle, cancellation, shutdown, and race coverage proving no concurrent heavy workers and exact-once release.

## Capabilities

### New Capabilities
- `worker-job-arbitration`: Process-local Web admission, active-job observation, cancellation ownership, shutdown fencing, and authoritative release for exclusive heavy worker jobs.

### Modified Capabilities
- `processing/scheduled-child-worker-execution`: Supersedes block 35's admission-first ordering so scheduled detection runs before JobId creation, `MarkPending()`, adapter arming, and coordinator admission; detector-positive work then attempts admission, while manual processing still bypasses detection.
- `processing/empty-scheduled-worker-gating`: Supersedes block 36's admitted-empty fixture so detector no-work proves zero identity, pending state, admission, backend, worker, or heavy-graph activity.
- `architecture/web-processing-geodata-boundary`: Supersedes block 39's accepted-empty wording while preserving its lightweight detector-only Web path and child-only detector-positive/manual delegation boundary.

## Impact

The change affects the Web worker admission/launch abstraction introduced by block 49, `ProcessingBackgroundService`, the scheduled ordering capabilities introduced by blocks 35/36/39, worker descriptor metadata from blocks 47–48, coordinator/diagnostic DI composition, and lifecycle/race tests. It deletes the temporary Lookup gate but does not change `Lookup.razor`, worker protocol DTOs, resolver behavior, or block 51's concrete cache-mutation contract.
