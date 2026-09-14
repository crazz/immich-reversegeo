## Why

Finalized change 61 selected **no watermark** because no reviewed source proves durable, commit-ordered coverage without false negatives. This existing block 62 proposal must therefore record a gated rejection rather than retain an implementation plan that contradicts its prerequisite.

## What Changes

- Mark the persisted watermarked detector as **no-go** under the finalized change 61 evidence.
- Preserve change 58's exact full-eligibility `EXISTS` detector without runtime modification.
- Remove stale assumptions that this change will add cursor persistence, an incremental query, fallback behavior, database infrastructure, detector wiring, dependency injection, or implementation tests.
- Permit reconsideration only after new or revised evidence satisfies every change 61 revisit criterion and this proposal is explicitly revised before implementation.

## Capabilities

### New Capabilities
- `persisted-watermarked-work-detection`: Defines the no-go contract that keeps persisted incremental detection absent unless the change 61 evidence gate is later satisfied.

### Modified Capabilities
- None.

## Impact

Planning artifacts and the MASTERPLAN block 62 decision only. Applying this rejected change introduces no runtime behavior, source changes, cursor files, queries, state, fallback, schema objects, triggers, listeners, replication slots, dependency-injection registrations, or implementation tests. Change 58's `EXISTS` path remains unchanged.
