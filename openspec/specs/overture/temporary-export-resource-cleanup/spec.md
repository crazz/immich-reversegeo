# Temporary Export Resource Cleanup Specification

## Purpose

Ensures one-shot Overture division export databases release path-specific SQLite resources after faults and remain safely retryable without changing published cache behavior.

## Requirements

### Requirement: Temporary exports do not enroll path-specific resources in reusable pools
The system SHALL create every GUID-specific temporary Overture division export database with connection pooling disabled.

#### Scenario: Repeated post-open failures use unique temporary paths
- **WHEN** repeated exports fail after opening different temporary databases
- **THEN** each opened connection reports pooling disabled through the provider-supported connection configuration
- **AND** each failed temporary file is removed
- **AND** no final country cache is published by a failed attempt

### Requirement: Temporary export failure preserves cache recovery
When a temporary Overture export fails, the system SHALL preserve the ability to retry the same country without reusing or publishing the failed temporary output.

#### Scenario: Retry after transient export failure
- **WHEN** a post-open export failure occurs and its injected cause is removed
- **THEN** a later export for the same country uses a new temporary path
- **AND** the successful output is validated and published as the country cache

### Requirement: Successful export behavior remains compatible
The system SHALL preserve the existing schema, release metadata, row validation, and atomic temporary-to-final publication behavior for successful Overture division exports.

#### Scenario: Successful temporary export
- **WHEN** an export writes at least one valid division row without a fault
- **THEN** the validated temporary database is moved to the final country-cache path
- **AND** the cache is reported ready
