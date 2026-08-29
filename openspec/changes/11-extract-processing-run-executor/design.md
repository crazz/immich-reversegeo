## Context

See [proposal.md](proposal.md) and [specs/processing-run-execution/spec.md](specs/processing-run-execution/spec.md). Blocks 7–10 are planning prerequisites, not evidence that their source has landed. At apply time, verify the required source APIs, registrations, and focused tests exist and pass; if any prerequisite is absent, stop and apply it first rather than recreating or assuming its contract here. This change then consumes the verified request/result separation of aggregate processed from successful updates, validating session accounting, Web adapter control-plane boundary, and reporter-backed administrative resolver signature without revising resolver/cache behavior.

The current pass order is count → eligibility/start publication → zero gate → skipped-ID snapshot → configuration read → repeated keyset batches. Each batch advances its cursor to the final fetched row, filters previously skipped IDs, evaluates remaining assets in bounded parallelism, and delays after the batch. Each asset resolves administrative areas first, optionally looks up airport infrastructure, applies city/state/country fallback, then independently writes Immich or records a skipped ID. There is no transaction spanning assets, batches, PostgreSQL, and SQLite.

## Goals / Non-Goals

**Goals:**
- Isolate one complete authoritative processing pass behind an injectable, awaitable, UI- and scheduler-independent boundary.
- Return the finalized block-7 result while using one finalized block-8 session for lifecycle, accounting, logs, and block-10 resolver activity.
- Preserve query, cursor, configuration, parallelism, resolution ordering, persistence, cancellation, failure, diagnostic, and partial-effect behavior.
- Leave production execution in-process and make every heavy collaborator replaceable or gateable for deterministic tests.

**Non-Goals:**
- Do not move schedule parsing/waiting, run admission, lock release, request creation, adapter arming, pending state, trigger APIs, CTS ownership, startup initialization, or UI/control-plane logging.
- Do not introduce the block-13 coordinator, a work detector, advisory locking, worker roles/processes, protocol envelopes, serialization, or progress coalescing.
- Do not change database predicates/schema, batching algorithms, source preference, geometry algorithms, cache synchronization, airport/admin ordering, fallback rules, skipped policy, settings, or public behavior.
- Do not add transaction, retry, rollback, compensation, run history, or checkpoint semantics.

## Decisions

### Return one terminal result from a reporter-supplied execution call

ProcessingRunExecutor.ExecuteAsync accepts ProcessingRunRequest, IProcessingEventReporter, and CancellationToken and returns Task<ProcessingRunResult>. On entry it captures zero-offset UTC start time through an injected or centralized TimeProvider, opens exactly one reporter session (thereby emitting RunStarted), and performs the authoritative count. It reports eligibility only after a successful non-negative count and captures terminal time after processing has stopped and cleanup-relevant scopes have unwound.

Completed, active-token-cancelled, and ordinary pass-failed executions build and finish the corresponding validated result with the session-owned counters, then return that same result after terminal acceptance. If opening or later reporter acceptance breaks the session, propagate the reporter infrastructure exception and do not recursively report, synthesize a second result, or repair Web state directly. This follows block 8's broken-session limit: a domain result may be constructible locally, but the call cannot promise a returned/observed terminal result when required reporting fails.

The request remains identity/trigger only. It does not gain settings, UI state, schedule, lock, or cancellation-source data. The supplied reporter is explicit so the same executor can later run with the Web adapter, no-op reporter, recorder, or worker bridge without changing processing.

### Keep eligibility and configuration ownership in the executor

The executor owns the exact GetUnprocessedCountAsync query as the authoritative pass fact. Future lightweight work detection may avoid launching an empty worker, but it neither replaces nor changes this count. Count success is the eligibility event; count cancellation/failure can terminate before eligibility.

Preserve the non-empty ordering exactly: the zero gate precedes skipped-ID and config reads; then load the skipped-ID set once; then obtain one AppConfig/ProcessingConfig snapshot for all batches. Do not move configuration into admission or the request. Preserve keyset AssetCursor.Initial, ordered (createdAt,id) batches, cursor advancement to the last fetched row before suppression, Math.Clamp(maxParallelism, 1, 32), the configured delay after every non-empty batch, and the eventual empty fetch. The batch diagnostic uses the run-local Updated count supplied by session accounting rather than ProcessingState. Extraction does not change Web compatibility projection: UI `Processed`/`ProcessedThisRun` remains session `UpdatedCount` (successful writes), never aggregate `ProcessedCount`.

This deliberately means eligibility can include IDs in skipped.db; those IDs may be fetched but are filtered before active evaluation and contribute no disposition. It also preserves the current race model: the count is informative, not a transactionally stable work set, and later database changes may make fetched work differ from the initial total.

### Move the per-asset pipeline as one unit and retain ordering

Move ProcessAssetAsync and its local step label with the pass. For each non-suppressed asset:
1. Resolve bundled-country and administrative areas with the processing settings and the finalized block-10 run reporting context.
2. If there is no country resolution, persist the ID to the skipped store, then commit one Skipped disposition and retain the current warning.
3. If enabled, run airport infrastructure lookup only after administrative resolution. Geometry containment overrides the administrative city; a non-containing best match is fallback only when administrative city is null.
4. Apply WithFallbackCity exactly once after the airport decision, preserving city → state → country ordering.
5. If writable, emit the existing verbose Trace detail before the write when configured, independently update asset_exif, then commit Updated. The non-verbose detail remains ILogger-only.
6. Preserve the logger-only no-city branch as Skipped without adding to skipped.db; preserve the no-admin-match branch as skipped-store insert followed by Skipped and its existing warning.
7. Convert handled per-asset exceptions into one Error diagnostic and Failed disposition while allowing other assets to continue. Active-token cancellation and block-6 critical exceptions escape this local boundary.

Do not replace OverturePlacesService with a general places/city lookup or reverse the resolution order. Airport infrastructure remains an optional post-admin override/fallback only; administrative state/country selection remains authoritative.

### Preserve independent writes and partial effects

ImmichDbRepository.WriteLocationAsync opens and commits its own PostgreSQL command; SkippedAssetsRepository.AddAsync opens and commits its own SQLite insert. Keep those operations and their ordering. There is no run-wide or batch transaction and no atomic transaction across PostgreSQL and SQLite. Do not add retries, compensation, or rollback.

A disposition linearizes only after its required persistence succeeds: Updated after the Immich command returns; no-country/no-admin Skipped after skipped insertion returns; no-city Skipped after the deliberate decision because it has no skipped-store write. The block-8 session then publishes committed accounting through its non-cancelled path. Cancellation or fatal failure later in the run retains earlier database changes and counters. A write failure produces Failed rather than Updated. A skipped insert failure produces Failed rather than Skipped; an insert that committed immediately before an ambiguous provider exception may remain as today because this extraction adds no compensation.

### Preserve cancellation and exception boundaries from blocks 6–10

The host owns and supplies the token. The executor owns no CTS and never calls Cancel. Check/propagate active cancellation through count, skipped/config/batch operations, parallel enumeration, resolver/cache work, airport lookup, write, and delay as their finalized APIs permit. An OperationCanceledException denotes run cancellation only when attributable to the active token. Per-asset active cancellation must escape to stop the parallel pass; an unrelated cancellation-like exception follows the existing handled dependency/per-asset failure path. Pass-level unrelated cancellation-like exceptions are Failed.

Keep the block-6 critical-exception taxonomy: exceptions such as OutOfMemoryException that finalized geodata collaborators must not downgrade escape per-asset handling and end the pass as Failed. Do not broaden every provider fallback into fatal behavior; ordinary finalized resolver/cache/lookup fallbacks remain intact. On unwind, reporter activities, committed dispositions, and terminal finish use the session's non-cancelled cleanup paths. A fatal pass outcome does not add to per-asset FailedCount; the block-9 adapter alone preserves its extra legacy UI error.

### Use narrow executor-facing seams without duplicating singleton owners

Prefer narrow interfaces implemented by, or thin adapters over, the existing production classes for only the operations execution calls: configuration read; count/batch/write; skipped snapshot/add; administrative resolve with the finalized block-10 reporting context; and airport infrastructure lookup. Do not redesign their broad public APIs or move query/geometry logic into the executor. If the finalized blocks already provide equivalent seams, consume them rather than create parallel vocabulary.

Register the stateless executor as a singleton. An abstraction directly implemented by an existing production singleton is factory-aliased to that exact object and verified with `ReferenceEquals`. If a thin adapter is required, register exactly one adapter singleton and alias its interfaces to it; do not claim the adapter is reference-identical to its wrapped service. This preserves caches, Npgsql data source ownership, skipped-store path, resolver in-flight maps, places release cache, and reporter correlation without duplication. The executor creates no DI scope and disposes none of these injected services. Its request, session, clock values, config/skipped snapshots, cursor, batch number, step labels, and counts are invocation-local, making the type safe under tests even though admission currently allows only one production pass.

Keep ProcessingBackgroundService registered as both its concrete singleton and hosted service resolving that exact instance. It still initializes skipped.db before scheduling, appends startup/schedule/contention logs directly, acquires/releases the run lock, immediately marks pending, creates/arms the accepted request per block 9, owns manual CTS, and dispatches manual work. It delegates the accepted request/reporter/token to the executor and does not retain count, batch, asset, resolution, or write logic.

### Make extraction verification deterministic without consuming block 14

Add clock injection (TimeProvider or the finalized equivalent), narrow collaborator fakes, a block-8 recording/fault-injection reporter, and TaskCompletionSource gates with asynchronous continuations. Tests control count, batch pages, parallel completion order, persistence return/failure, reporter backpressure/failure, cancellation checkpoints, and terminal time without sleeps, PostgreSQL, SQLite, geodata, cron timing, hosted loops, or Blazor.

Block 11 verifies extraction equivalence and boundaries: empty short circuit/order; one representative mixed batch preserving suppression, admin/airport/fallback/write/skip behavior; active cancellation versus unrelated OCE/fatal failure; post-persistence accounting; reporter/session identity and terminal result; host delegation after manual/scheduled admission; and DI identity/lifetimes. Existing Phase 1 and blocks 7–10 tests remain regressions. Block 14 still owns the broader scheduler-free executor matrix and reusable fake fixture; block 11 should expose the seams it needs without moving scheduler/coordinator concerns into executor tests.

## Risks / Trade-offs

- [Block 10's final source API differs from its pre-run plan] → Apply block 10 first, re-read its final types, and consume its reporter-backed resolver context; do not edit block 10 or recreate a second progress adapter.
- [A count and later batches observe different database snapshots] → Preserve current independent queries; document that the total is informational and avoid introducing a transaction during extraction.
- [Parallel completion reorders diagnostics] → Preserve per-asset causal order and coherent session accounting, but do not impose global asset ordering absent today.
- [Persistence succeeds and reporting then fails] → Preserve the write, propagate the broken reporter, and do not pretend rollback or direct-state repair occurred.
- [Interface aliases accidentally create duplicate singleton caches/state] → Register aliases through factories to existing instances and verify reference identity in composition tests.
- [Extraction duplicates block 14] → Keep block-11 tests focused on movement equivalence, host delegation, and seams; defer exhaustive scheduler-free scenario coverage and fixture hardening to block 14.

## Migration Plan

1. Verify blocks 7–9 and their tests are applied; apply block 10, re-read its final resolver/reporting API, and stop rather than duplicate missing prerequisite contracts.
2. Add or reuse narrow executor-facing seams and deterministic time support, aliasing production singleton instances without changing collaborator behavior.
3. Move the outer pass and per-asset helper together into the executor; preserve exact operation, persistence, event, and exception ordering.
4. Register the singleton executor and reduce ProcessingBackgroundService only to its existing startup/schedule/admission/request/pending/CTS/delegation responsibilities.
5. Add deterministic extraction, host-delegation, and DI lifetime tests; retain Phase 1 and blocks 7–10 regression suites.
6. Run focused tests, npm run test, strict OpenSpec validation, and a scope diff proving no code from blocks 10, 12+, UI, protocol, work detection, or geometry was changed.
7. Roll back by restoring the pipeline methods to the hosted service and removing executor/seam registrations; no data migration or compensation is required.
