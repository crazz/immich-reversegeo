## Purpose

Defines cooperative caller-cancellation and critical-failure behavior across active geodata operations while preserving intentional source diagnostics, cache sharing, and malformed-data fallbacks.

## ADDED Requirements

### Requirement: Token-bearing geodata operations observe caller cancellation
An active geodata operation that accepts a cancellation token SHALL observe cancellation of that supplied token before returning cached, bundled, fallback, diagnostic, unavailable, null, non-match, or successful output.

#### Scenario: Token is already cancelled on entry
- **WHEN** a token-bearing geodata operation is invoked with an already-cancelled token
- **THEN** it throws an `OperationCanceledException` associated with caller cancellation instead of returning an otherwise available cached or bundled result

#### Scenario: Token is cancelled before result publication
- **WHEN** caller cancellation is observed after synchronous source work returns but before a cache file or successful result is published
- **THEN** the operation throws cancellation, publishes no new cache or successful result, and performs its existing temporary-artifact cleanup

### Requirement: Cancellation identity is preserved at catch boundaries
The system SHALL classify an `OperationCanceledException` as caller or run cancellation only when the token governing that current operation is requested; broad diagnostic and fallback handlers SHALL rethrow active caller cancellation before normalization.

#### Scenario: Active caller cancellation reaches the run boundary
- **WHEN** geodata resolution throws because the processing run token is requested
- **THEN** the run emits its existing cancellation signal and terminal cleanup without recording an ordinary per-asset error, skipped asset, or location write for that cancelled asset

#### Scenario: Unrelated operation cancellation is not caller cancellation
- **WHEN** a geodata dependency throws `OperationCanceledException` while the token governing the current caller is not requested
- **THEN** the condition is handled according to that dependency's ordinary failure or source-unavailability contract and is not labelled as cancellation of the current caller or run

### Requirement: Shared cache cancellation preserves task ownership
Country-cache operations SHALL retain first-owner token ownership and exact-task cleanup while distinguishing owner-task cancellation from cancellation of an individual waiter.

#### Scenario: Non-owner waiter cancels
- **WHEN** a non-owner caller cancels its wait on an active shared country-cache task
- **THEN** only that caller's wait is cancelled, and the shared task remains active, joinable, and owned by its original token

#### Scenario: Shared owner task is cancelled for a live waiter
- **WHEN** the owner token cancels the shared task while another waiter's own token remains active
- **THEN** the live waiter observes ordinary source unavailability or configured source fallback rather than cancellation of its own operation

### Requirement: Cancellation of synchronous native work is cooperative
The system SHALL check cancellation before and after synchronous native regions, at practical managed row, layer, or candidate boundaries, and immediately before cache publication or success return; it SHALL NOT claim to preempt a native call already executing.

#### Scenario: Cancellation occurs during an executing native call
- **WHEN** cancellation is requested while a synchronous DuckDB, SQLite, filesystem, or geometry operation is already executing
- **THEN** the operation observes cancellation at its next managed checkpoint without guaranteeing immediate interruption of that native call

### Requirement: Critical memory failures remain failures
The system SHALL propagate `OutOfMemoryException` unchanged through active geodata geometry, lookup, release discovery, cache status/readiness/validation, export, metadata, resolver, and Web helper boundaries.

#### Scenario: Memory failure reaches a diagnostic catch boundary
- **WHEN** a controlled geodata dependency throws `OutOfMemoryException`
- **THEN** the exception remains observable as a failure and is not converted into a miss, diagnostic result, documented-release fallback, false readiness, zero status, empty metadata, null, or territory fallback

### Requirement: Intended ordinary diagnostics and fallbacks remain available
Non-cancellation, non-memory-exhaustion operational failures SHALL retain the existing source-specific diagnostic and fallback behavior unless a malformed-data requirement states that the artifact must fail.

#### Scenario: Ordinary source operation fails
- **WHEN** an HTTP, database, filesystem, or release-discovery operation fails without caller cancellation or memory exhaustion
- **THEN** the existing diagnostic, documented-release fallback, cache-unavailable result, or territory fallback for that boundary remains available

### Requirement: Malformed-data boundaries remain source-specific
Only recognized per-candidate malformed WKB or topology failures at tolerant containment boundaries SHALL be converted to geometry false; source artifacts and non-data critical failures SHALL retain their existing failure behavior.

#### Scenario: Malformed Overture candidate geometry reaches tolerant containment
- **WHEN** cached-division or bundled-infrastructure candidate geometry has a recognized malformed WKB or topology failure
- **THEN** that candidate's geometry containment is false without masking caller cancellation or critical memory failure

#### Scenario: Malformed bundled-country artifact
- **WHEN** a bundled-country geometry or index artifact is malformed
- **THEN** artifact loading fails rather than converting the corruption into an ordinary country non-match

#### Scenario: Malformed GADM cached candidate geometry
- **WHEN** cached GADM candidate WKB has a recognized malformed-data failure
- **THEN** geometry containment is false while the existing bounding-box fallback and candidate ranking remain unchanged

#### Scenario: Malformed GADM source artifact during cache construction
- **WHEN** a GADM source GeoPackage header, schema, or geometry blob is malformed during export
- **THEN** cache construction fails, temporary artifacts are cleaned up, and any previously published cache remains untouched
