## Context

See `proposal.md` for motivation and `specs/web-lookup-worker-routing/spec.md` for observable behavior. Today `Lookup.razor` injects `GadmDivisionsService`, `GadmDivisionCacheService`, `OverturePlacesService`, `OvertureDivisionCacheService`, and `OvertureDivisionsService`; it owns cache ensuring, resolution, result shaping, and a single `_running`/`_lookupStatus` pair. Coordinate paste parsing is synchronous and responsive, but numeric submission has no explicit finite/range validation, inputs/options remain editable while work runs, there is no cancel action, and exceptions are rendered directly.

Block 47 defines the strict v2 typed job/session lifecycle: one `jobId`, kind-correlated events, common ready/start/log/activity/terminal frames, controller classification when no valid worker terminal exists, cancellation grace/kill ownership, and completion only after process exit plus stream finality. Finalized block 48 owns the exact `CoordinateLookup` request fields (`Latitude`, `Longitude`, `IncludeAirportInfrastructure`, `IncludeLiveOverturePlaces`, `PreferGadmAdministrativeAreas`, and canonical typed default/country city-profile overrides), the closed discrete progress-step payload, transport-owned source/result/attribution/license DTO graph, worker handler, in-job atomic cache ensuring, cancellation behavior, and exclusive-heavy-geodata descriptor metadata. This change consumes those contracts and must not edit, duplicate, or weaken block 48. Block 50 follows this change in the numbered implementation sequence even though the original block 49 text named it as a prerequisite, so the controller boundary must allow central arbitration to replace a temporary admission implementation.

Standard and Web-only both have an interactive Web control plane and must be able to launch the same internal worker job. Run-once has no Lookup UI surface. `ProcessingState` remains exclusively the processing-run projection, and Lookup is a read-only preview that must not write `asset` or `asset_exif`.

## Goals / Non-Goals

**Goals:**

- Keep local parsing/validation immediate while isolating all geodata, cache ensure/query, and live Places work in one v2 worker.
- Give the page a deterministic, testable state machine for admission, startup, activity/progress, cancellation, terminal result, diagnostics, safe errors, and control enablement.
- Preserve one correlation identity and make circuit/navigation disposal close the owned worker session without stale UI mutation.
- Establish a small admission/launch abstraction that block 50 can adopt without rewriting page logic.
- Preserve Lookup result semantics and diagnostics, including GADM licensing guidance, in Standard and Web-only modes.

**Non-Goals:**

- Change block 48 request/result fields, resolver precedence, cache semantics, or diagnostic algorithms.
- Add a queue, priority/fairness policy, durable jobs, cross-Web-container lookup exclusion, or block 50's global coordinator.
- Project Lookup activity into `ProcessingState`, Dashboard processing counts/logs, or scheduled-run state.
- Write Immich asset metadata or add any database mutation to Lookup.
- Remove every heavy Web registration globally; block 55 owns that cleanup after all callers migrate.
- Redesign the result cards or add scoped Razor CSS.

## Decisions

### 1. Put a lightweight controller/state seam between Razor and the worker session

`Lookup.razor` will retain coordinate parsing and rendering, but an injectable page-scoped orchestration object (names finalized during apply) will own an immutable submission snapshot, operation generation, `jobId`, admission/launch handle, event projection, cancellation, and async disposal. Its dependencies are control-plane-only: a lookup-job client, lightweight effective-settings reader/mapper, clock/identity seam where already established, and renderer notification callback. It must not depend on Overture/GADM resolvers, cache services, DuckDB/SQLite geodata services, `ImmichDbRepository`, or `ProcessingState`.

A pure state object exposes phases equivalent to `Idle`, `Validating`, `Admitting`, `Starting`, `Running`, `CancelRequested`, `Completed`, `Cancelled`, `Busy`, and `Failed`, plus the active status/activity, safe error, typed result/diagnostics, and control flags. `Validating` is normally synchronous and may not become an observable delay, but naming it makes transition tests precise. Razor does not infer terminal state from button flags.

Alternative: keep all session logic in the component. Rejected because circuit disposal, race testing, and block 50 replacement would remain coupled to markup. Alternative: publish Lookup into `ProcessingState`. Rejected because it pollutes processing-only counts, logs, and transitions with an unrelated diagnostic job.

### 2. Validate locally before admission and snapshot every submitted option

Paste parsing remains local. Submission rejects non-finite latitude/longitude and values outside latitude -90..90 or longitude -180..180 before requesting admission, creating a `jobId`, launching a process, clearing the last good result, or initializing any worker/geodata service. The displayed validation message identifies the invalid field/range and controls remain usable. Block 48 still validates the wire request independently as the trust boundary.

On a valid click, capture the finalized request exactly: `Latitude`, `Longitude`, `IncludeAirportInfrastructure`, `IncludeLiveOverturePlaces`, `PreferGadmAdministrativeAreas`, and the canonically bounded typed optional default plus ISO3-sorted country city-profile overrides. Do not add a separate diagnostic-flags bag, full AppConfig, schedule, database, or mutable UI state. Later edits cannot alter the in-flight request. To avoid ambiguous submissions, paste, numeric inputs, options, and Lookup are disabled from admission through terminal cleanup; Cancel is shown/enabled only for a cancellable active session and becomes disabled with “Cancelling…” after the first request. Validation failure and fail-fast busy leave controls enabled. A newly admitted request clears stale error/result content; a rejected invalid or busy attempt may retain the last completed result while clearly labeling the new attempt's outcome.

Alternative: let the worker provide the only validation. Rejected because it consumes admission/startup for obvious input mistakes and makes the form feel unresponsive.

### 3. Use one correlated operation from admission through final stream drain

The controller allocates or receives exactly one lower-case GUID-D `jobId` for the valid attempt and pairs it with a monotonically increasing page-operation generation. Every event must match v2, `CoordinateLookup`, and the exact active `jobId`; kind/identity mismatches are protocol failures handled by the existing controller classifier, not displayed as valid progress. UI callbacks also check the generation and disposed flag so late callbacks from a cancelled, superseded, or disposed operation cannot overwrite newer state.

There is at most one component-owned operation. Double click/re-entry is rejected locally while active; no second session is created. A valid worker terminal supplies the semantic outcome, while session completion after exit/stdout/stderr drain is the cleanup gate before controls return to enabled. Startup/crash/protocol/transport/missing-terminal/forced-kill paths use the existing controller-classified outcome and never fabricate a worker terminal or accept a late result. Session disposal is idempotent and awaited.

Alternative: correlate only by a component boolean or accept the latest callback. Rejected because late stream callbacks can corrupt a subsequent attempt and cancellation can target the wrong child.

### 4. Keep admission replaceable until block 50 centralizes it

Define one controller-side admission/launch interface whose result is a closed union equivalent to `Admitted(handle)`, `Busy(active job metadata)`, or `Unavailable(safe reason)`. An admitted handle owns the v2 session and authoritative release callback. A busy/unavailable result launches no process. The page consumes only this result and never reads a global singleton directly.

For block 49 apply before block 50, register a temporary process-local exclusive gate covering Lookup launches through this client, with atomic acquire and release in one `finally`/async-dispose path for startup failure, normal completion, cancellation, crash, protocol failure, and circuit disposal. Publish/use the same admission resource-class and active metadata shapes from block 47 so block 50 can replace the implementation and widen coverage to processing/cache/reset jobs. Do not teach the page temporary policy details. Once block 50 lands, delete the temporary registration/tests that assert lookup-only ownership and rerun this change's contract suite against the shared coordinator.

The firm UI policy is fail-fast: show “Lookup could not start because <friendly active job kind> is running. Try again after it finishes.” Do not expose raw enum/type names, PID, secrets, or arbitrary worker text; do not queue and do not fall back in process. If the temporary seam cannot reliably name a non-Lookup owner before block 50, use the safe generic “another background job” label and record that limitation in tests; block 50 upgrades the metadata without a page change.

Alternative: retain block 50 as a hard prerequisite. Rejected for the requested numbered transition (50 comes next). Alternative: bypass admission temporarily. Rejected because duplicate clicks/circuits could launch overlapping heavy Lookup workers and force later page rework.

### 5. Map lifecycle frames to bounded user-facing states without exposing internals

The mapping is:

| Input | Page projection |
|---|---|
| Valid request awaiting admission | `Admitting`: “Checking worker availability…” |
| Admitted, process not accepted/started | `Starting`: “Starting isolated lookup…” |
| `job-started` | `Running`: “Lookup worker started.” |
| finalized CoordinateLookup progress event | show its bounded closed step (country, Overture cache/admin, GADM cache/admin, airport, live Places, or final selection) plus safe optional source/country/status text; never invent counts or percentages |
| `activity-started` / `activity-ended` | show the most recent active bounded label; restore the previous active label for nested activities; ignore no identity/kind mismatches because those fail classification |
| log event | not a terminal or raw error; only explicitly safe lookup status levels/messages approved by the v2 contract may supplement status, never diagnostics/result |
| completed terminal with typed result | map transport DTO to existing country/admin/GADM/airport/Places/final-output/trace cards and requested diagnostic candidate sections |
| cancelled terminal after cancel | `Cancelled`: “Lookup cancelled.”; no result invented |
| failed terminal | `Failed` with stable safe code/message and no fabricated result or source diagnostics; no stack trace/stderr/raw exception |
| controller-classified startup/crash/protocol/transport/kill outcome | `Failed` or `Cancelled` according to the established classifier, with bounded safe guidance |
| busy admission | `Busy`, controls immediately re-enabled, no worker/session created |

Source-level disabled/skipped/no-match/unavailable/failed degradation represented inside block 48's completed typed result remains a completed Lookup and is rendered on that source card/trace; it is not promoted to a job failure and does not discard independently resolved fields. A cancelled or failed terminal carries no completed partial result and cannot be displayed as successful.

Alternative: treat the last progress text as completion. Rejected because progress is informational and the terminal/finalizer owns outcome authority.

### 6. Cancellation and circuit disposal share one stop path

User Cancel invokes one idempotent stop operation against the exact active `jobId`, using block 47's cooperative cancel, grace period, and process-tree kill escalation. The state changes immediately to `CancelRequested`, but controls remain locked until the authoritative terminal/classified completion and stream drain; a completion racing with Cancel wins according to the session finalization gate. Repeated Cancel, navigation disposal, and circuit disposal join the same stop task.

The component implements async disposal. It marks itself disposed first, suppresses further render callbacks, requests stop for an admitted active job, awaits bounded session cleanup/disposal, and releases temporary/shared admission exactly once. Disposal must not block synchronously on the Blazor renderer and must not leave an orphan process. Host shutdown remains launcher-owned and composes with this call.

Alternative: cancel only the page token or dispose without stopping. Rejected because the child can continue expensive cache/geodata work after its circuit disappears.

### 7. Preserve mode behavior and dependency boundaries

Register the lightweight lookup client/controller in Standard and Web-only interactive Web compositions. Both launch the same private v2 worker with the worker-role dependency graph from block 48; neither resolves geodata in the Web host. Run-once does not expose/serve the interactive Lookup page. If a supported Web mode cannot construct or launch the internal worker, Lookup renders a safe unavailable/failure state and retains form controls; it never calls local resolvers.

Add composition assertions that the page/controller graph contains no heavy Overture/GADM resolver/cache services and does not resolve `ProcessingState` or asset repositories. This change need not remove registrations still used elsewhere before block 55, but the Lookup path itself must be free of them. The lookup request/handler is read-only with respect to Immich PostgreSQL: the preview label remains “what would be written,” and tests prove no asset update command/repository method is invoked.

Alternative: support Standard through the old direct path and worker-route only Web-only. Rejected because behavior would drift by mode and Standard would keep geodata resident in Web.

### 8. Keep GADM licensing and errors explicit

The GADM option label and result card continue to say it is experimental and for non-commercial use; the running/status area includes a short reminder when GADM is selected, and public docs link the GADM license. A GADM cache/download/query failure returned as a safe source diagnostic says that GADM data is unavailable and that independently resolved Overture fields remain usable; it must not imply the license caused the technical failure. Generic worker failures must not echo download URLs, local paths, exception text, or license text supplied by untrusted output.

Alternative: rely only on the data-sources page. Rejected because users can enable the option directly on Lookup and need the restriction at the decision point.

### 9. Test the controller contract before component markup details

Use the existing MSTest project and a fake admission/lookup client plus deterministic session/event fixture to exhaustively test state transitions, control flags, correlation, cancellation races, stale callbacks, classified failures, and disposal without adding a broad Blazor test framework. Add a small Razor/component rendering seam only if needed to assert essential labels/buttons and GADM copy. Reuse the real worker fixture established by blocks 47–48 for one end-to-end successful result/diagnostic flow, cancel flow, and startup/protocol failure classification; do not duplicate block 48 resolver parity suites.

Add Standard/Web-only composition tests and negative assertions for no Lookup-path heavy-service resolution, `ProcessingState` events, or asset writes. Retain block 48's fixture/goldens as the authority for wire shape and resolver results.

## Risks / Trade-offs

- [Applied block 48 APIs differ from its finalized planning contract] → At apply start bind to the exact request fields, city-profile override graph, closed progress discriminator, transport result/source/attribution/license DTOs, cancellation semantics, and descriptor metadata; stop rather than create parallel DTO/event contracts, edit block 48, or weaken type safety.
- [Temporary admission overlaps block 50 ownership] → Keep it behind the shared-shaped interface/resource metadata, scope it narrowly, mark explicit replacement tasks, and remove only its implementation when block 50 lands.
- [Circuit disposal races terminal delivery] → Mark disposed/generation first, join one idempotent stop/dispose task, and release only after session finality.
- [Progress volume overwhelms the circuit] → Project only bounded latest state and rely on block 65 for broader coalescing; never append every progress/log frame to component state.
- [Old result appears to belong to a rejected attempt] → Retain it only for validation/busy convenience with explicit “last completed lookup” labeling; clear it once a new job is admitted.
- [Safe errors lose useful source context] → Render typed source diagnostics from completed results separately from controller/job failures and include stable support-oriented error codes where supplied.

## Migration Plan

1. Confirm blocks 47 and 48 are applied and freeze the exact v2 `CoordinateLookup` DTO/event/session names used by the controller. Stop implementation if their identity, cancellation, terminal, or typed-result contracts differ materially from finalized artifacts.
2. Add the page-independent state/controller and fake contract tests, including local validation and all lifecycle/disposal races.
3. Add the temporary shared-shaped admission/launch implementation, DI registrations, and real-worker fixture coverage; route `Lookup.razor` through it and remove all direct heavy injections/methods from the page.
4. Verify Standard and Web-only composition, no in-process fallback, no `ProcessingState` events, and no asset writes; update public docs and GADM copy.
5. When block 50 lands, replace the temporary implementation with the shared coordinator adapter, delete the temporary gate, and rerun busy/release/cancel/crash/reuse tests without changing the page contract.
6. Land before block 55 removes heavy Web registrations. Rollback restores the prior page only while those registrations still exist; after block 55, rollback must revert the dependent removal too. No data or database migration is required.
