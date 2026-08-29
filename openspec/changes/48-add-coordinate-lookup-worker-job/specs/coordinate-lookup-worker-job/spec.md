## Purpose

Runs the existing coordinate-resolution and diagnostic workflow in a temporary worker with typed outputs, observable progress, cancellation, source attribution, and explicit data-source constraints.

## ADDED Requirements

### Requirement: CoordinateLookup request is typed and validated before geodata work
The system SHALL implement the exact case-sensitive v2 worker job kind `CoordinateLookup`. Its request SHALL carry one canonical job identity at the envelope level; finite latitude and longitude; the current Lookup source choices for bundled airport infrastructure, live Overture Places, and preferred GADM administrative areas; and a bounded, deterministic city-resolver override snapshot sufficient to reproduce the configured country-aware profile. Latitude MUST be within -90 through 90 inclusive and longitude MUST be within -180 through 180 inclusive. The system MUST reject non-finite, out-of-range, malformed, duplicate, non-canonical, or over-limit request values before job acceptance, handler resolution, cache access, network access, or geodata initialization, and MUST NOT wrap or clamp coordinates.

#### Scenario: Boundary coordinates are accepted
- **WHEN** a request contains finite latitude -90 or 90 and longitude -180 or 180 with valid options
- **THEN** request validation accepts the coordinate values unchanged for lookup

#### Scenario: Invalid coordinates are rejected before heavy work
- **WHEN** latitude or longitude is non-finite, malformed, or outside its inclusive range
- **THEN** the worker emits no accepted-job event or terminal, initializes no geodata or cache service, performs no network or filesystem mutation, and returns the established invalid-input exit 2

#### Scenario: Current Lookup options are snapshotted
- **WHEN** a valid request is built from the current Lookup inputs
- **THEN** it contains the airport-infrastructure, live-Places, and prefer-GADM choices plus the applicable configured city-resolver overrides, and does not carry unrelated processing, schedule, database, or UI state

### Requirement: Resolution behavior remains aligned with Lookup and processing
The handler SHALL first resolve country identity from bundled Overture country divisions. After a country match it SHALL preserve the current Lookup cache-ensure and diagnostic behavior for cached Overture divisions; SHALL expand the same GADM territory candidate family and ensure/query it only when the current prefer-GADM option is enabled; SHALL run bundled airport diagnostics only when enabled; and SHALL run live Overture Places diagnostics only when enabled. Administrative state and city selection MUST apply the same country-aware city profile and current GADM-over-Overture preference. Final city selection MUST use a geometry-containing airport first, otherwise the administrative city, otherwise a non-containing airport fallback, then state, then country. Live Places SHALL remain diagnostic-only and MUST NOT replace the final city.

#### Scenario: Country has no bundled match
- **WHEN** bundled country resolution returns spatial no-match or identity-mapping failure
- **THEN** the job completes with typed country diagnostics, starts no per-country cache or optional source work, and returns no final location fields

#### Scenario: GADM is disabled
- **WHEN** prefer-GADM is false
- **THEN** no GADM cache is ensured or queried, Overture remains the administrative source, and the result records GADM as disabled rather than failed

#### Scenario: GADM is preferred with territory candidates
- **WHEN** prefer-GADM is true and the matched country expands to a GADM fallback family
- **THEN** the worker ensures those candidate caches in the same order as current Lookup, queries available candidates, uses GADM state and city when present, and falls back field-by-field to Overture when absent

#### Scenario: Airport geometry matches the coordinate
- **WHEN** airport lookup is enabled and its selected candidate geometry contains the point
- **THEN** that airport name overrides the administrative city and is attributed as a bundled airport geometry match

#### Scenario: Airport does not geometrically match
- **WHEN** airport lookup is enabled but its selected candidate does not geometrically contain the point
- **THEN** the administrative city remains preferred and the airport is used only when no administrative city was resolved

#### Scenario: Live Places is enabled
- **WHEN** the live Overture Places option is true after a country match
- **THEN** the job returns the current filtered/ranked place diagnostics and source list without changing final country, state, or city

### Requirement: Result and diagnostics are strongly typed and attributable
A completed job SHALL return one bounded `CoordinateLookup` result containing the echoed coordinate/options snapshot; typed country status and identity; cache readiness for each attempted country code; raw best matches and ordered candidate diagnostics for attempted Overture divisions, GADM divisions, airport infrastructure, and live Places; resolved Overture/GADM state and city values; city-profile summary; bounded trace entries; and final country/state/city values with a closed source attribution for every non-null field. Disabled, skipped, no-match, unavailable, and failed source conditions MUST remain distinguishable. Release/version identifiers and Overture record source lists SHALL be preserved where the current diagnostics expose them. Envelope-kind and payload-kind mismatches MUST fail closed.

#### Scenario: Independently useful fields survive a source failure
- **WHEN** an optional or cache-backed source reports an ordinary availability/query failure that current Lookup treats diagnostically
- **THEN** the completed result records that source and safe failure condition while retaining independently resolved fields and diagnostics

#### Scenario: Final fields identify their sources
- **WHEN** the job resolves any final country, state, or city field
- **THEN** the result identifies the closed source decision that supplied that field, including geometry-match versus airport-fallback distinctions

#### Scenario: GADM diagnostics are requested
- **WHEN** prefer-GADM is enabled whether GADM succeeds, is unavailable, or yields no match
- **THEN** the result identifies GADM, its dataset version when available, its official attribution/license URL, and a visible notice that GADM data is limited to academic and other non-commercial use

#### Scenario: Result bounds are exceeded
- **WHEN** candidate, source, trace, message, or profile data exceeds its protocol limit
- **THEN** the producer applies the documented deterministic bound without emitting invalid or unbounded protocol frames

### Requirement: Cache effects remain inside the admitted lookup job
The handler SHALL ensure missing Overture and enabled GADM per-country caches inside the same `CoordinateLookup` worker and MUST NOT launch nested workers. It SHALL preserve source-specific shared-task, validation, temporary-file cleanup, and atomic publication behavior. Cache readiness and ordinary source unavailability SHALL be observable in typed results and safe events. Cancellation or later failure MUST NOT roll back a cache file that was already validated and atomically published, and MUST NOT publish an incomplete temporary file.

#### Scenario: Missing Overture cache is downloaded
- **WHEN** country resolution succeeds and its Overture division cache is missing
- **THEN** the admitted worker ensures and validates that cache before querying it, reports download/wait activity, and records resulting readiness

#### Scenario: Cancellation interrupts a cache download
- **WHEN** cooperative cancellation occurs before cache validation and publication
- **THEN** the lookup ends as cancelled, incomplete temporary artifacts follow the existing source cleanup contract, and no partial cache is published

#### Scenario: Cache completes before later cancellation
- **WHEN** a cache is validated and published before another lookup step observes cancellation
- **THEN** the published cache remains available even though the job returns the cancelled outcome

### Requirement: Progress, activity, logging, cancellation, and terminal behavior use v2 lifecycle rules
After acceptance the worker SHALL emit the common job-started event, safe bounded log events, balanced common activity events for each cache download or shared-cache wait, and typed `CoordinateLookup` step progress sufficient to distinguish country, source-cache, administrative, airport, Places, and final-selection work. Progress SHALL describe discrete steps rather than promise a misleading percentage across optional/concurrent branches. The handler SHALL propagate the active cancellation token through cache, country, division, GADM, airport, and live-Places operations; SHALL stop starting new optional work after cancellation; and SHALL participate in structured completion of started work without leaking unobserved tasks. The host alone SHALL emit exactly one terminal after acceptance.

#### Scenario: Successful lookup lifecycle
- **WHEN** an accepted lookup completes
- **THEN** events use one job identity and exact `CoordinateLookup` kind, every accepted activity start has one matching end, one completed terminal contains the typed result, and the process exits 0

#### Scenario: Cooperative cancellation
- **WHEN** the active lookup is cancelled before completion
- **THEN** active operations observe cancellation, activities close, no completed partial result is emitted, one cancelled terminal is emitted when transport permits, and the process exits 130 absent a higher-precedence failure

#### Scenario: Ordinary source degradation
- **WHEN** a source failure is classified by current Lookup semantics as diagnostic degradation
- **THEN** the job may complete with typed failure diagnostics and exit 0 rather than converting that source condition into a worker crash

#### Scenario: Unhandled lookup failure
- **WHEN** a non-cancellation domain failure escapes the lookup operation
- **THEN** the host emits one safe failed terminal and selects managed execution exit 4 absent a higher-precedence startup, infrastructure, or output failure

### Requirement: CoordinateLookup has explicit isolation and arbitration metadata
The `CoordinateLookup` descriptor SHALL declare itself cancellable, heavy, geodata-bearing, and in the same exclusive heavy-geodata admission resource class used by processing and cache mutation for the later Web coordinator. This change SHALL NOT implement queueing, priority, busy presentation, or UI admission. The lookup handler SHALL NOT acquire the processing-only PostgreSQL advisory run lock, read or write Immich asset/exif rows, access skipped-asset persistence, or create any database schema asset. Exit 3 SHALL remain reserved for the established PostgreSQL advisory-lock busy outcome and SHALL NOT represent local lookup admission.

#### Scenario: Handler dependencies are composed
- **WHEN** the `CoordinateLookup` handler is registered
- **THEN** ready advertises the exact kind and registry validation confirms its typed request/result and exclusive heavy-geodata metadata

#### Scenario: Lookup executes without asset persistence
- **WHEN** a coordinate lookup succeeds, degrades, fails, or is cancelled
- **THEN** it performs zero Immich asset/exif writes and zero skipped-asset writes while permitting only the documented geodata cache-file side effects

#### Scenario: Local arbitration is deferred
- **WHEN** the descriptor metadata is inspected before block 50 is applied
- **THEN** it supplies policy facts but does not itself acquire a local slot, queue work, return a local busy exit, or alter processing admission

### Requirement: Protocol compatibility and deterministic verification
The v2 codec SHALL add only the exact `CoordinateLookup` typed request, progress, and result variants defined by this capability; v1 bytes and v2 `ProcessAssets` bytes SHALL remain unchanged. Canonical NDJSON goldens SHALL cover the request/options, each job-specific progress shape, completed result with diagnostics/attribution/license metadata, cancelled and failed terminals, malformed and kind-mismatched payloads, and bounds. Deterministic unit and child-process fixture tests MUST use checked-in bundled/cache/source fixtures or controlled seams and MUST NOT depend on live Overture, GADM, network timing, or the Immich database.

#### Scenario: Existing protocol goldens are rerun
- **WHEN** the CoordinateLookup codec variants are added
- **THEN** every v1 and existing v2 ProcessAssets golden remains byte-for-byte unchanged

#### Scenario: Real child fixture completes a lookup
- **WHEN** the worker-process fixture runs CoordinateLookup against deterministic country, division, GADM, airport, and Places fixtures
- **THEN** it verifies ready advertisement, exact identity/kind, ordered progress, balanced activity, typed result parity, one terminal, stream finality, and exit 0

#### Scenario: Managed exit matrix is exercised
- **WHEN** fixture cases cover invalid input, cancellation, domain failure, startup/infrastructure failure, and stdout protocol failure
- **THEN** they select exits 2, 130, 4, 5, and 6 respectively, while no CoordinateLookup case invents exit 3
