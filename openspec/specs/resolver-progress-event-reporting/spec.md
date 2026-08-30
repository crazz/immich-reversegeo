# Resolver Progress Event Reporting Specification

## Purpose

Makes processing-time administrative resolver and cache-wait progress observable through the existing run event stream without coupling reusable resolution or Lookup work to a processing session.

## Requirements

### Requirement: Processing resolver diagnostics use the admitted run session
When administrative resolution occurs for an asset in an accepted processing run, the system SHALL emit its existing country-check, country-result, source-cache status, readiness or unavailability, and cached-query messages through that run's existing event session. Those messages SHALL remain plain Information diagnostics associated with the same processing request; the resolver MUST NOT open, arm, or select a processing session of its own.

#### Scenario: Processing resolution reports on the existing run
- **WHEN** an accepted processing run resolves an asset after its run session is open
- **THEN** resolver diagnostics are emitted on that same session and no additional run-start or eligibility event is created

#### Scenario: Existing message presentation is preserved
- **WHEN** country, cache, readiness, unavailability, or query progress is emitted
- **THEN** it uses the existing plain message text and Information presentation without adding a warning or error prefix

### Requirement: Cache waits preserve source-specific correlated activity
For each Overture or GADM cache result indicating that the caller started or is awaiting a download, the system SHALL begin one activity with a non-empty opaque identity and SHALL end that same identity when the caller's wait exits. A started download and an awaited in-flight download SHALL retain their distinct source-specific labels. An already-ready cache SHALL emit its existing status/readiness diagnostics without an artificial activity. Activity completion MUST describe only the local wait lifetime and MUST NOT alter or imply ownership, cancellation, or success of the shared cache task.

#### Scenario: Download owner receives the source-specific label
- **WHEN** an Overture or GADM cache call returns StartedDownload during processing
- **THEN** its activity label identifies that source as downloading the named country cache and its end reuses the same opaque activity identity

#### Scenario: Concurrent waiter receives the waiting label
- **WHEN** another processing asset receives AwaitedExistingDownload for an in-flight source cache
- **THEN** it receives an independently identified waiting activity whose lifetime can overlap and end independently of the owner's activity

#### Scenario: Equal labels remain independently observable
- **WHEN** concurrent processing calls start activities with equal display labels but distinct identities
- **THEN** ending either identity leaves the other activity observable until its own end

#### Scenario: Ready cache has no artificial activity
- **WHEN** a required cache is already ready
- **THEN** the existing already-ready and readiness diagnostics are emitted without an activity-start event

### Requirement: Activity cleanup and outcome reporting preserve source semantics
Every cache activity whose start is accepted SHALL be ended through the session's non-cancelled cleanup path before the resolver call unwinds or continues beyond that wait. Readiness SHALL be reported only after a successful wait. Existing source failure behavior SHALL remain unchanged: ordinary GADM candidate failures report unavailability and continue eligible fallback, while propagating Overture failures remain propagating. Active caller cancellation, foreign cancellation-like failure, critical memory failure, and reporter failure SHALL retain the classifications established by earlier blocks. Reporter failure MUST NOT be converted into source unavailability or a handled asset failure, and no recursive report SHALL be attempted through a broken session.

#### Scenario: Successful wait ends before readiness
- **WHEN** a non-ready cache wait completes successfully
- **THEN** its matching activity ends and the existing source readiness diagnostic is emitted before the cached query

#### Scenario: GADM candidate is ordinarily unavailable
- **WHEN** an ordinary GADM cache failure occurs for one candidate
- **THEN** its matching activity ends, the existing unavailability diagnostic is emitted, and remaining candidate fallback proceeds according to current resolver behavior

#### Scenario: Active cancellation ends accepted activity
- **WHEN** the active caller token cancels a cache wait after its activity start was accepted
- **THEN** the matching activity end is attempted through non-cancelled cleanup and active cancellation propagates without a readiness diagnostic

#### Scenario: Foreign cancellation-like failure is not active cancellation
- **WHEN** a shared cache task produces cancellation while the current caller token is not requested
- **THEN** activity cleanup occurs and the failure follows that source's existing ordinary failure or unavailability path rather than cancelling the processing run

#### Scenario: Reporter fails during resolver reporting
- **WHEN** the run session fails before accepting a resolver diagnostic or activity operation
- **THEN** the infrastructure failure propagates without direct state repair, source-unavailability conversion, or a recursive event attempt

### Requirement: Reporting is invocation-scoped and optional outside processing
Administrative resolution without an explicitly supplied processing run session SHALL perform the same source selection, cache ensuring, querying, cancellation, and result construction without emitting processing events. A no-op session explicitly supplied by a processing caller SHALL preserve the same resolver behavior while accepting reporting without receiver side effects. The reusable resolver MUST NOT retain a run session between calls or obtain one from singleton state, and interactive Lookup work MUST NOT be associated with a processing request merely because a processing run is active.

#### Scenario: Resolver is called without reporting
- **WHEN** administrative resolution is invoked without a processing run session
- **THEN** it returns or fails according to the same resolver/cache behavior and emits no processing event

#### Scenario: No-op processing session is supplied
- **WHEN** a processing caller supplies a valid no-op run session
- **THEN** resolver reporting calls complete without receiver side effects and resolution behavior is unchanged

#### Scenario: Lookup overlaps a processing run
- **WHEN** interactive Lookup cache or query work overlaps an active processing run
- **THEN** Lookup keeps its independent status path and emits no event for that processing request

#### Scenario: Concurrent calls do not leak sessions
- **WHEN** the singleton resolver handles concurrent calls with different sessions or with no session
- **THEN** each diagnostic and activity is confined to the session explicitly supplied for that invocation
