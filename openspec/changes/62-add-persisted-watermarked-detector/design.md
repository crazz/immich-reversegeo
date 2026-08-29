## Context

See `proposal.md` for the rejection rationale and `specs/persisted-watermarked-work-detection/spec.md` for the no-go contract. Finalized change 61 selected no watermark and requires change 58's exact full-eligibility `EXISTS` detector to remain the frequent-check correctness path. There is therefore no approved source or ordering from which a cursor design can be derived.

## Goals / Non-Goals

**Goals:**
- Make the existing block 62 planning set consistent with change 61's finalized no-go decision.
- Ensure an apply pass is behavior-neutral and limited to verifying the decision boundary.
- Preserve one explicit, measurable route for a future evidence-backed revision.

**Non-Goals:**
- Do not design or implement cursor storage, incremental queries, advancement, corruption fallback, schema objects, triggers, listeners, replication slots, detector wiring, dependency injection, configuration, or implementation tests.
- Do not alter change 58's `EXISTS` predicate or detector behavior.
- Do not treat periodic reconciliation or other risk reduction as proof that a lossy source is safe.

## Decisions

### 1. Reject implementation rather than invent a fallback

**Decision:** Block 62 adds no runtime path. Applying it only verifies the no-go decision and absence of stale implementation assumptions.

**Rationale:** No cursor format, query, state machine, or fallback can repair a source that may omit an eligible transition. A fallback to full `EXISTS` would add unused complexity while the approved baseline already performs that observation directly.

**Alternatives considered:** Persisting a scalar with overlap, resetting corrupt state to a full scan, and relying on reconciliation were rejected by change 61 because they reduce risk without proving zero false negatives.

### 2. Preserve change 58 as the sole approved frequent-check behavior

**Decision:** Do not add a detector implementation, decorator, registration, or alternate query under block 62.

**Rationale:** Runtime scaffolding for a rejected capability could change composition or imply approval even if dormant. The behavior-neutral outcome is to leave the existing exact `EXISTS` path untouched.

### 3. Reopen only by revising the evidence and this change

**Decision:** A future implementation requires new or revised evidence satisfying every change 61 criterion, followed by explicit revision of all block 62 artifacts.

**Rationale:** This prevents an implementation from inheriting obsolete assumptions. Logical decoding or any other future source must bring its own compatibility, bootstrap, recovery, coordination, cost, and operational design rather than being smuggled into this rejected polling plan.

## Risks / Trade-offs

- **Frequent empty checks may retain the cost of full eligibility observation** → Preserve correctness and use change 59/60 evidence to diagnose cost; do not trade known omissions for speed.
- **The feature-shaped change name may imply implementation** → State the rejected/no-go status consistently in MASTERPLAN and all four artifacts.
- **A future safe source may emerge** → Reopen only through the measurable change 61 gate and explicit artifact revision.

## Migration Plan

No migration, deployment, or rollback applies because this change introduces no runtime behavior or persisted data. Verify that an apply pass produces no source, database, configuration, dependency-injection, or runtime-test changes.
