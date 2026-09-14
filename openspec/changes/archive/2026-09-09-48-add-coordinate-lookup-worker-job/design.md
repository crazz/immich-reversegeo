## Context

See `proposal.md` for motivation and `specs/coordinate-lookup-worker-job/spec.md` for required behavior. Finalized block 47 reserves the exact v2 kind `CoordinateLookup`, advertises only registered handlers, uses one envelope-level JobId, keeps common ready/log/activity/terminal/error frames host-owned, and exposes immutable arbitration metadata without policy. Its v1 and existing v2 contracts are frozen.

Current Lookup starts with bundled Overture country identity, snapshots configured city-resolver overrides, starts Overture-cache, optional GADM-cache, optional bundled-airport, and optional live-Places work, then queries/compares source diagnostics and applies the same airport/admin/fallback ordering used by processing. Lookup exposes exactly three source controls: airport infrastructure, live Places, and preferred GADM. Preferred GADM implicitly enables the current territory-family expansion. It may publish on-demand cache files but performs no Immich database writes. GADM is an optional non-commercial source and that constraint is currently visible beside its checkbox and in public data-source documentation.

## Goals / Non-Goals

**Goals:**
- Implement one closed, typed, cancellable CoordinateLookup worker variant and handler on block 47's lifecycle.
- Preserve the current page's source options, diagnostic richness, cache behavior, and final country/state/city decisions while keeping processing's airport/admin alignment.
- Make protocol validation, attribution, licensing, progress, cancellation, side effects, and exits deterministic and testable before the Web page is routed to it.

**Non-Goals:**
- Change `Lookup.razor` injection, routing, controls, markup, status/cancel/busy presentation, or public docs; block 49 owns those changes.
- Implement the shared active-job coordinator, queue, priority, or busy policy (block 50), standalone cache mutation jobs (block 51), or cache deletion/reset coordination (blocks 52–54).
- Change geometry algorithms, ranking thresholds, source precedence, cache formats/paths, processing behavior, GADM licensing terms, or the frozen v1/ProcessAssets protocol.
- Read or mutate Immich asset/exif/skipped data, add schema objects, or use the processing-only advisory lock.

## Decisions

### 1. Use the exact CoordinateLookup kind and capability-specific DTO graph

Register one `CoordinateLookup` descriptor/handler pair after its DTOs and codec exist. The request and result are concrete variants in block 47's closed unions; no `object`, dictionary, `JsonElement`, service model, Razor-private class, or arbitrary option bag crosses the wire. The envelope JobId remains the sole identity and handlers cannot emit terminals.

The request contains:
- finite `Latitude` and `Longitude` doubles;
- `IncludeAirportInfrastructure`, `IncludeLiveOverturePlaces`, and `PreferGadmAdministrativeAreas`, matching the only current page source controls;
- a typed city-resolver override snapshot, represented as one optional default profile plus a canonically ISO3-sorted bounded list of unique country-profile entries. Each profile carries a bounded ordered subtype list and a closed tie-break value.

The worker continues to combine those overrides with its versioned bundled city-profile catalog after country identity is known. It does not send the entire AppConfig and does not reread mutable settings after acceptance. Latitude [-90, 90] and longitude [-180, 180] are inclusive; reject NaN/infinity, alternate numeric encodings disallowed by the protocol, duplicate profile countries/subtypes, invalid ISO3/tie-break values, and all documented count/string bounds before registry lookup or heavy-service resolution. Do not clamp, wrap, or normalize coordinates.

Alternative: load AppConfig entirely in the worker. Rejected because launch-time UI choices and config races would not form one immutable request. Alternative: send one already-resolved city profile. Rejected because the country is unknown until heavy lookup. Alternative: expose processing's separate GADM enable/prefer/territory flags. Rejected because the current Lookup UI has one prefer-GADM option and always expands its fallback family.

### 2. Extract a reusable lookup operation but do not route the page in this block

Move page-owned resolution into a UI-independent operation consumed by the worker handler, with narrow seams for bundled country, city profile, Overture/GADM cache ensure/readiness, division diagnostics, airport diagnostics, live Places diagnostics, progress/event reporting, and cancellation. Keep `Lookup.razor` on its existing direct path for block 48; block 49 will switch it to the client and can remove the duplicate path only after parity is proven.

The operation follows the existing observable order and decisions:
1. country from bundled Overture; stop cleanly on spatial no-match or identity mapping failure;
2. derive city profile and GADM fallback candidates;
3. start the same independent cache/airport/Places operations when enabled, with structured ownership so all started tasks are observed;
4. ensure Overture cache, query Overture division diagnostics, and derive Overture state/city;
5. if preferred, ensure GADM candidates in deterministic catalog order, query available caches, and derive GADM state/city;
6. await enabled airport and Places diagnostics;
7. choose GADM values over Overture values field-by-field when requested; choose geometry-containing airport over admin city, otherwise admin city over airport fallback, then state, then country. Places remains diagnostic-only.

Ordinary cache/source exceptions that current Lookup converts to trace/error diagnostics stay completed degradation. Active-token cancellation must escape those boundaries. Foreign cancellation-like failures and critical exceptions follow finalized cancellation/failure rules rather than being mislabeled as cancellation or source unavailability.

Alternative: call only `AdministrativeAreaResolverService`. Rejected because current Lookup returns raw candidates/cache diagnostics, always uses its GADM fallback expansion when selected, and separately exposes optional Places/airport details that the processing-oriented result does not carry. Shared pure selection helpers should still be reused so processing and Lookup do not drift.

### 3. Return transport-owned diagnostic sections and closed final attribution

Define immutable transport DTOs rather than serializing current mutable service records directly. The completed result echoes coordinates/options and contains:
- country status, ISO3/alpha2/name/source id or safe failure reason;
- per-source state (disabled, skipped, ready, no-match, unavailable, failed as applicable), cache readiness/country codes, release/version, best match, deterministically ordered bounded candidates, and safe error;
- Overture and GADM resolved state/city, city-profile summary, bounded trace lines;
- final country/state/city plus one closed source value per non-null field: bundled country divisions, cached Overture divisions, cached GADM divisions, bundled airport geometry match, or bundled airport fallback.

Candidate DTOs preserve every field the current UI renders, including selection/decision, geometry/bbox facts, admin/type/category/class/status, distance/confidence/area, and Overture record source lists. Disabled and skipped are explicit states, not null conventions. Candidate ordering uses the existing page sort rules plus a final ordinal stable ID tie-break before deterministic truncation. Protocol limits are named constants covered by boundary goldens; truncation is reported in its section rather than silent. Safe source failures exclude stacks, local paths, raw input, stderr, and secrets.

Every GADM section includes stable attribution metadata even on disabled/unavailable/no-match paths when GADM was requested: dataset name, returned version when known, official license URL, and the existing plain-language academic/other non-commercial-use warning. Block 49 can therefore keep the license visible without reconstructing legal metadata in the Web process.

Alternative: return only the final GeoResult. Rejected because Lookup is a diagnostic feature. Alternative: serialize page-private/service records. Rejected because that couples the wire format to mutable UI/source internals and leaves disabled/failure states ambiguous.

### 4. Model lookup progress as discrete typed steps plus common activity/log frames

Add a CoordinateLookup progress payload with a closed step discriminator and bounded status text/optional source and country code. Steps cover country, Overture cache/admin, GADM cache/admin, airport, live Places, and final selection. Do not emit percentages: cache downloads and optional source tasks can overlap and have no stable common denominator.

Use block 47 common activity frames with unique IDs for each non-ready cache download or existing-download wait. Labels retain the current source/country wording; every accepted start is ended in structured cleanup on success, degradation, cancellation, or reporter failure. Common safe logs record source readiness/degradation and selection decisions. The typed terminal result remains the authoritative diagnostic snapshot; progress/log frames are transient observation and must not be required to reconstruct it.

Alternative: encode all trace lines as generic progress. Rejected because consumers need stable steps and block 47 already separates logs, activities, and kind-specific events.

### 5. Preserve cooperative cancellation and partial cache side effects

Pass the active token to every token-aware cache, country, division, GADM, airport, and Places operation; check before starting each optional or sequential operation and before result publication. Started parallel tasks are held in a structured operation scope and observed during unwind so cancellation cannot leak tasks or exceptions. An active-token cancellation yields no success result; the host emits one cancelled terminal and exit 130 if transport remains healthy. An unexpected domain failure maps to exit 4, startup/composition/config infrastructure to 5, and stdout protocol failure to 6 under block 47 precedence.

Cache publication is an intentional side effect: preserve per-country in-flight sharing inside the worker, source validation, unique temporary files, cleanup, and atomic replace/move. Cancellation before publication leaves no partial final database; cancellation after validated atomic publication does not delete the usable cache. The result records readiness only for observations made before terminal cancellation/failure.

Alternative: rollback published caches when a lookup is cancelled. Rejected because caches are shared regenerable assets and publication is already atomic; rollback would create races and discard valid work.

### 6. Declare heavy local arbitration metadata but do not widen the advisory lock

The descriptor declares capability family CoordinateLookup, cancellable=true, heavy=true, geodata-bearing=true, and the same exclusive heavy-geodata admission resource class that block 50 will use for processing/cache/reset conflicts. Block 48 registers metadata only; it does not own a slot or define busy behavior.

Do not acquire block 31's PostgreSQL advisory run lock. That lock is finalized as protection for asset-processing work against one Immich database, and block 50 explicitly retains it as the cross-container processing safeguard. CoordinateLookup does not touch asset data and may run in deployments where diagnostic lookup should not depend on a processing lock. Consequently exit 3 is not a lookup/local-busy code. The admitted Lookup may write only Overture/GADM cache files under the existing cache services. Cross-Web-entry serialization arrives in block 50; cache delete/reset conflicts and standalone mutation semantics remain blocks 51–54.

Trade-off: separately launched private workers do not gain a new cross-process cache lock here. This is acceptable because CoordinateLookup has no public direct-launch entry and block 49 will use the Web coordinator; existing atomic publication/cleanup remains the last-resort file-safety mechanism. If cross-controller cache arbitration is later required, it needs an explicit lock-domain design rather than silently broadening the processing advisory key.

### 7. Lock compatibility and behavior with deterministic tests

Add pure validation/codec tests, operation/handler parity tests, and the existing real subprocess fixture. Checked-in fixtures or controlled seams provide country polygons, Overture/GADM candidates, airport matches, live Places, cache states, faults, and gates; no acceptance test calls live Azure/GADM endpoints or an Immich database.

Protocol goldens cover canonical request/options/profile ordering, each progress variant, all source-section states, candidate/source attribution and truncation, GADM license metadata, success/no-country/degraded result, cancelled/failed terminal, malformed/range/bounds/duplicate/kind mismatch, and v2 ready advertisement. Rerun v1 and ProcessAssets goldens byte-for-byte.

Parity matrices use the named baseline suites (bundled country, Overture divisions, Overture Places, GADM divisions, territory resolver) and explicit combined cases for GADM preference/fallback, airport geometry override/non-containing fallback, state/country city fallback, Places diagnostic-only behavior, and source failure retention. Child-fixture cases assert ready/identity/kind, validation-before-DI, event/activity order, cancellation, one terminal, stdout/stderr/exit finality, no DB repositories, cache publication/cleanup, and exits 0/2/4/5/6/130; exit 3 is explicitly absent.

## Risks / Trade-offs

- [Extracted worker behavior drifts from the still-direct page] → Drive both through shared pure policy helpers and compare typed worker output against deterministic page/service baselines before block 49.
- [Transport diagnostics become too large] → Apply explicit per-section candidate/source/trace/profile bounds, deterministic ordering/truncation, and golden boundary tests.
- [Concurrent optional work obscures progress or cancellation] → Use discrete steps, uniquely scoped activities, and structured observation/unwind rather than percentages or fire-and-forget tasks.
- [GADM use becomes invisible after process isolation] → Carry stable attribution and non-commercial-use notice in every requested GADM result section for block 49 to render.
- [Lookup cache writes conflict with later mutation/delete work] → Declare the exclusive heavy-geodata resource class now, retain atomic source publication, and leave coordinator/delete policy to blocks 50–54.
- [Advisory-lock expectations are ambiguous] → State explicitly that the processing lock is not acquired, no asset DB writes occur, and no lookup path uses exit 3.

## Migration Plan

1. Apply finalized block 47 and verify the exact `CoordinateLookup` reserved kind/registry/event/terminal APIs; stop if the implementation differs rather than inventing a parallel protocol.
2. Add bounded transport DTOs, semantic validators, codec variants, attribution/license constants, and negative/canonical goldens while preserving all old goldens.
3. Extract/reuse pure selection and lookup-operation seams, implement typed progress/activity/log reporting and cancellation-safe task ownership, then register the handler/descriptor.
4. Add deterministic parity/unit tests and real child-worker fixture cases, including no-DB-write and cache-side-effect assertions; run default and explicit integration suites.
5. Keep `Lookup.razor` on its current path until block 49. Rollback unregisters the kind and removes only its v2 variants/handler; no DB or cache migration is required and already valid caches remain usable.
