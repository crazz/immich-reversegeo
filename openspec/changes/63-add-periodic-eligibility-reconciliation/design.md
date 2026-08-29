## Context

See `proposal.md` for the withdrawn premise and `specs/periodic-eligibility-reconciliation/spec.md` for the no-go contract. Finalized block 61 found no safe polling watermark, made block 62 no-go, and required block 58's exact full-eligibility `EXISTS` observation to remain on every scheduled check. The accepted scheduling design therefore has one schedule and one full-current-state correctness path, not a frequent tail that needs a slower repair pass.

## Goals / Non-Goals

**Goals:**

- Reconcile block 63 with finalized block 61 and remove stale watermarked-tail assumptions.
- Preserve the single schedule, full-eligibility detector, lock, pending-state lifecycle, and existing reporting semantics.
- Define an objective gate for any future reconsideration.

**Non-Goals:**

- Do not implement a reconciliation timer, cadence, trigger, detector, or worker mode.
- Do not add configuration, settings migration, UI, activity/log classification, persistence, schema objects, or public documentation.
- Do not alter block 58's eligibility predicate or use reconciliation to relax block 61's zero-false-negative requirement.
- Do not revise blocks 62 or 64.

## Decisions

### 1. Withdraw the separate reconciliation cadence

A second daily or weekly cadence is rejected because every scheduled check already performs the same complete current-eligibility observation. Repeating that observation under another timer does not recover anything omitted by a tail cursor; there is no approved tail cursor. It instead adds timer coordination, contention, configuration, and user-explanation costs.

Alternative: retain a dormant reconciliation configuration for future use. Rejected because it creates migration and UI semantics for an architecture that did not pass its prerequisite gate.

### 2. Preserve the existing single path without a reconciliation identity

The current scheduled path, full-eligibility detector, run lock, pending-state lifecycle, and activity/log semantics remain unchanged. A scheduled pass is not renamed or separately classified as reconciliation, and manual behavior remains unchanged. This avoids introducing a second path whose only distinction would be its timer.

Alternative: label occasional existing runs as reconciliation without changing their query. Rejected because the label implies distinct behavior and would still require cadence and reporting rules with no correctness benefit.

### 3. Revisit only after the watermark gate changes

A future source must first satisfy block 61's supported-version, mutation, inverse-commit, restart/replay, schema-drift, multi-container, and bounded-cost criteria. Only then can a new or revised proposal determine whether reconciliation is needed for that actual architecture and what guarantees it provides. Block 63's stale daily-or-weekly design is not carried forward automatically.

## Risks / Trade-offs

- [A future safe incremental design may benefit from reconciliation] → Reopen through a new or revised evidence-backed proposal after the watermark gate passes.
- [A second cadence might appear to reduce practical risk today] → It duplicates the same full-eligibility check and adds complexity; retain the simpler correctness path.
- [Negative requirements can drift from runtime] → Verify configuration, scheduling, detector, UI, and activity surfaces by repository inspection and existing focused tests without adding implementation.

## Migration Plan

No runtime, configuration, data, or UI migration exists. Withdraw the unimplemented plan, validate the four OpenSpec artifacts, and verify the repository has no second schedule, reconciliation configuration, or reconciliation-specific path while block 58's finalized full-eligibility detector contract remains unchanged. Rollback is unnecessary because no runtime change is authorized; future reconsideration requires a new or revised proposal.
