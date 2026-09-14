## Context

See [proposal.md](proposal.md) for motivation and [specs/resolver-progress-event-reporting/spec.md](specs/resolver-progress-event-reporting/spec.md) for the behavior contract. Blocks 6–9 are planning prerequisites, not evidence that their source has landed. At apply time, verify the required source APIs, registrations, and focused tests exist and pass; if any prerequisite is absent, stop and apply it first rather than recreating or assuming its contract here. This change then relies on block 6's active-token cancellation and critical-memory rules, block 8's asynchronous run session, typed logs, opaque activity identities, cleanup, no-op, serialization, and broken-session rules, and block 9's one-session singleton Web projection.

Block 9 intentionally leaves one direct state path. The singleton `AdministrativeAreaResolverService` accepts optional `IAdministrativeAreaResolutionProgress`; parallel asset calls receive a nested `ProcessingResolutionProgress` that calls `ProcessingState.BeginActivity` and `AppendLog`. The resolver itself observes Overture/GADM `GetOrStartDownload` results. `StartedDownload` and `AwaitedExistingDownload` produce different source-specific activity labels, while `AlreadyReady` produces no activity. Overture failures propagate; GADM ordinary candidate failure reports unavailability and permits fallback. Earlier block-6 application must make active cancellation and OOM escape those tolerant boundaries.

The current Lookup page does not call the aggregate resolver. It directly runs country/cache/query operations and owns page-local status. That duplication is outside this change, but it proves that geodata/cache work can overlap processing without owning a processing request. The resolver and cache services are singletons, so no mutable run/session correlation may be stored on them.

## Goals / Non-Goals

**Goals:**
- Remove the final resolver/cache-to-`ProcessingState` production bridge by using block 9's already-open run session.
- Preserve exact source-specific diagnostic text, Information presentation, activity labels, activity lifetime, cache ownership, source outcomes, and resolver results.
- Make reporting explicit, awaited, invocation-scoped, optional, concurrency-safe, and testable under success, failure, cancellation, and reporter faults.
- Keep non-processing and future Lookup reuse independent from processing admission and state.

**Non-Goals:**
- Do not redo block 9's request creation, arming, session opening, lifecycle, progress/accounting, terminal projection, adapter registration, or main-pass log routing.
- Do not inject a reporter into cache services, change their in-flight maps or first-owner token semantics, add download outcome fields to activity events, or redesign cache APIs.
- Do not unify Lookup with the aggregate resolver, change Lookup UI/status behavior, or advance blocks 47–51 worker-job/cache routing.
- Do not change administrative source order, ranking, territory fallbacks, result models, airport behavior, persistence, configuration, or worker protocol.

## Decisions

### Pass the existing run session explicitly per resolver invocation

Expose a no-report resolver overload that takes coordinates, configuration, and cancellation only, plus a processing overload that additionally requires the finalized block-8 `IProcessingRunEventSession` (or its exact finalized run-session reporting surface). `ProcessAssetAsync` receives the session already opened by block 9 and passes that same object to the processing overload. The resolver never injects `IProcessingEventReporter`, opens a session, consults the state adapter, or caches a session in a field.

This overload shape makes absence deliberate rather than resolving a global optional service, retains a simple reusable call for non-processing code, and avoids nullable positional ambiguity around `CancellationToken`. Calls without reporting are silent no-ops at the reporting boundary. A valid session backed by block 8's no-op reporter follows the same awaited call path but has no receiver side effects.

**Alternative considered:** inject the singleton reporter/state adapter into the singleton resolver. Rejected because the reporter opens runs rather than identifying the caller's run, and a global adapter could couple Lookup or a stale concurrent invocation to whichever request is armed. **Alternative considered:** retain `IAdministrativeAreaResolutionProgress` and adapt it to events. Rejected because it preserves a second vocabulary and lets future executor code reintroduce a Web-specific bridge. The old interface is an unsupported internal seam; no HTTP or packaged public contract changes.

### Reuse session logs and activities without extending block 8

Map every current `Report`/`ReportCacheEvent` call to an awaited `Information` log on the supplied session, preserving message text exactly. Do not promote the existing GADM unavailable text to Warning: its direct-state presentation is currently plain, while the separate `ILogger` warning remains outside the UI event stream under block 8's diagnostic boundary.

For each non-blank cache label, await the session's activity-begin operation and hold its returned asynchronous scope. The session allocates the opaque identity and pairs the end; the resolver does not generate IDs, track dictionaries, or end by display label. `StartedDownload` keeps `Downloading Overture/GADM administrative cache for {ISO3}...`; `AwaitedExistingDownload` keeps `Waiting for Overture/GADM administrative cache for {ISO3}...`; `AlreadyReady` creates no scope. Distinct invocations therefore remain independent even when labels match.

**Alternative considered:** derive identity from source/country/label. Rejected because concurrent callers can legitimately share all three while retaining distinct local waits. **Alternative considered:** add success/failure/cancelled to `ActivityEnded`. Rejected because block 8's vocabulary is immutable and an activity end only establishes lifetime; readiness/unavailability diagnostics and propagated exceptions carry outcome semantics.

### Separate cache-operation failures from reporter failures

All reporter/session operations are awaited. Do not place reporter calls inside a tolerant catch that could reinterpret their failure as GADM cache unavailability. Structure each candidate operation so source acquisition/wait/query exceptions are classified by the existing block-6/source rules, while activity disposal remains guaranteed once start was accepted. If disposal or any other session operation fails, propagate the reporter infrastructure fault, make no direct `ProcessingState` repair, and do not attempt another diagnostic through the broken session.

A successful wait disposes its activity before readiness is emitted, retaining current visible ordering. For GADM, an ordinary candidate failure disposes the activity, keeps the existing `ILogger` warning and plain unavailability event, then continues fallback. Active caller cancellation and `OutOfMemoryException` escape without readiness/unavailability normalization; a cancellation-like shared-task failure with an unrequested caller token follows the existing ordinary source path. Overture continues to propagate source failures and emits no newly invented unavailable message. Cleanup uses the session's non-cancelled end path and does not cancel or evict shared work.

If both a source operation and activity-end reporting fail, the reporting failure is the actionable infrastructure failure for the event path; retain the source exception for local logging/diagnostic association where the finalized APIs permit, but do not send exception objects or recurse through the session.

**Alternative considered:** catch all resolver exceptions and report one unavailable event. Rejected because it changes Overture behavior, hides active cancellation/OOM, and can swallow a broken reporter. **Alternative considered:** fire-and-forget activity disposal to preserve the source exception. Rejected because it violates block 8 ordering/finality and can leave the projected activity visible.

### Keep cache services and singleton lifetimes unchanged

The resolver remains singleton and stateless with respect to reporting. Cache services remain unaware of processing events; the resolver reports only the local result returned by `GetOrStartDownload` and the lifetime of its own `WaitAsync`. Shared in-flight task ownership, first-owner cancellation, exact-value cleanup, publication, and cache readiness remain owned by the source services.

After verifying the applied baseline, Program registrations retain block 9's identities: one concrete singleton state adapter factory-aliased as `IProcessingEventReporter` to that exact instance, one concrete `ProcessingBackgroundService` singleton factory-aliased as `IHostedService` to that exact instance, and the existing singleton resolver/cache services. Block 10 adds no registration and does not resolve the reporter from an ad hoc scope. Removing nested `ProcessingResolutionProgress` eliminates resolver/cache direct state access but does not remove `ProcessingState` from the background service, because startup/schedule/contention/`MarkPending` remain block-9 control-plane responsibilities.

**Alternative considered:** make the resolver scoped to carry a session. Rejected because it changes current composition, does not align with parallel per-asset invocation lifetime, and risks mutable correlation leakage.

### Limit production routing to the block-10 seam

Thread the block-9 session only through the existing `RunOnceAsync`/`ProcessAssetAsync` call chain far enough to invoke the resolver overload, then delete `ProcessingResolutionProgress`. Do not reopen a session per asset, emit duplicate main-pass logs/dispositions, re-arm the adapter, or change error classification at the processing boundary. A reporter fault must continue through block 9's infrastructure-failure path rather than become a handled per-asset failure; implementation must verify the block-9 catch structure still guarantees this when the resolver begins awaiting reporter operations.

Lookup remains untouched. If it later reuses the aggregate resolver, it must call the no-report overload (or receive a future Lookup-specific observer), never the currently armed processing session. Current direct Lookup cache helpers continue using `UpdateLookupStatusAsync` and do not emit processing events even when a processing run overlaps.

**Alternative considered:** refactor Lookup and processing onto one observer abstraction now. Rejected because that changes user-visible interactive behavior and belongs to later worker/cache-routing blocks.

### Verify concurrency and failure with controlled gates

Use block 8's recording/fault-injection support plus the narrow deterministic source seams established by earlier cache/cancellation blocks. Tests use fixed requests/IDs where exposed and `TaskCompletionSource` gates with asynchronous continuations; they do not use sleeps, live downloads, environmental cancellation races, global event ordering across unrelated assets, or wall-clock string equality.

Contract tests assert per-session subsequences and identity pairing: source-specific start/status, activity start, matching end, then readiness/query where applicable. Concurrent tests hold two equal-label or source-distinct waits open, release them out of order, and prove one end cannot clear the survivor. Failure matrices cover StartedDownload, AwaitedExistingDownload, AlreadyReady, ordinary GADM fallback, propagating Overture failure, active cancellation, foreign cancellation-like failure, OOM, begin/log/end reporter faults, null/no-report invocation, explicit no-op session, and concurrent calls assigned to different/no sessions. Routing/DI tests prove one admitted session is reused, no resolver/cache state access remains, and block-9 identity/lifetimes are unchanged. A focused Lookup test or boundary assertion proves overlapping Lookup status never enters the processing recorder.

## Risks / Trade-offs

- [The checkout still shows pre-block-8/9 source even though their plans are complete] → Treat blocks 1–9 as immutable sequencing prerequisites; at apply time stop if finalized block-8/9 APIs and routing are absent rather than recreating them in block 10.
- [A broad GADM catch can swallow reporter, active-cancellation, or OOM failures] → Narrow source classification around cache operations and keep awaited reporter calls outside tolerant normalization.
- [Non-cancelled activity cleanup can delay cancellation under reporter backpressure] → Preserve block 8's bounded awaited cleanup and correctness; do not fire-and-forget or hold cache/state locks while awaiting it.
- [Removing a public C# progress interface is source-breaking for unsupported direct consumers] → Preserve the no-report resolver call shape, document the internal seam replacement, and make no HTTP/config/data compatibility claim; do not retain a second production reporting vocabulary.
- [Lookup currently duplicates resolver/cache orchestration] → Leave it untouched and explicitly test session isolation; consolidation is later scope.
- [Reporter fault during source failure can obscure the source exception] → Propagate the infrastructure fault without recursion and retain source context only in local logging where possible.

## Migration Plan

1. Verify whether blocks 6–9 are applied in source and their finalized cancellation, session, adapter, and production-routing tests pass; if not, apply them first and stop this change.
2. Add the no-report and run-session resolver overloads plus awaited log/activity helpers, preserving exact messages and source exception taxonomy.
3. Thread the already-open block-9 session through only the resolver call and remove `ProcessingResolutionProgress`.
4. Add deterministic resolver event, activity, failure/cancellation, no-report/no-op, concurrency, routing, DI, and Lookup-isolation tests.
5. Run focused Web tests, the repository default-exclusion suite, strict validation, and a diff review restricted to block 10.
6. Roll back by restoring the resolver-only progress seam and nested bridge; no data, cache, configuration, or external migration is required.
