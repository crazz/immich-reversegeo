## Why

After block 37 has run the child worker as the production default through every rollout gate, retaining the temporary in-process branch leaves an unsupported escape hatch that can defeat process isolation. Block 38 makes child execution the only production Web processing route while preserving worker execution and the still-required Lookup/Data services.

## What Changes

- Require successful block-37 rollout evidence before removal: packaged child startup, mandatory terminal/cancellation/failure paths, cleanup with no orphaned resources, safe retrigger, and no in-process resolution.
- Delete the temporary backend enum, immutable selection object, selection overload/default, keyed production backend registrations, production in-process adapter, and code-only emergency fallback.
- Give the coordinator one non-keyed child-processing backend contract and preserve one child dispatch for every accepted manual request and detector-positive scheduled request, with no fallback, replacement, replay, or retry.
- Keep the scheduled empty-work detector in the Web control plane as a lightweight database gate; detector-zero, contention, and pre-dispatch cancellation/failure resolve no child backend and execute no heavy processing.
- Retain the authoritative processing executor only in internal-worker composition and test-only fixtures/direct construction. Remove every production Web DI alias, factory, constructor path, and transitive registration that can resolve or invoke it.
- Keep heavy Web services still required by Lookup and Data until Phase 7/block 55; split registration ownership where necessary rather than deleting those services early.
- Add compile/dependency, descriptor, search-based, lifecycle, process-fixture, and packaging guards proving the removed route cannot return as dead or reachable production code.
- Add no public configuration or migration. After this change, rollback is only a version/source revert followed by rebuild and redeploy.

## Capabilities

### New Capabilities
- `processing/child-worker-only-production`: guarantees that production Web processing can dispatch only to the child worker while preserving the lightweight scheduled no-work gate and worker-only authoritative execution.

### Modified Capabilities
- None.

## Impact

Affects the Web coordinator dispatch seam, Web/internal-worker registration helpers, temporary backend-selection types, the in-process backend adapter, composition and architecture tests, and existing Phase 4 process/packaging fixtures. It does not change AppConfig, environment/CLI/UI contracts, protocol or result semantics, processing algorithms, persistence, Lookup/Data behavior, or the heavy Web services whose removal belongs to block 55.

## Audit Reconciliation

“Local contention” means only Web/coordinator admission rejection before dispatch; it is not PostgreSQL advisory-lock Busy. Preserve distinct authoritative committed terminals, local admission rejection without a child, canonical advisory Busy as a failed child terminal with no eligibility and four zero counts, and forced raw kill as classification evidence rather than a terminal. No case restores an in-process fallback.

