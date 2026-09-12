## Context

See [proposal.md](proposal.md) and [specs/processing-work-detection/spec.md](specs/processing-work-detection/spec.md). The landed `IScheduledRunWorkGate.HasWorkAsync(CancellationToken)` is count-backed and closes no heavy graph. Block 50 superseded block 35's admission-first ordering: detection occurs before identity, shared admission, pending publication, and adapter arming. No-work, cancellation, and failure close at the scheduled preflight boundary without a processing lifecycle. A positive result proceeds to the existing shared admission and child route. Standard schedules through that gate, manual processing bypasses it, Web-only has no scheduler activity, and public Run-once executes directly under the worker-side advisory lock and authoritative count.

Applied blocks 50, 54, 55, and 56 were verified at archive commit `5b3535e39cba452d37206bfe872862a3bc3e2307`. Bind to `ProcessingRunCoordinator`, its existing preflight cancellation/drain path, `RepositoryScheduledRunWorkCounter`, and finalized role composition. Maintain only exact dependency-policy entries/references required by the replacement contract; preserve all block-56 enforcement and archived artifacts.

The current PostgreSQL eligibility operation is `ImmichDbRepository.GetUnprocessedCountAsync(CancellationToken)`: `asset` joins `asset_exif` on quoted `assetId`, with null city/country, present latitude/longitude, and null `deletedAt`. The same exact count currently serves Dashboard and processing. After the worker migration, the child executor's count remains authoritative; the Web detector is only a pre-launch observation.

## Goals / Non-Goals

**Goals:**

- Replace/alias block 35's temporary bare boolean gate with one dependency-light control-plane interface, immutable request/snapshot, and immutable result.
- Preserve block 50's pre-admission call order, scheduler-local outcomes, cancellation/failure behavior, and count-backed production behavior while keeping the worker count authoritative.
- Make successful diagnostics safe and bounded without turning the result into a count, work set, query-plan, or cursor transport.
- Keep the current implementation stateless and singleton-safe and make deterministic fakes easy to inject.
- Leave a deliberate source-compatible path for change 58 to replace the count-backed adapter with the finalized full-eligibility existence implementation without SQL details crossing into scheduling; do not reserve speculative incremental or reconciliation behavior.

**Non-Goals:**

- No eligibility predicate, Dashboard statistics, worker executor/count, worker protocol/request, advisory lock, processing state, scheduling cadence, cron/configuration, deployment mode, or public UI change.
- No existence query, index recommendation, detector telemetry, query-plan documentation, watermark source, cursor persistence, reconciliation cadence, or NAS mode implementation. Blocks 58–60 own the existence/observation/documentation follow-ups; finalized blocks 61–64 reject the latter three feature paths.
- No Immich/schema mutation, skipped-store read or write, batch/resolver/geodata/cache work, stable work-set transaction, reservation, fallback, retry, replay, replacement worker, or catch-up.
- No edit to archived block-56 artifacts or weakening/duplication of its policy; only required exact contract and factory-manifest maintenance.

## Decisions

### 1. Add one immutable request/result contract in the lightweight control plane

Introduce the final seam as `IProcessingWorkDetector.DetectAsync(ProcessingWorkDetectionRequest, CancellationToken)` returning `ProcessingWorkDetectionResult`. Use the exact landed namespace/contracts assembly that Standard Web already shares with scheduling; do not place the contract in Overture, GADM, worker execution, or a package with native/geodata dependencies. It is an internal application contract, not a public HTTP, CLI, configuration, or worker-protocol surface.

The request contains:

- the existing scheduled trigger value, with no RunId or JobId because no processing request has been admitted; and
- an immutable `ProcessingWorkDetectionSnapshot` containing only bounded logical values for `Purpose` and `Coverage`.

Block 57 supports only the scheduled trigger, scheduled-launch purpose, and current full-eligibility coverage and rejects unsupported enum values exhaustively. It carries no run/job identity, AppConfig object, cron text, SQL, table/column names, connection data, count, asset ID, work set, processing settings, or cursor. The separate snapshot makes the later scheduler policy choice explicit without making the detector read mutable settings or teaching scheduling how a strategy queries storage.

The result contains:

- `bool HasWork`, the only field the coordinator may use for launch gating; and
- one immutable diagnostics value with bounded enums for implementation kind and logical coverage plus `UsedFallback`.

For block 57 the diagnostic kind is count-backed/full-eligibility and `UsedFallback` is false. It contains no exact/estimated count, duration, SQL/query plan, schema name, parameter, credential, exception text, row/cursor identity, or work set. Block 59 may time and log an invocation around the seam; elapsed time does not belong in the result. Alternatives: retain `Task<bool>`, rejected because it cannot carry safe low-cardinality strategy evidence; return the exact count, rejected because it couples callers to the temporary adapter and invites the Web value to become authoritative; use a string metadata bag, rejected because it permits unbounded or secret-bearing data.

### 2. Keep cancellation and failures on the exceptional path

The method receives the exact existing preflight token linked to scheduler/host cancellation. A matching `OperationCanceledException` propagates as cancellation. Any repository/adapter fault propagates as failure. Neither produces a successful result with `HasWork = false`, and the result has no `Error` union that could be accidentally treated as no work. The existing block-50 preflight path retains cancellation/drain cleanup and bounded logger-only failure/no-work presentation without identity, admission, pending, adapter, or worker artifacts. Once positive work wins admission, existing identity-checked cleanup and child finality remain authoritative.

The adapter does not create a timeout in block 57. If block 59 or later composition adds a bounded timeout, the owning layer must distinguish its own timeout token from host/user cancellation without changing no-work semantics. Raw exceptions may reach controlled structured application logging according to existing policy, but result metadata and user-facing state remain secret-free. Alternative: catch every exception and return false, rejected because database outages would silently suppress scheduled work.

### 3. Preserve behavior with one stateless count-backed adapter

The initial production adapter depends only on the lightweight repository/count boundary and calls the existing exact eligibility operation once per detector invocation, passing the exact cancellation token. It returns `HasWork = count > 0` and the constant safe diagnostics described above. It does not cache the last result, retain a connection/command, keep mutable counters, or publish processing state. It reads no skipped IDs or processing configuration and touches no batch, resolver, airport, Overture, GADM, cache, protocol, launcher, or backend service.

The interface makes no exact-count performance promise: count-backed is a migration adapter, not part of the caller contract. Dashboard retains `GetUnprocessedCountAsync` for statistics, and the child executor independently repeats its authoritative exact count after worker startup and advisory-lock acquisition. The eligible scheduled path therefore still performs two exact queries until block 58. Alternative: share the Web count with the worker or put it on the worker request, rejected because it would be stale, break protocol boundaries, and weaken worker authority.

### 4. Replace the temporary gate at its existing coordinator call site

Migrate the landed scheduled preflight call site in place: begin tracked preflight → one detector call with the immutable scheduled request/snapshot and exact preflight token → finish preflight → unchanged logger-only closure for no-work/failure, or positive work → existing shared admission attempt → active handle/CTS publication and frozen plan → immediate `MarkPending()` → matching adapter arm → lazy backend dispatch. A positive result may lose admission to processing, cache maintenance, or database maintenance; it must then create no pending state or child. Preserve preflight shutdown fencing/draining and all admitted cleanup. Do not move detection after admission, into cron calculation, into the child backend, or into processing execution.

Replace the temporary interface and registration outright once every call site/test fake is migrated. If a short compatibility alias is needed during implementation, remove it before completing block 57. Never stack a new detector around the old gate in a way that can issue two queries or create two state owners. Existing preflight no-work/cancel/failure handling and the positive-child route remain unchanged except for consuming `result.HasWork`.

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
- a `TaskCompletionSource`-gated invocation that captures the exact pre-admission request/snapshot/token and proves call ordering without sleeps;
- matching-token cancellation;
- a configured non-cancellation exception; and
- call/constructor counters plus fail-on-use sentinels for bypass and no-heavy-resolution assertions.

Fakes must not implement SQL parsing, return numeric counts, mutate `ProcessingState`, or share unsynchronized queues across parallel tests. Reuse the same fake contract across Standard scheduled positive/negative/cancel/failure tests, Web-only/manual/Run-once bypass tests, and the later existence adapter. Repository adapter tests separately verify count-to-boolean mapping and predicate parity; coordinator tests should not mock Npgsql.

## Risks / Trade-offs

- [Historical admission-first planning conflicts with applied block 50] → Preserve the verified pre-admission contract and its zero-identity/state tests; use the existing coordinator/preflight and mode roots rather than creating parallel paths.
- [Request/snapshot types become a dumping ground] → Keep only the scheduled trigger and closed bounded snapshot enums; reject run/job identity, AppConfig, SQL, cursor, work-set, and arbitrary metadata fields in review/tests.
- [Safe diagnostics become behavior inputs] → Expose `HasWork` as the sole launch decision and test that metadata variation cannot change dispatch/finalization.
- [Temporary alias causes duplicate queries] → Resolve aliases to one singleton, instrument invocation count, migrate the one call site, and remove the old route within block 57.
- [Count and existence implementations drift semantically] → Verify both against the same explicit predicate cases; retain the worker's exact count as authority.
- [Singleton accidentally retains request state] → Require immutable locals only and concurrency tests with independently gated invocations.
- [Speculative incremental state reappears despite the finalized no-go] → Keep block 57 non-persistent and full-eligibility-only; any future alternative requires new evidence and explicit revision of the block 61–64 planning decisions before implementation.
- [Detection races are mistaken for bugs or consistency] → Test both directions and document that the observation is advisory, non-atomic, and never a work reservation.

## Migration Plan

1. Bind to applied blocks 50/54/55/56; record the landed temporary gate, pre-admission coordinator order, role composition, repository boundary, and tests. Preserve archived block 56 and maintain only exact policy entries/references required by this replacement.
2. Add the dependency-light immutable request/snapshot/result/diagnostic types and final detector interface, with exhaustive current purpose/coverage validation.
3. Add the stateless count-backed singleton adapter over the landed exact-count repository operation and safe constant metadata.
4. Replace the temporary gate at the existing scheduled pre-admission call site without changing preflight cancellation/drain, no-work/fault closure, positive admission, `MarkPending`, state arming, backend laziness, child dispatch, or cleanup.
5. Register one detector identity only in the appropriate Standard scheduling composition; preserve Web-only, manual, Run-once, private-worker, and startup bypass boundaries.
6. Add contract fakes and focused parity, cancellation/failure, race, DI/lifetime, no-side-effect, and mode/trigger tests. Run focused tests and the normal default-exclusion suite.
7. Run strict OpenSpec validation/status and review a block-57-only diff. Rollback restores the temporary gate registration/call site; there is no schema, settings, protocol, or persisted-state migration.
