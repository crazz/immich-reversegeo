## Context

See [proposal.md](proposal.md) for motivation and [specs/geodata/cancellation-preservation/spec.md](specs/geodata/cancellation-preservation/spec.md) for required behavior. The processing token is forwarded from the hosted run through per-asset administrative resolution and shared country-cache waits, but active catches currently conflate three signals: cancellation requested by the current caller, cancellation of a shared task owned by a different token, and an unrelated `OperationCanceledException`. Other broad catches normalize `OutOfMemoryException` along with ordinary operational and malformed-data failures.

The active path spans Web orchestration, Overture and GADM lookup, cache, export, release, metadata, and geometry code. Blocks 4–5 are planning prerequisites, not evidence that their source has landed. At apply time, verify the required source APIs, registrations, and focused tests exist and pass; if any prerequisite is absent, stop and apply it first rather than recreating or assuming its contract here. This design then extends, rather than replaces, their non-pooled Overture temporary exports, deterministic source-operation seams, first-owner task semantics, and exact-value in-flight cleanup. Legacy code is excluded.

## Goals / Non-Goals

**Goals:**
- Make caller-token identity explicit at every active catch and return boundary.
- Preserve critical memory failure without imposing an over-broad rule that all exceptions propagate.
- Record exact tolerant malformed-data boundaries for Overture and GADM.
- Add cooperative checkpoints and deterministic controlled-throw tests across Web, Overture, and GADM.

**Non-Goals:**
- Adding typed run outcomes before block 7, extracting the executor, changing source order/ranking, redesigning geometry, or changing cache formats.
- Adding component-owned cancellation to Lookup or GeoBoundaries; those callers currently pass default/non-cancellable tokens, while service behavior applies whenever a cancellable token is supplied.
- Preempting synchronous DuckDB/SQLite calls, filesystem operations, NetTopologySuite parsing/predicates, or an already-started `Task.Run` delegate.
- Requiring every exception to propagate. Existing non-cancellation, non-OOM diagnostics and fallbacks remain intentional unless the malformed-artifact boundary requires failure.

## Decisions

### Classify cancellation by the current caller token

At every broad catch, first test and rethrow when the token governing the current operation is requested. At the processing boundary, rethrow active-token cancellation from per-asset work so the existing run-level cancellation log and cleanup execute. An `OperationCanceledException` with an unrequested run/caller token is not a cancelled run; it follows the dependency's ordinary failure/source-unavailability path.

Alternative: rethrow every `OperationCanceledException`. Rejected because a shared cache owner task can be cancelled by a different token, and labelling a live waiter or run as cancelled would be false.

### Preserve block 5 shared-task semantics

Keep first-owner token ownership, exact-value terminal removal, and non-owner `WaitAsync(waiterToken)`. A waiter cancelling its own token exits without evicting or cancelling shared work. If the owner cancels the shared task while another waiter remains live, normalize that shared-task cancellation at the source boundary as ordinary unavailability for the live waiter, allowing the current territory/source fallback.

Alternative: link every waiter token into shared work. Rejected because one waiter could cancel work still needed by others and would invalidate block 5.

### Add cooperative checkpoints around native regions

Observe tokens on public token-bearing entry, before native regions, between practical managed candidates/rows/layers, after native return, and immediately before success or cache publication. If cancellation is observed after export and before move, use existing cleanup to remove the temporary output and leave the published cache unchanged.

Alternative: wrap synchronous work in `Task.Run(..., token)` and claim cancellation. Rejected because the token can prevent scheduling but cannot stop a delegate or native call already executing.

### Propagate OOM while preserving ordinary diagnostics

Order or filter active broad catches so `OutOfMemoryException` escapes unchanged from lookup diagnostics, release fallbacks, cache probes/status/validation, metadata tolerance, geometry, resolver, and Web cache helpers. Preserve ordinary HTTP/SQLite/I/O/release failures as their existing diagnostics, unavailable results, documented release, or territory fallback. Cleanup should still be attempted without replacing the original critical failure.

Alternative: exempt only geometry catches. Rejected because the same critical failure is normalized at adjacent active boundaries and would still be lost. Alternative: rethrow all exceptions. Rejected because diagnostic fallback is an intended product behavior.

### Limit malformed geometry tolerance to existing candidate boundaries

For Overture, narrow geometry-false conversion to recognized malformed WKB/topology exceptions for cached-division and bundled-infrastructure candidates; bundled-country artifact/index corruption remains fatal. For GADM cached candidates, malformed WKB leaves `GeometryContainsPoint=false` while existing bounding-box fallback and ranking remain unchanged. Malformed GADM source GeoPackage/header/schema/WKB during export remains a cache-build failure with temp cleanup and no replacement of an older cache.

Alternative: treat every malformed geometry as a candidate miss. Rejected because it would hide corrupted source artifacts and would change GADM's existing bbox behavior.

### Use narrow deterministic seams, not environmental faults

Reuse blocks 4–5 exporter/source-operation seams. Add only the minimal internal delegates/helpers required to inject cancellation, unrelated OCE, ordinary operational exception, OOM, malformed candidate bytes, and a post-export/pre-publication gate. Use `TaskCompletionSource` with asynchronous continuations for ordering; do not use live network, sleeps, real OOM, RSS, or native timing. Keep public constructors and DI behavior unchanged.

Alternative: trigger failures via corrupt external services or memory pressure. Rejected as unsafe and nondeterministic.

## Risks / Trade-offs

- [More critical failures become visible instead of diagnostics] → Limit unconditional propagation to active caller cancellation and OOM, and add ordinary-fallback regression cases at each modified category.
- [Additional token checks add loop overhead] → Check at managed boundaries rather than inside indivisible native calls and retain current query/geometry algorithms.
- [Foreign owner-task cancellation is easy to misclassify] → Test owner, non-owner waiter, and live foreign-waiter cases independently using block 5's exact task seam.
- [Malformed exception types vary by parser/topology operation] → Inventory actual recognized data exceptions during implementation and catch only those demonstrated by controlled malformed fixtures; do not use a blanket catch.
- [Cleanup can obscure the original failure] → Preserve the original cancellation/OOM with bare rethrow and best-effort cleanup that cannot replace it.

## Migration Plan

1. Verify whether blocks 4–5 are applied in source; if not, apply them first, then retain their helper/seam and in-flight ownership contracts.
2. Add failing deterministic Web, Overture, and GADM taxonomy/checkpoint tests before changing catches.
3. Correct processing/resolver classification, then Overture boundaries, then GADM boundaries, preserving ordinary diagnostic regression coverage in each step.
4. Add post-native/pre-publication checkpoints and malformed-source/candidate cases.
5. Run focused projects with default exclusions, then the repository default suite. For integration-covered paths, run bounded source-specific live checks; record any failing positive assertion as failing, and classify whether it is caused by this change before using it as a change-06 completion gate. A demonstrated pre-existing query/upstream failure must be tracked for correction without weakening the live assertion. No data migration, configuration change, or rollback procedure beyond reverting this behavior-only commit is required.
