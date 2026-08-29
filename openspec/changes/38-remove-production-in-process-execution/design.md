## Context

See `proposal.md` for motivation and `specs/processing/child-worker-only-production/spec.md` for behavior. Block 33 introduced an internal `ProcessingBackendKind`, `TemporaryProcessingBackendSelection`, two keyed scoped `IProcessingRunBackend` adapters, and lazy selected-only resolution. Block 37 changed ordinary composition to `ChildWorker` while retaining explicit code-only `InProcess` as a temporary rebuild/revert seam. Its rollout gate requires packaged startup and all mandatory outcomes to preserve exact finality, cleanup, retrigger, and zero in-process/geodata resolution before this block begins.

The block-11 `ProcessingRunExecutor` remains the authoritative heavy pass and is required by the internal-worker role. Block 19 temporarily placed executor and geodata registrations in a reusable heavy module also invoked by Web because processing, Lookup, and Data overlapped. After this change, Web still needs some heavy resolver/cache services for Lookup/Data until block 55, but Web processing no longer needs or may resolve the executor. The block-35 scheduled gate remains a lightweight Web database operation and block 36 owns its detector-zero regression. Block 39 is a separate composition-boundary test change and is not edited here.

Exact landed names may differ from these planning names. Apply must inventory the completed blocks 11, 13, 19–20, and 25–37 and remove their semantic equivalents rather than preserving parallel or obsolete types.

## Goals / Non-Goals

**Goals:**
- Collapse coordinator dispatch from a two-value temporary strategy to one child-only backend contract.
- Remove all production Web construction and invocation paths to the executor while retaining it in worker composition and focused test fixtures.
- Preserve detector-before-dispatch ordering, one authoritative lifecycle, existing role packaging, and Lookup/Data dependencies.
- Leave no unused selector, enum member, adapter, registration helper, alias, fallback branch, or transition-only test utility in production code.

**Non-Goals:**
- No changes to protocol, launcher, cancellation/classification, advisory lock, event projection, processing results, `ProcessingState`, scheduler semantics, or processing algorithms.
- No removal of resolver, cache, repository, country identity, or other heavy Web services still consumed by Lookup/Data; block 55 owns that broader boundary.
- No block-39 edits, new public backend/deployment setting, configuration migration, second worker artifact, automatic retry, or data migration.
- No project/assembly move solely for architectural appearance; logical composition ownership and dependency guards are sufficient for this deletion step.

## Decisions

### 1. Treat block-37 rollout evidence as an irreversible entry gate

Before deleting the transition seam, require the completed block-37 matrix against the production publish/image: startup prerequisite validation; manual and detector-positive scheduled success/no-work; process-local rejection with no process/exit; advisory Busy as Failed plus exit 3; Completed/Cancelled/Failed terminals; protocol/crash and forced-kill raw evidence; exact-session cleanup and stream drainage; no orphan process, activity, run scope, or coordinator handle; safe retrigger; and no in-process executor/geodata resolution from child selection. Preserve the evidence in the change record or test output used by the team.

If any gate fails, do not partially apply block 38. Repair or revert block 37 while its code-only transition seam still exists. Alternative: remove the fallback first and rely on later testing. Rejected because it discards the last controlled rollout exit before child execution is proven in supported packaging.

### 2. Replace keyed strategy selection with one run-scoped child contract

Delete the temporary enum, selection singleton, internal selection parameter/overload, exhaustive selection switches, invalid-enum validation, both keyed registrations, and the keyed in-process adapter. Retain the landed child adapter behavior behind one internal non-keyed child-processing contract (provisionally `IChildProcessingRunBackend`; reuse a clearer landed equivalent if one exists). The contract and adapter belong to the Web control-plane/child-client composition because they launch and supervise another role; they do not own heavy execution.

The singleton coordinator keeps its scope/factory boundary so child session, bridge, classifier state, and other run-owned objects remain scoped. After manual admission—or after a positive scheduled detector result—it creates one run scope, resolves exactly one unkeyed child backend, invokes it once with the existing request/reporter/token, awaits finality and scope disposal, then releases only the matching handle. Rejected, detector-zero, and pre-dispatch cancellation/failure paths create/resolve no backend scope. The coordinator does not inject the executor, launcher details, both implementations, an enumerable, or a generic selection service.

Alternative: keep `IProcessingRunBackend` with a single production implementation. Rejected because its neutral name and transition-era tests preserve the false possibility of another production execution strategy. Alternative: inject a singleton child adapter directly. Rejected because it can collapse established run ownership or construct child dependencies before detector gating.

### 3. Make executor ownership worker-only without deleting Lookup/Data dependencies

The authoritative executor and its executor-only seam aliases belong to reusable execution code but are registered only by internal-worker composition. Tests may directly construct the executor with narrow fakes or build a worker test host. Production Web composition, its registration helpers, and its coordinator constructor graph contain no executor descriptor, alias, factory, service-locator lookup, or delegate capable of invoking it.

Split the transitional heavy registration helper along actual consumers if necessary: a worker execution registration adds the executor plus its heavy dependencies; a Web Lookup/Data registration retains only the resolver/cache/repository services those pages still consume. Do not duplicate singleton caches, data sources, or mapping delegates, and do not remove a heavy type merely because processing no longer uses it. Record each retained Web-heavy descriptor and its current Lookup/Data consumer so block 55 has an explicit handoff.

The types may remain in the current assembly in this block. Type ownership is enforced by role-specific registration and references: coordinator/child client are Web control-plane; executor is worker execution; test fakes are test-only. Alternative: move the executor to a new project now. Rejected because it broadens scope and risks dependency churn unrelated to deleting the production route.

### 4. Preserve the lightweight scheduled gate and one lifecycle

The block-35 detector remains Web-side, database-only, and advisory. It does not resolve the executor, processing config, skipped store, batches, protocol session, resolver, cache, airport, or geometry services. Normal false follows block 36's local zero finalization. Cancellation/failure before dispatch preserves the established local outcome. A positive result only authorizes one child backend resolution; the worker executor still performs the authoritative exact count.

Child startup/protocol/crash/cancellation/cleanup outcomes continue through the existing bridge, cancellation owner, classifier/finalizer, reporter receipt, scope cleanup, and identity-checked coordinator release. No failure path gains in-process fallback, replacement child, replay, resubmission, or automatic retry.

### 5. Delete the public/configuration concept completely and prevent dead code

There was never a public backend contract, so this change adds no migration parser, obsolete setting, compatibility alias, hidden environment variable, CLI switch, endpoint, or UI control. Remove transition comments and tests that assert explicit in-process selection or invalid enum values; replace them with child-only construction and absence tests. Delete now-unreferenced production interfaces, adapters, overloads, factories, keys, imports, and helpers rather than leaving dormant code.

Add three complementary guards:
1. compile/dependency checks prove coordinator/control-plane constructors cannot reference the executor or an in-process adapter and Web role composition cannot resolve executor contracts;
2. descriptor/reference tests prove worker composition retains exactly one executor identity while Web retains required Lookup/Data descriptors and one unkeyed child backend registration;
3. a repository search guard over production source fails on the removed temporary type names, enum values/keys, selection overloads, keyed backend registration/resolution, and known in-process adapter names, while explicitly allowing executor construction/registration only in worker composition and test fixtures.

Search is a regression aid, not the sole architecture proof. Match landed identifiers and semantic equivalents during apply so renaming does not evade the dependency tests.

## Risks / Trade-offs

- [The reusable heavy helper couples executor registration to Lookup/Data services] → Split registration by consumer without duplicating shared singletons; test role descriptors and reference identity.
- [Removing keyed dispatch accidentally resolves a child before the scheduled gate] → Retain scope creation/resolution after positive detection and reuse block-36 fail-on-resolution counters.
- [A hidden executor path survives through a factory or alias] → Combine constructor/dependency checks, descriptor inspection, and search-based guards.
- [Deleting broad heavy Web registrations breaks Lookup/Data] → Inventory current page consumers and retain every required service until block 55.
- [Child-only failures tempt a fallback reintroduction] → Keep failures visible and rollback only by version revert; never recover per run in-process.
- [Landed prerequisite names differ] → Reconcile semantic ownership against applied source/tests and delete equivalents; do not create duplicate contracts.

## Migration Plan

1. Verify and record the block-37 rollout gate against the supported production publish/image and Phase 4 process fixture; stop if any criterion fails.
2. Inventory exact landed selector, keyed backend, coordinator, executor, composition-root, detector, child-session, and test-fixture types plus every Web consumer of the transitional heavy registration module.
3. Introduce/retain one non-keyed child contract at the coordinator scope boundary; migrate manual and eligible scheduled dispatch without changing lifecycle ordering.
4. Split role-specific registrations so executor/executor-only aliases are worker-only while Lookup/Data-required Web services remain; preserve singleton identities and lazy initialization.
5. Delete temporary selector/enum/selection, keyed adapters/registrations, in-process adapter/fallback, transition overloads, and dead tests/helpers/comments.
6. Run compile/dependency, descriptor, search, focused coordinator/scheduler/composition tests, block-36 regression, default suite, strict Phase 4 process/integration fixtures, and production publish/image verification.
7. Roll out the new version only after all gates pass. After deployment there is no runtime or code-only backend switch. Rollback means reverting this version/change set, rebuilding, and redeploying the prior version; preserve failure evidence and do not add a fallback patch.

## Audit Reconciliation

“Local contention” means only Web/coordinator admission rejection before dispatch; it is not PostgreSQL advisory-lock Busy. Preserve distinct authoritative committed terminals, local admission rejection without a child, canonical advisory Busy as a failed child terminal with no eligibility and four zero counts, and forced raw kill as classification evidence rather than a terminal. No case restores an in-process fallback.

