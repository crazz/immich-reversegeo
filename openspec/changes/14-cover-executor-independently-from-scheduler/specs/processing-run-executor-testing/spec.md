## Purpose

Defines exhaustive, deterministic verification of one standalone processing run so later scheduling, coordination, and worker changes can preserve executor behavior without infrastructure-coupled tests.

## ADDED Requirements

### Requirement: Executor characterization is scheduler-free and reuses extraction coverage
The test suite SHALL construct the processing executor directly through its finalized collaborator seams and SHALL observe only its immutable run result, reporter session, collaborator calls, and persisted fake effects. It SHALL extend the block 11 extraction fixture and tests where they already establish a behavior, rather than adding a second empty-pass or representative mixed-pass test, and SHALL leave Phase 1 hosted lifecycle and mutable UI-state coverage unchanged.

#### Scenario: Standalone deterministic fixture
- **WHEN** any executor characterization scenario is run
- **THEN** it uses controlled in-memory fakes, fixed UTC time, and asynchronous gates without starting cron calculation, a scheduler or coordinator, a hosted service, a Blazor circuit, mutable Web UI state, PostgreSQL, SQLite, or real geodata/cache services

#### Scenario: Direct extraction test already covers the behavior
- **WHEN** block 11 already supplies a direct-executor zero-count, representative mixed-pass, principal cancellation/failure, reporter-fault, or extraction-equivalence assertion
- **THEN** block 14 retains or extends that assertion through the shared direct-executor fixture and adds only the missing orthogonal cases instead of duplicating the test at another scope

#### Scenario: Host and composition extraction tests remain outside the fixture
- **WHEN** block 11 already verifies host delegation or dependency-injection identity
- **THEN** those tests remain unchanged regression coverage outside the block 14 direct-executor fixture

### Requirement: Empty and non-empty run snapshots are characterized
The test suite SHALL prove the executor’s zero gate and the run-local lifetime of its skipped-ID and processing-configuration snapshots.

#### Scenario: Authoritative count is zero
- **WHEN** the eligibility count returns zero
- **THEN** the healthy session reports one completed empty result and no skipped-ID load, configuration read, batch fetch, resolver, airport lookup, persistence, or batch delay occurs

#### Scenario: Non-empty run takes one snapshot of each input
- **WHEN** a positive count leads to any number of batches
- **THEN** skipped IDs and processing configuration are each loaded exactly once and later fake changes to either source do not alter that run

#### Scenario: Suppressed asset is fetched
- **WHEN** a fetched asset ID is present in the run’s skipped-ID snapshot
- **THEN** the cursor still advances past that fetched row but the asset performs no resolution, persistence, disposition, or processed accounting

### Requirement: Keyset batching, cursor advancement, and delay are characterized
The test suite SHALL verify that a positive-count run is driven to completion by ordered keyset batch responses rather than by the informative eligibility total.

#### Scenario: Multiple non-empty batches reach an empty sentinel
- **WHEN** controlled batches are returned for the initial cursor and successive final-row cursors
- **THEN** the executor requests the initial cursor first, advances to each fetched batch’s final row before suppression, requests one final empty batch, and never repeats or skips a scripted cursor

#### Scenario: Delay follows each non-empty batch
- **WHEN** the configured batch delay is nonzero
- **THEN** one controllable delay occurs only after all work in each non-empty batch has reached a terminal boundary, including the last non-empty batch, and no delay follows the final empty batch

#### Scenario: Eligibility total differs from fetched work
- **WHEN** the reported eligibility total is lower or higher than the assets supplied by subsequent batches, including a positive total followed immediately by an empty batch
- **THEN** batch termination and result counts follow fetched terminal dispositions while the published eligibility value remains unchanged

### Requirement: Bounded parallel asset execution is characterized
The test suite SHALL verify the executor’s configured concurrency clamp and SHALL avoid imposing a global event order that concurrent assets do not guarantee.

#### Scenario: Configured parallelism is below the minimum
- **WHEN** maximum parallelism is zero or negative
- **THEN** gated asset work observes at most one active asset at a time

#### Scenario: Configured parallelism is within bounds
- **WHEN** maximum parallelism is between one and thirty-two
- **THEN** gated work can reach but never exceed that configured bound

#### Scenario: Configured parallelism exceeds the maximum
- **WHEN** maximum parallelism is greater than thirty-two
- **THEN** gated work never exceeds thirty-two active assets

#### Scenario: Concurrent completion order varies
- **WHEN** gates release assets in a different order from their batch order
- **THEN** every asset retains its own required causal ordering while the test makes no assertion about a global cross-asset event order

### Requirement: Every asset disposition and fallback branch is characterized
The test suite SHALL distinguish successful updates, deliberate skips, and handled per-asset failures and SHALL preserve the processing source and fallback order.

#### Scenario: Successful location update
- **WHEN** administrative resolution and fallback produce a writable country and city
- **THEN** the resolved location is persisted before one Updated disposition and the run result counts the asset as processed and updated

#### Scenario: No country or administrative match
- **WHEN** processing cannot produce the required administrative location
- **THEN** the asset ID is added to the skipped store before one Skipped disposition and the established warning event is emitted

#### Scenario: Country exists but no city fallback exists
- **WHEN** country resolution succeeds but city, state, and country-name fallback cannot produce a city
- **THEN** no skipped-store insert occurs, one Skipped disposition is reported, and logger-only diagnostics are not fabricated as reporter events

#### Scenario: Ordinary per-asset source or persistence operation fails
- **WHEN** the resolution source, airport source, update persistence, or skipped persistence raises a handled noncritical exception for one asset
- **THEN** that asset receives one Error diagnostic and one Failed disposition, other assets continue, and a healthy run can still complete

#### Scenario: Reporter fails from inside resolver reporting
- **WHEN** an awaited activity or log report fails while the resolver is using the run session
- **THEN** the reporter-origin infrastructure failure escapes the resolver and executor without conversion to source unavailability, an Error diagnostic, or a Failed asset disposition

#### Scenario: Airport lookup is disabled
- **WHEN** the run’s configuration snapshot disables airport lookup
- **THEN** administrative resolution is used without invoking the airport collaborator

#### Scenario: Containing airport overrides administrative city
- **WHEN** administrative resolution completes first and enabled airport lookup returns a geometry-containing airport city
- **THEN** the airport city overrides the administrative city before final fallback and persistence

#### Scenario: Non-containing airport does not override administrative city
- **WHEN** enabled airport lookup returns a nearby but non-containing airport and administrative resolution already supplied a city
- **THEN** the administrative city is retained

#### Scenario: Non-containing airport fills an absent administrative city
- **WHEN** enabled airport lookup returns a nearby non-containing airport and administrative resolution supplied no city
- **THEN** the airport city is used as the fallback candidate

#### Scenario: Administrative fallback order
- **WHEN** no airport override applies
- **THEN** the executor chooses the first available value in city, state, then country-name order exactly once before deciding update or skip

### Requirement: Persistence and partial effects are characterized
The test suite SHALL verify that PostgreSQL-location and skipped-store operations remain independent, nontransactional effects and that accepted terminal dispositions are not erased by later cancellation or failure.

#### Scenario: Persistence fails before disposition
- **WHEN** an update or skipped-store insert fails before returning successfully
- **THEN** no Updated or Skipped disposition is reported for that attempt and the asset follows the handled-failure or critical-failure taxonomy

#### Scenario: Active cancellation is observed during persistence
- **WHEN** a location update or skipped-store insert observes active-token cancellation before it returns success
- **THEN** the asset has no fake persistence effect or disposition, remains uncounted, and the run follows the Cancelled path with prior effects retained

#### Scenario: Cancellation follows successful persistence
- **WHEN** cancellation is requested after a fake persistence operation succeeds but before disposition publication is accepted
- **THEN** publication completes through the established non-cancelled committed path and the resulting terminal counts retain that effect

#### Scenario: Cancellation follows a committed non-persistence decision
- **WHEN** cancellation is requested after a no-city Skipped decision or handled Failed decision is committed but before its disposition publication is accepted
- **THEN** publication completes through the established non-cancelled committed path and the resulting terminal counts retain that disposition

#### Scenario: Later work cancels or fails
- **WHEN** one or more assets have committed terminal dispositions and a later boundary cancels or fatally fails the run
- **THEN** prior fake persistence effects and accepted counts remain visible with no rollback, retry, compensation, or cross-store transaction

#### Scenario: Reporter fails after persistence
- **WHEN** a reporter fault occurs after a fake persistence effect but before or during its disposition report
- **THEN** the persistence effect remains, the original reporter failure propagates under broken-session rules, and no compensating write or recursive terminal report occurs

### Requirement: Cancellation is characterized at meaningful executor boundaries
The test suite SHALL distinguish active-token cancellation from foreign cancellation-like exceptions and SHALL account only assets that crossed a terminal disposition boundary.

#### Scenario: Cancellation before eligibility succeeds
- **WHEN** the active token is cancelled before or during the authoritative count
- **THEN** the result is Cancelled with no eligibility event, no downstream collaborator work, and zero dispositions

#### Scenario: Cancellation before asset disposition
- **WHEN** the active token is cancelled during snapshot loading, batch retrieval, administrative resolution, airport lookup, or another pre-persistence asset boundary
- **THEN** the run is Cancelled, interrupted assets are uncounted, and already accepted earlier dispositions are retained

#### Scenario: Cancellation between batches or during delay
- **WHEN** the active token is cancelled after a completed batch or while its controlled delay is pending
- **THEN** no later batch begins, the run is Cancelled, and prior dispositions remain in the result

#### Scenario: Foreign cancellation-like exception
- **WHEN** a collaborator throws an OperationCanceledException while the executor’s active token is not cancelled
- **THEN** the exception follows the ordinary per-asset or pass-level failure classification for that boundary and does not produce a Cancelled outcome

### Requirement: Fatal, critical, repository, and reporter failures are characterized
The test suite SHALL verify failure taxonomy at both pass-level and per-asset executor boundaries.

#### Scenario: Pass-level repository, snapshot, or delay failure
- **WHEN** count, skipped snapshot, configuration snapshot, batch retrieval, or batch delay raises an unexpected non-cancellation exception
- **THEN** no later batch begins and the healthy reporter receives one Failed terminal result with message-only failure detail, retained prior counts, and no artificial per-asset FailedCount increment

#### Scenario: Per-asset repository failure
- **WHEN** a location update or skipped insert raises a handled ordinary exception
- **THEN** the asset is counted once as failed and the run continues to a Completed outcome unless another terminal condition occurs

#### Scenario: Critical out-of-memory failure in execution
- **WHEN** a controlled non-reporter execution collaborator raises OutOfMemoryException
- **THEN** it escapes per-asset handling, the healthy session finishes the run as Failed without converting it to a skip or ordinary asset failure, and earlier effects remain

#### Scenario: Reporter fails while opening the run and emitting start
- **WHEN** the combined session-open and RunStarted acceptance boundary fails, including with OutOfMemoryException
- **THEN** no usable session or terminal attempt exists, the original reporter infrastructure failure propagates, and no direct UI-state fallback or recursive reporter call occurs

#### Scenario: Reporter fails during a midstream report
- **WHEN** eligibility, log, activity, disposition, or cleanup reporting fails, including from inside resolver reporting or with OutOfMemoryException
- **THEN** the session is broken, the original reporter infrastructure failure propagates, and the executor makes no recursive terminal attempt through that session

#### Scenario: Reporter rejects finish
- **WHEN** RunFinished acceptance fails after the executor constructs and attempts its validated result
- **THEN** the original reporter infrastructure failure propagates, ExecuteAsync returns no result, no second terminal report is attempted, and prior fake effects remain

### Requirement: Terminal result and event invariants are characterized
For every healthy reporter path, the test suite SHALL verify the immutable request, result, accounting, timestamp, activity-cleanup, and terminal-event contracts without retesting the model constructors or Web adapter projection.

#### Scenario: Completed result includes mixed dispositions
- **WHEN** a run contains any mix of updated, skipped, and handled-failed assets
- **THEN** ProcessedCount equals UpdatedCount plus SkippedCount plus FailedCount, the exact request is retained, and handled failures do not prevent a Completed outcome

#### Scenario: Cancelled or failed result retains partial counts
- **WHEN** a run cancels or fails after prior dispositions
- **THEN** its exact request and prior coherent counts are retained, cancellation has no failure detail, fatal failure has nonblank message-only detail, and fatality adds no per-asset failure count

#### Scenario: Fixed timestamps and terminal order
- **WHEN** a fake UTC clock supplies execution start and end instants
- **THEN** the result uses those zero-offset instants with end not before start, outstanding activities end before one accepted RunFinished event, and the returned result exactly matches that event

#### Scenario: Eligibility and processed counts diverge
- **WHEN** the count snapshot includes suppressed assets or later data changes alter fetched work
- **THEN** eligibility remains the reported snapshot while terminal counts include only fetched assets that reached one disposition
