## Context

See [proposal.md](proposal.md) and [specs/processing-work-detection/spec.md](specs/processing-work-detection/spec.md). Block 35 deliberately introduces a temporary `IScheduledRunWorkGate.HasWorkAsync(CancellationToken)` after coordinator admission, pending publication, and exact-request state arming but before backend resolution. Its count-backed adapter closes no heavy graph; its local no-work/cancel/failure and positive-child outcomes are already owned by the coordinator. Blocks 41–43 then make the split explicit: Standard schedules through that gate, manual processing bypasses it, Web-only has no scheduler activity, and public Run-once executes directly under the worker-side advisory lock and authoritative count.

The inspected source is still pre-migration: `ProcessingBackgroundService` combines schedule, admission, exact count, and in-process execution, and none of blocks 11–55 or the block-35 temporary gate exists in active source. This is a planning dependency discrepancy, not permission for block 57 to retrofit the old monolith. Apply must first verify that blocks 35–55 are landed and bind to their exact contract names, composition slices, and tests. Block 56 is parallel-owned and must not be edited.

The current PostgreSQL eligibility operation is `ImmichDbRepository.GetUnprocessedCountAsync(CancellationToken)`: `asset` joins `asset_exif` on quoted `assetId`, with null city/country, present latitude/longitude, and null `deletedAt`. The same exact count currently serves Dashboard and processing. After the worker migration, the child executor's count remains authoritative; the Web detector is only a pre-launch observation.

## Goals / Non-Goals

**Goals:**

- Replace/alias block 35's temporary bare boolean gate with one dependency-light control-plane interface, immutable request/snapshot, and immutable result.
- Preserve block 35's call order, local outcomes, cancellation/failure behavior, and count-backed production behavior while keeping the worker count authoritative.
- Make successful diagnostics safe and bounded without turning the result into a count, work set, query-plan, or cursor transport.
- Keep the current implementation stateless and singleton-safe and make deterministic fakes easy to inject.
- Leave a deliberate source-compatible path for change 58 to replace the count-backed adapter with the finalized full-eligibility existence implementation without SQL details crossing into scheduling; do not reserve speculative incremental or reconciliation behavior.

**Non-Goals:**

- No eligibility predicate, Dashboard statistics, worker executor/count, worker protocol/request, advisory lock, processing state, scheduling cadence, cron/configuration, deployment mode, or public UI change.
- No existence query, index recommendation, detector telemetry, query-plan documentation, watermark source, cursor persistence, reconciliation cadence, or NAS mode implementation. Blocks 58–60 own the existence/observation/documentation follow-ups; finalized blocks 61–64 reject the latter three feature paths.
- No Immich/schema mutation, skipped-store read or write, batch/resolver/geodata/cache work, stable work-set transaction, reservation, fallback, retry, replay, replacement worker, or catch-up.
- No edit to block 56 or expansion of its architecture-test ownership.

## Decisions

### 1. Add one immutable request/result contract in the lightweight control plane

Introduce the final seam as `IProcessingWorkDetector.DetectAsync(ProcessingWorkDetectionRequest, CancellationToken)` returning `ProcessingWorkDetectionResult`. Use the exact landed namespace/contracts assembly that Standard Web already shares with scheduling; do not place the contract in Overture, GADM, worker execution, or a package with native/geodata dependencies. It is an internal application contract, not a public HTTP, CLI, configuration, or worker-protocol surface.

The request contains:

- the existing immutable processing request identity/trigger needed to prove exact admitted-request forwarding; and
- an immutable `ProcessingWorkDetectionSnapshot` containing only bounded logical values for `Purpose` and `Coverage`.

Block 57 supports only scheduled-launch purpose with current full-eligibility coverage and rejects unsupported enum values exhaustively. It carries no AppConfig object, cron text, SQL, table/column names, connection data, count, asset ID, work set, processing settings, or cursor. The separate snapshot makes the later scheduler policy choice explicit without making the detector read mutable settings or teaching scheduling how a strategy queries storage.

The result contains:

- `bool HasWork`, the only field the coordinator may use for launch gating; and
- one immutable diagnostics value with bounded enums for implementation kind and logical coverage plus `UsedFallback`.

For block 57 the diagnostic kind is count-backed/full-eligibility and `UsedFallback` is false. It contains no exact/estimated count, duration, SQL/query plan, schema name, parameter, credential, exception text, row/cursor identity, or work set. Block 59 may time and log an invocation around the seam; elapsed time does not belong in the result. Alternatives: retain `Task<bool>`, rejected because it cannot carry safe low-cardinality strategy evidence; return the exact count, rejected because it couples callers to the temporary adapter and invites the Web value to become authoritative; use a string metadata bag, rejected because it permits unbounded or secret-bearing data.

### 2. Keep cancellation and failures on the exceptional path

The method receives the coordinator-owned token for the exact admitted request. A matching `OperationCanceledException` propagates as cancellation. Any repository/adapter fault propagates as failure. Neither produces a successful result with `HasWork = false`, and the result has no `Error` union that could be accidentally treated as no work. The existing identity-checked block-35 local finalizer remains responsible for cancellation/failure presentation, safe bounded detail, abandonment cleanup, and matching-handle release.

The adapter does not create a timeout in block 57. If block 59 or later composition adds a bounded timeout, the owning layer must distinguish its own timeout token from host/user cancellation without changing no-work semantics. Raw exceptions may reach controlled structured application logging according to existing policy, but result metadata and user-facing state remain secret-free. Alternative: catch every exception and return false, rejected because database outages would silently suppress scheduled work.

### 3. Preserve behavior with one stateless count-backed adapter

The initial production adapter depends only on the lightweight repository/count boundary and calls the existing exact eligibility operation once per detector invocation, passing the exact cancellation token. It returns `HasWork = count > 0` and the constant safe diagnostics described above. It does not cache the last result, retain a connection/command, keep mutable counters, or publish processing state. It reads no skipped IDs or processing configuration and touches no batch, resolver, airport, Overture, GADM, cache, protocol, launcher, or backend service.

The interface makes no exact-count performance promise: count-backed is a migration adapter, not part of the caller contract. Dashboard retains `GetUnprocessedCountAsync` for statistics, and the child executor independently repeats its authoritative exact count after worker startup and advisory-lock acquisition. The eligible scheduled path therefore still performs two exact queries until block 58. Alternative: share the Web count with the worker or put it on the worker request, rejected because it would be stale, break protocol boundaries, and weaken worker authority.

### 4. Replace the temporary gate at its existing coordinator call site

Migrate block 35's admitted scheduled call site in place: active handle/CTS publication → immutable backend/plan snapshot as already landed → immediate `MarkPending()` → exact-request state adapter arm → one detector call with the admitted request/snapshot/token → existing local finalization or lazy backend dispatch. Do not move detection into cron calculation, before admission, into the child backend, or into processing execution.

Prefer replacing the temporary interface and registration outright once every call site/test fake is migrated. If the landed prerequisite requires a short compatibility alias during the same change, the old and new service types must resolve the same singleton instance and the alias must delegate exactly once; remove the old call path before completing block 57. Never stack a new detector around the old gate in a way that can issue two queries or create two state owners. The existing local no-work/cancel/failure finalizer and positive-child route remain unchanged except for consuming `result.HasWork`.

### 5. Preserve advisory race semantics and worker authority

The result describes one completed database observation only. It neither holds the PostgreSQL advisory lock nor reserves rows or shares an atomic snapshot with the child. If Web reports work and the worker later counts zero, one child completes through its ordinary authoritative zero-work lifecycle. If Web reports no work and eligibility appears immediately afterward, the local occurrence stays complete and the asset waits for a later ordinary trigger. Database changes between count, batch queries, and keyset pages retain existing executor semantics; this block does not claim snapshot isolation or repair cursor races.

Diagnostic metadata cannot alter these outcomes. No direction of race authorizes fallback, retry, replacement launch, replay, catch-up, or request reopening. Worker advisory-lock Busy remains a launched worker outcome, not a detector result or local no-work result.

### 6. Keep trigger and deployment-mode use explicit

Only the internal Standard scheduler consumes the detector. Dashboard manual admission bypasses it and launches through the existing child/coordinator path; Dashboard statistics may separately request an exact count. Web-only registers no scheduler/detector activation path and does not invoke detection regardless of saved schedule values. Public Run-once does not register or invoke the Web detector and performs exactly its worker-side advisory lock and authoritative count. Private workers also do not consume the Web detector.

In Standard composition, register the concrete count-backed detector once as a singleton and map `IProcessingWorkDetector` to that exact instance. Preserve any landed concrete/hosted scheduler alias identity; the detector is not a hosted service and must not initialize PostgreSQL, read configuration, or perform work at provider construction/startup. Web-only and Run-once roots should omit the scheduled detector descriptor when their finalized composition contract requires structural exclusion rather than merely leaving it unused.

### 7. Keep the facade stateless; isolate any future state behind strategy collaborators

The block-57 detector and result types are immutable, and the production adapter is stateless and concurrency-safe. A singleton lifetime matches the scheduler and lightweight repository boundary without creating per-run scopes or disposable state. The contract must not expose `Reset`, `Advance`, `SaveCursor`, mutable `LastResult`, or a caller-supplied SQL/query delegate.

Change 58 can replace only the full-eligibility adapter with an existence-backed implementation and change the safe implementation-kind metadata; the request, result, caller, local finalizers, and worker authority remain unchanged. Change 59 observes duration/outcome around the seam, and change 60 documents the finalized query evidence. Finalized change 61 selected no watermark; changes 62–64 are no-go decisions. Therefore this contract retains only current full-eligibility coverage, adds no incremental coverage/cursor collaborator, creates no separate reconciliation identity, and maps no NAS-specific schedule mode.

Alternative: make the scheduler own cursor persistence and pass raw watermark values. Rejected because it leaks schema/query knowledge into control-plane timing and makes safe advancement impossible to encapsulate. Alternative: keep one mutable singleton detector with implicit mode/current cursor. Rejected because concurrent/future callers become order-dependent and tests cannot prove snapshot identity.

### 8. Standardize deterministic fakes at the contract boundary

Test helpers should provide thread-safe fakes/spies for:

- constant work and no-work results with explicit safe metadata;
- a FIFO scripted sequence for repeated scheduled occurrences;
- a `TaskCompletionSource`-gated invocation that captures exact request/snapshot/token and proves call ordering without sleeps;
- matching-token cancellation;
- a configured non-cancellation exception; and
- call/constructor counters plus fail-on-use sentinels for bypass and no-heavy-resolution assertions.

Fakes must not implement SQL parsing, return numeric counts, mutate `ProcessingState`, or share unsynchronized queues across parallel tests. Reuse the same fake contract across Standard scheduled positive/negative/cancel/failure tests, Web-only/manual/Run-once bypass tests, and the later existence adapter. Repository adapter tests separately verify count-to-boolean mapping and predicate parity; coordinator tests should not mock Npgsql.

## Risks / Trade-offs

- [Prerequisite blocks are absent from active source] → Treat applied blocks 35–55 as a hard apply prerequisite; reconcile exact landed symbols and stop rather than modifying the pre-migration monolith or duplicating coordinator/mode contracts.
- [Request/snapshot types become a dumping ground] → Keep closed bounded enums and immutable identity only; reject AppConfig, SQL, cursor, work-set, and arbitrary metadata fields in review/tests.
- [Safe diagnostics become behavior inputs] → Expose `HasWork` as the sole launch decision and test that metadata variation cannot change dispatch/finalization.
- [Temporary alias causes duplicate queries] → Resolve aliases to one singleton, instrument invocation count, migrate the one call site, and remove the old route within block 57.
- [Count and existence implementations drift semantically] → Verify both against the same explicit predicate cases; retain the worker's exact count as authority.
- [Singleton accidentally retains request state] → Require immutable locals only and concurrency tests with independently gated invocations.
- [Speculative incremental state reappears despite the finalized no-go] → Keep block 57 non-persistent and full-eligibility-only; any future alternative requires new evidence and explicit revision of the block 61–64 planning decisions before implementation.
- [Detection races are mistaken for bugs or consistency] → Test both directions and document that the observation is advisory, non-atomic, and never a work reservation.

## Migration Plan

1. Verify blocks 35–55 are actually applied; record the landed temporary gate, coordinator order, Standard/Web-only/Run-once composition, repository boundary, and tests. Do not edit block 56.
2. Add the dependency-light immutable request/snapshot/result/diagnostic types and final detector interface, with exhaustive current purpose/coverage validation.
3. Add the stateless count-backed singleton adapter over the landed exact-count repository operation and safe constant metadata.
4. Replace/alias the temporary gate at the existing scheduled predispatch call site without changing admission, `MarkPending`, state arming, local finalizers, backend laziness, child dispatch, or cleanup.
5. Register one detector identity only in the appropriate Standard scheduling composition; preserve Web-only, manual, Run-once, private-worker, and startup bypass boundaries.
6. Add contract fakes and focused parity, cancellation/failure, race, DI/lifetime, no-side-effect, and mode/trigger tests. Run focused tests and the normal default-exclusion suite.
7. Run strict OpenSpec validation/status and review a block-57-only diff. Rollback restores the temporary gate registration/call site; there is no schema, settings, protocol, or persisted-state migration.
