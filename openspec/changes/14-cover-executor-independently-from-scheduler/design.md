## Context

See `proposal.md` for motivation and `specs/processing-run-executor-testing/spec.md` for the behavior matrix. The current `ProcessingPipelineTests.cs` contains only small `GeoResult`, `ProcessingState`, and `AssetCursor` checks; it does not execute the processing pass. Block 11 plans the executor and deliberately consumes only enough tests to prove extraction equivalence: one zero-count path, one representative mixed paged run, principal cancellation/failure/reporter cases, host delegation, and DI identity. Blocks 7–10 separately own model validation, reporter-session semantics, Web-state projection, and resolver-progress behavior.

Block 14 therefore starts from the applied block 11 fixture and adds a matrix of small direct-executor tests. It does not create a second monolithic pipeline test or move scheduler/coordinator assertions into the executor scope.

## Goals / Non-Goals

**Goals:**
- Make every executor-owned branch observable through one reusable fixture, controlled collaborator history, fake persistence effects, reporter events, and the returned result.
- Characterize causal ordering, snapshot lifetime, pagination, clamped concurrency, persistence boundaries, partial effects, cancellation checkpoints, and failure taxonomy.
- Keep the suite deterministic under concurrent asset processing with fixed time and signal gates.
- Clearly identify inherited block 11 assertions and extend them only when a required block 14 dimension is missing.

**Non-Goals:**
- Do not modify production behavior, eligibility predicates, fallback policy, persistence, transactions, retries, or executor APIs merely to satisfy tests.
- Do not retest Phase 1 admission, pending/active/terminal `ProcessingState` behavior, lock recovery, logs, notifications, or hosted cancellation.
- Do not retest block 7 constructors, the full block 8 reporter state machine, block 9 UI projection, or block 10 resolver/cache internals; use their finalized public contracts as collaborators.
- Do not test block 11 host delegation/DI identity again, or any block 12 scheduler and block 13 coordinator behavior.
- Do not use cron, wall-clock sleeps, a host, Blazor, child processes, PostgreSQL, SQLite, downloaded/bundled geodata, or real cache services.

## Decisions

### Inventory and extend block 11 before adding tests
During apply, first map every block 11 executor test to the block 14 matrix. Keep its zero-count and representative mixed-run tests as extraction sentinels. Move shared setup into the reusable fixture if necessary and strengthen an existing test when that is the smallest way to add a missing assertion. New tests target one boundary or equivalence class each.

Alternative: duplicate block 11’s scenarios in a new class. Rejected because two nearly identical empty/mixed tests would drift and would blur extraction verification versus exhaustive characterization.

### Use one scriptable fixture with narrow typed fakes
Create one direct-executor fixture around the finalized block 11 constructor. It provides:
- a fixed request and advancing fixed UTC time source;
- scripted count and keyset-batch responses with cursor/call history;
- mutable-but-snapshotted configuration and skipped-ID sources;
- scripted administrative and airport results with per-asset call history;
- fake update and skipped-insert effects that can succeed, throw, or gate;
- a controllable batch-delay seam;
- the finalized recording/fault-injection reporter support from block 8/11;
- asynchronous gates created with continuations run asynchronously and a concurrency probe.

Fail-fast defaults reject unexpected collaborator calls. Builders may make scenario setup concise, but assertions use observable histories/results rather than fake implementation details.

Alternative: a mocking framework or real SQLite fixture. Rejected because a small stateful pipeline fake makes cursor, order, snapshots, committed effects, and gates explicit without infrastructure timing or brittle mock choreography.

### Organize tests by executor-owned causal boundaries
Keep focused groups for run setup/snapshots, pagination/delay, parallelism, disposition/fallback, persistence/partial effects, cancellation, pass/critical/reporter failures, and terminal invariants. If `ProcessingPipelineTests.cs` becomes unwieldy, split only the executor tests and their shared fixture into clearly named files in the same test project; leave the existing helper/model tests intact unless a normal test-only move is needed for clarity.

Alternative: one large Cartesian parameterized test. Rejected because failures would be difficult to diagnose and cross-product combinations would conceal which causal contract broke. Data-driven rows are appropriate only for true equivalence classes such as parallelism clamps, fallback candidates, cancellation checkpoints, and pass-level failure sources.

### Assert causal order, not accidental global order
Record monotonically numbered collaborator and reporter acceptances. Assert required per-run and per-asset edges: start before count, eligibility before snapshots/batches, admin before airport, persistence before disposition, all assets in a batch terminal before its delay, activity cleanup before finish, and finish before return. Under parallel processing, compare only events for the same asset or explicit gate boundary; do not require batch-input order across assets.

Alternative: serialize all tests at parallelism one. Rejected because it would leave the production concurrency clamp and cancellation races uncharacterized.

### Model irreversible effects separately from accepted dispositions
The fixture records fake persistence success immediately when the update/insert returns and records accounting only when the reporter accepts the disposition. This permits precise assertions for persistence failure, cancellation after persistence, reporter failure after persistence, and later run failure. There is no synthetic rollback or transaction in the fixture.

Alternative: infer persistence from Updated/Skipped events. Rejected because it cannot detect an incorrect disposition-before-write order or preserve the distinction between a durable effect and a subsequently broken reporter.

### Use boundary tables for cancellation and failures
Cancellation rows cover before/during count, snapshot loading, batch retrieval, resolution, airport lookup, during update/skipped persistence, after successful persistence, after committed no-city Skipped or handled Failed decisions, and between batches/during delay. Each row states whether eligibility exists, which effects/counts survive, and whether another batch may begin. A separate foreign-OCE row keeps active-token cancellation distinct.

Failure rows distinguish pass-level count/snapshot/batch/delay failures, handled per-asset source/airport/update/skipped-insert failures, nested resolver reporter failures, critical `OutOfMemoryException` from non-reporter execution layers, and reporter-origin faults. Reporter rows follow the actual API boundaries: combined open/start failure leaves no session; midstream failure breaks the session with no terminal attempt; finish-acceptance failure means a validated result was attempted but `ExecuteAsync` throws and returns none. Healthy-session pass failures return Failed results; every reporter-origin failure, including OOM, propagates its original infrastructure exception.

Alternative: assert only one cancellation and one generic exception. Rejected because it misses the persistence/accounting boundary and the executor’s intentionally different local versus outer catches.

### Reassert invariants through produced values, not constructor tests
Every healthy scenario uses a common terminal assertion for exact request identity, fixed zero-offset UTC timestamps, `EndedAtUtc >= StartedAtUtc`, `Processed = Updated + Skipped + Failed`, outcome/detail rules, exactly one accepted finish, and equality between returned and reported terminal results. Scenario-specific assertions cover eligibility divergence and partial counts. This consumes block 7 without repeating its invalid-construction matrix.

Alternative: duplicate all model and reporter validation tests in the executor class. Rejected because those failures belong to blocks 7–8 and would make this suite sensitive to contract-internal validation paths.

## Risks / Trade-offs

- [The applied block 11 API or test file layout differs from its plan] → Re-read source and tests first, adapt the fixture to finalized seams, and do not introduce a parallel executor abstraction.
- [Exhaustive rows become slow or brittle] → Keep all collaborators in memory, use gates rather than timeouts as the oracle, share common terminal assertions, and parameterize only equivalent boundaries.
- [Concurrency assertions deadlock when a regression prevents a gate from being reached] → Give the test harness bounded diagnostic cancellation only as a fail-safe; never use elapsed time to establish success or ordering.
- [Reporter contract tests are accidentally duplicated] → Test injected reporter failures only where they intersect executor cleanup, persistence, result return, or no-recursion behavior; leave backpressure/session validation breadth in block 8.
- [Resolver/airport fakes over-specify geodata algorithms] → Script only returned administrative/airport facts and containment classification; assert executor call and selection policy, not geometry implementation.
- [Repository failures have different local taxonomy by operation] → Preserve the finalized block 11 catch boundaries: count/snapshot/batch are pass-level, update/skipped-insert are per-asset unless the exception is active cancellation or critical.

## Migration Plan

1. Apply blocks 7–13 in sequence, then re-read the finalized block 11 executor, tests, fake seams, and any changes made by blocks 12–13 without editing those blocks.
2. Inventory inherited block 11 extraction tests against the block 14 matrix and establish the shared direct-executor fixture.
3. Add focused matrix groups in dependency order: snapshots/paging, concurrency, dispositions/fallback, persistence/partial effects, cancellation/failures, then terminal invariants.
4. Run focused scheduler-free tests and the default repository suite, strict-validate the change, and confirm by scope review that only block 14 test/planning outputs changed during apply.
5. Roll back by reverting only block 14 test additions or test-only fixture refactoring; no runtime or data migration exists.
