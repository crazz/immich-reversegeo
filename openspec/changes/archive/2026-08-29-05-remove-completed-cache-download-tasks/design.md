## Context

See [proposal.md](proposal.md) for motivation and [specs/geodata/cache-download-retry-cleanup/spec.md](specs/geodata/cache-download-retry-cleanup/spec.md) for required behavior. Both active services are singleton-registered and independently own `ConcurrentDictionary<string, Lazy<Task>>` maps and positive ready-cache sets. The resolver calls `GetOrStartDownload` directly for both sources; `EnsureDataAsync` is not the universal ownership boundary.

Each `GetOrStartDownload` first calls `HasData` before map insertion. That outer check cannot strand an entry because none exists yet. The winning lazy then runs a source-specific internal operation that repeats `HasData`; country mapping and directory/path setup also precede the current protected region. Overture's existing catch/finally protects export `*.tmp` cleanup. GADM's protects its database `*.tmp` and package `*.gpkg.download` cleanup. Exceptions before those regions can leave the evaluated lazy in the map. Current key-only removal in both the internal finally and `EnsureDataAsync` does not prove that the removed value is still the completing value.

Block 4 is a planning prerequisite, not evidence that its source has landed. At apply time, verify its required helper, registration, and focused tests exist and pass; if absent, stop and apply block 4 first rather than recreating or assuming its contract here. This change then composes with its Overture internal test access and exporter-operation seam without replacing it. GADM remains a distinct implementation with different download artifacts and mapping steps.

## Goals / Non-Goals

**Goals:**
- Give each source map sole ownership of a winning lazy from insertion through all inner task work and terminal cleanup.
- Make cleanup race-safe for success, fault, and cancellation while preserving one active task per source/country.
- Make lifecycle ordering deterministic in unit tests without network, native database timing, sleeps, or reflection.
- Preserve direct resolver and `EnsureDataAsync` call compatibility.

**Non-Goals:**
- Change the first winning caller's token ownership or make a later waiter's cancellation cancel shared work.
- Normalize exceptions or broaden cancellation propagation; block 6 owns those changes.
- Change source precedence, ready-cache validity, download endpoints, export schemas, publication order, or cross-process coordination.
- Merge the Overture and GADM implementations behind a new shared abstraction.

## Decisions

### 1. The winning lazy owns full-operation cleanup

Create each candidate lazy so its delegate awaits a lifecycle wrapper around the complete existing source operation, not merely the current transfer/export region. The wrapper uses `try/finally` from its first instruction and therefore encloses the repeated inner `HasData`, source mapping, directory/path setup, and the existing source-specific operation. The outer `HasData` remains before insertion for the existing ready fast path; if it faults, no map entry exists and no removal is needed.

The wrapper removes its own entry after success, fault, or cancellation. `EnsureDataAsync` only awaits the returned task with its caller token; it no longer conditionally owns map cleanup. This gives direct resolver callers and wrapper callers the same lifecycle.

Alternative: widen only each current internal try/finally. Rejected because lifecycle ownership would remain duplicated in `EnsureDataAsync`, and future pre-task setup could again escape cleanup. Alternative: attach fire-and-forget continuations. Rejected because continuation scheduling complicates the guarantee that terminal observation and removal have a deterministic order.

### 2. Removal is an atomic exact key/value compare-remove

The lifecycle wrapper captures the exact winning lazy and removes the `KeyValuePair<string, Lazy<Task>>` atomically through the dictionary's compare-remove operation (for example, its `ICollection<KeyValuePair<TKey,TValue>>.Remove` contract if that is the target framework's supported form). It SHALL NOT perform `TryGetValue` followed by key-only `TryRemove`, because replacement can occur between those calls.

The candidate lazy is created before `GetOrAdd`; its delegate closes over that candidate. Only the returned winning lazy is evaluated. Starter/awaiter status is determined by reference identity between the candidate and winner, rather than by whether a `GetOrAdd` value factory happened to execute. This also removes the current possibility that a losing factory reports `StartedDownload`.

Alternative: unconditional `TryRemove(iso3, out _)`. Rejected because cleanup from an old task or waiter can remove newer work. Alternative: a non-atomic identity check followed by removal. Rejected for the same race.

### 3. Cancellation semantics stay split between task ownership and waiting

The winning lazy continues to capture the token supplied by the caller whose candidate wins insertion. If that token cancels the underlying source operation, the shared task becomes cancelled and exact-value cleanup makes the country retryable. A later caller's token is applied only by its existing `WaitAsync`; cancelling that wait does not remove or cancel an active task, and a later caller must still join it.

This block records and tests those current semantics but does not claim immediate interruption of synchronous DuckDB or SQLite work. Block 6 remains responsible for cancellation propagation through active catch boundaries.

### 4. Use source-operation delegates and signal gates as the deterministic seam

Add the narrowest internal constructor/operation seam in each service around the complete source operation that the lifecycle wrapper awaits. Production defaults to the existing internal implementation and both public constructors/DI behavior remain unchanged. Reuse block 4's Overture friend-assembly/test infrastructure and exporter seam; the new outer lifecycle seam must not bypass the production export operation by default. Add equivalent internal test access to the GADM test project only as needed.

A controlled operation records invocation count and token, signals entry, then awaits `TaskCompletionSource` gates configured to complete, fault, or cancel. Tests can therefore hold one task active, start simultaneous callers, cancel only a waiter, and release terminal outcomes without sleeps. A separate internal exact-remove helper used by production can be tested with an old and replacement value in a standalone dictionary, deterministically proving stale cleanup preserves the replacement without exposing the service's private map or adding timing hooks.

Ready behavior uses handcrafted valid SQLite fixtures and asserts zero operation calls. For success cleanup, the controlled operation must, before returning success, publish a minimal source-valid cache at the final country path using the same handcrafted-cache builder as readiness/status tests, including every schema and metadata element required by `HasData` and status/readability checks. The test asserts readiness, removes that cache through the existing service deletion path, and proves a later request starts a new task and second source invocation. Merely completing the delegate or mutating ready state is not valid publication. Fault and owner-cancellation cleanup are observed by repairing/reconfiguring the controlled operation and proving a new task identity and second invocation for the same ISO3.

Alternative: force filesystem or live-source failures. Rejected because repaired success would still require network/native work and path-lock behavior is platform-sensitive. Alternative: expose the in-flight dictionary. Rejected because tests should assert lifecycle behavior and the production compare-remove helper rather than private mutable state.

### 5. Preserve each source's inner artifact boundary

Overture retains block 4's non-pooled GUID `*.tmp` output, validation, and atomic move behavior. GADM separately retains package download, GeoPackage export, database `*.tmp`, `*.gpkg.download`, validation, and move cleanup. The lifecycle wrapper adds map cleanup outside these source-specific regions; it does not replace their artifact catches/finally blocks.

## Risks / Trade-offs

- [Self-referential candidate construction is implemented incorrectly] → Create the candidate before insertion, evaluate only the returned winner, and cover starter/awaiter identity with gated concurrent tests.
- [A nominal compare-remove is implemented as check-then-remove] → Require and directly test one atomic key/value removal primitive against a replacement value.
- [The lifecycle seam bypasses real production code] → Default it to the existing full source operation, keep it internal, and retain focused artifact/publication tests through the existing source-specific seams.
- [Waiter cancellation is mistaken for shared-task cancellation] → Use separate tokens and gates and assert operation count/task identity after only one wait is cancelled.
- [Success cleanup is hidden by the positive ready cache] → Delete the completed fixture through the existing service path before asserting a new operation can start.
- [Block 5 duplicates block 4 coverage] → Reuse its Overture test infrastructure but keep this block focused on task-map ownership; do not repeat pooling or post-open resource assertions.

## Migration Plan

1. Verify whether block 4 is applied in source; if not, apply it first, then retain its Overture helper/seam unchanged.
2. Add gated source-operation fixtures and failing lifecycle tests for both sources, including the atomic stale-value removal test.
3. Wrap each full source operation in exact-value terminal cleanup and derive starter status from winning-lazy identity.
4. Remove caller-side map removal from both `EnsureDataAsync` methods while preserving their wait-token behavior.
5. Run focused source tests, then the default suite. No persisted cache migration or deployment step is required.
6. Roll back by reverting the lifecycle wrappers/seams and caller-cleanup change; existing cache files require no conversion.
