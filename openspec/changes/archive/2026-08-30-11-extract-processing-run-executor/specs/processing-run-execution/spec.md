## Purpose

Defines execution of one admitted processing request to a terminal result without requiring a scheduler or WebUI state, while retaining the established processing, persistence, reporting, and cancellation behavior.

## ADDED Requirements

### Requirement: Execution has a UI- and scheduler-independent contract
The system SHALL execute one valid processing request using a supplied processing-event reporter and active cancellation token and SHALL return a matching validated terminal result when the reporting session remains healthy. Execution SHALL open exactly one run-scoped reporting session at execution entry, before authoritative eligibility counting, and every event and the returned result SHALL retain the request identity.

#### Scenario: An admitted request executes in-process
- **WHEN** the existing manual or scheduled control plane delegates an admitted request
- **THEN** execution uses the supplied reporter and returns the matching result without reading or mutating WebUI state

#### Scenario: Eligibility fails before it is known
- **WHEN** authoritative eligibility counting is cancelled by the active token or fails unexpectedly
- **THEN** the session finishes with the corresponding cancelled or failed result without fabricating eligibility or asset progress

#### Scenario: Reporter acceptance fails
- **WHEN** the reporter session fails before accepting a required event
- **THEN** execution propagates that infrastructure failure, performs no recursive reporting through the broken session, and does not fall back to direct WebUI mutation

### Requirement: Execution owns the authoritative pass pipeline
Execution SHALL own the authoritative eligibility count, zero-work short circuit, skipped-ID snapshot, non-empty-run processing configuration snapshot, keyset batch enumeration, suppression filtering, bounded parallel asset evaluation, inter-batch delay, and terminal accounting. The eligibility count SHALL retain the current database predicate. A zero count SHALL finish without reading skipped IDs or configuration and without fetching, resolving, persisting, or delaying. Web compatibility projection SHALL retain `Processed`/`ProcessedThisRun` as session `UpdatedCount` (successful writes); aggregate `ProcessedCount` SHALL NOT replace that UI value. Previously suppressed IDs SHALL remain absent from all disposition counts even though they may be present in the eligibility total and fetched batches.

#### Scenario: No eligible assets exist
- **WHEN** authoritative eligibility counting returns zero
- **THEN** execution reports eligibility zero and completed zero accounting without invoking non-empty pipeline dependencies

#### Scenario: A fetched identifier was previously suppressed
- **WHEN** a batch contains an identifier from the run-start skipped-ID snapshot
- **THEN** execution advances enumeration normally but performs no resolution, persistence, or disposition accounting for that asset

#### Scenario: Processing settings change during a run
- **WHEN** a non-empty pass has obtained its processing configuration and settings are later changed
- **THEN** all batches in that pass retain the obtained batch size, delay, parallelism, source, airport, and logging settings

### Requirement: Per-asset resolution order and outcomes remain compatible
For each evaluated asset, the system SHALL perform administrative country/area resolution before optional airport infrastructure lookup. Airport geometry containment SHALL override an administrative city; otherwise an airport candidate SHALL be used only when no administrative city exists. The established city fallback SHALL then prefer city, state, and country in that order. The system SHALL not redesign source preference, geometry, cache preparation, or work eligibility.

A successful location write SHALL produce one Updated disposition. Existing reachable no-country and no-administrative-match decisions SHALL produce one Skipped disposition with their established skipped-store and diagnostic differences. After `WithFallbackCity`, every matched `GeoResult` SHALL have a non-null city selected from city, state, then country; the retained logger-only no-city conditional is therefore an unreachable compatibility guard and SHALL NOT be claimed as an executable Skipped disposition. A handled per-asset exception SHALL produce one Failed disposition and SHALL not fail an otherwise completed pass. An asset interrupted before a disposition SHALL remain uncounted.

#### Scenario: Airport geometry contains the asset
- **WHEN** administrative resolution succeeds and enabled airport lookup returns a geometry-containing candidate
- **THEN** the airport name replaces the administrative city before the established fallback and write checks

#### Scenario: Airport is only near the asset
- **WHEN** enabled airport lookup returns a non-containing candidate and administrative resolution already supplied a city
- **THEN** the administrative city remains selected

#### Scenario: Asset has no country match
- **WHEN** administrative resolution returns no country
- **THEN** the identifier is persisted in the skipped store before one Skipped disposition is committed

#### Scenario: Resolved location is writable
- **WHEN** the final location satisfies the current country-and-city write rule and the Immich update succeeds
- **THEN** one Updated disposition is committed after the write

#### Scenario: Matched location falls back to state
- **WHEN** a matched result has no city after airport selection but has a state
- **THEN** fallback selects that state as the city before the write, one Updated disposition follows a successful write, and no skipped-store insert, logger-only no-city warning, or Skipped disposition occurs

#### Scenario: Matched location falls back to country
- **WHEN** a matched result has neither city nor state after airport selection
- **THEN** fallback selects its country as the city before the write, one Updated disposition follows a successful write, and no skipped-store insert, logger-only no-city warning, or Skipped disposition occurs

#### Scenario: Asset operation fails but the pass continues
- **WHEN** an ordinary or unrelated cancellation-like exception is handled at the per-asset boundary
- **THEN** one Failed disposition and its established diagnostic are emitted and other assets may continue

### Requirement: Persistence retains independent write semantics
The system SHALL preserve the existing independent persistence boundaries: each Immich location update is its own repository operation and each skipped-ID insert is its own skipped-store operation. Execution SHALL NOT add a run-wide, batch-wide, per-asset cross-store, or distributed transaction, retry, compensation, or rollback. A disposition SHALL be committed to reporting only after its required persistence operation succeeds. A later cancellation or fatal failure SHALL retain already completed writes, skipped inserts, and committed disposition counts.

#### Scenario: Cancellation follows a successful write
- **WHEN** cancellation is requested after an Immich update succeeds but before progress publication completes
- **THEN** the Updated disposition is published through the non-cancelled committed-disposition path and remains in the cancelled result

#### Scenario: Skipped persistence fails
- **WHEN** the skipped-ID insert required by a no-country or no-admin-match branch fails
- **THEN** no Skipped disposition is committed for that branch and the exception follows the established handled per-asset failure path

#### Scenario: A later asset fails fatally
- **WHEN** a pass-level failure occurs after earlier assets completed persistence
- **THEN** the failed result retains earlier committed counts and no earlier persistence is rolled back

### Requirement: Cancellation and exception boundaries retain the established taxonomy
Active-token cancellation SHALL terminate the pass as Cancelled and SHALL not create a per-asset failure. Cancellation-like exceptions not attributable to the active token SHALL follow the established ordinary dependency or failure path. Critical exceptions that blocks 6 and 10 require to escape local fallbacks SHALL continue to escape to the pass boundary and produce a Failed result. Cleanup-required activity ends, committed disposition publication, and terminal reporting SHALL not use an already-cancelled run token.

#### Scenario: Active cancellation interrupts parallel processing
- **WHEN** the active run token is cancelled before one or more assets reach a terminal disposition
- **THEN** parallel enumeration stops cooperatively, interrupted assets remain uncounted, outstanding activities close, and the result is Cancelled with prior committed counts

#### Scenario: Pass-level failure occurs
- **WHEN** an unexpected exception escapes eligibility, batching, configuration, delay, or asset processing
- **THEN** execution logs the failure through its ordinary logger boundary, finishes with a Failed result containing message-only detail, and does not add a pass-level per-asset failure count

### Requirement: Control-plane ownership remains outside execution
Execution SHALL NOT calculate or wait for schedules, acquire or release the run admission lock, create run requests, arm WebUI projection, mark pending state, own or cancel a manual cancellation source, decide duplicate admission, emit startup/schedule/contention logs, expose dashboard commands, or launch another process. The existing host SHALL retain those responsibilities and delegate only after admission.

#### Scenario: Duplicate trigger is rejected
- **WHEN** manual or scheduled admission rejects a trigger while another pass owns the lock
- **THEN** the executor is not invoked and no request, reporter session, or result is created

#### Scenario: Manual cancellation is requested
- **WHEN** the current Web host cancels its manual run source
- **THEN** execution observes only the token supplied by the host and owns no cancellation source or UI command

### Requirement: Execution is deterministic and lifetime-safe
Production composition SHALL use one singleton-compatible stateless executor and SHALL alias executor-facing collaborator abstractions to the existing production singleton instances rather than creating duplicate resolver, repository, reporter-adapter, or hosted-service owners. Invocation-specific mutable data SHALL remain local to one execution. Tests SHALL be able to substitute count, configuration, skipped store, batch, resolver, infrastructure, persistence, reporter, and UTC-time operations and to gate concurrent operations without sleeps, live databases, geodata, scheduler timing, or Blazor hosting.

#### Scenario: Concurrent asset completions are reordered
- **WHEN** deterministic test gates release parallel assets in a chosen order
- **THEN** accounting remains coherent and each asset receives at most one terminal disposition independent of completion order

#### Scenario: Production services are resolved
- **WHEN** the Web composition root resolves the executor and its collaborators
- **THEN** every abstraction directly implemented by a singleton resolves to that exact object, any thin adapter's interfaces resolve to its one adapter object without claiming identity with the wrapped service, and the hosted service resolves the concrete `ProcessingBackgroundService` singleton
