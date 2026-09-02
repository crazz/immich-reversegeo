## Purpose

Separates shared infrastructure, the Web control plane, and internal-worker execution dependencies so each parsed application role builds only its intended service graph while preserving established runtime behavior.

## ADDED Requirements

### Requirement: Role selection precedes role-specific host composition
The system SHALL complete block 18 role selection before application-owned environment/path resolution, dependency registration, filesystem initialization, or creation/building of a role-specific host. For roles owned by this change, it SHALL apply exactly one of the Web or internal-worker composition paths. Selection failure and the reserved RunOnce boundary SHALL initialize neither path. The internal-worker path SHALL remain usable by a later Generic Host without requiring an ASP.NET Web host.

#### Scenario: Existing Web invocation
- **WHEN** role parsing selects the existing Web behavior
- **THEN** the system applies the shared/core and Web control-plane registrations before building the Web host

#### Scenario: Internal worker invocation
- **WHEN** the exact private invocation selects InternalWorker
- **THEN** the system applies the shared/core and internal-worker registrations without creating or building a Web host

#### Scenario: Unavailable non-Web boundary
- **WHEN** selection fails or returns the reserved RunOnce value before its owning change is available
- **THEN** neither composition root, host builder, dependency injection, filesystem initialization, nor application logging is initialized

### Requirement: Shared runtime inputs retain compatible semantics
Both role graphs SHALL derive configuration, environment-sensitive directories, bundled-data paths, logging, and database access from the same rules. Configuration secrets SHALL remain sourced from environment variables and SHALL NOT be persisted to settings storage or emitted by composition logging. Each built host SHALL use one shared database data-source instance for every repository and execution alias in that host.

#### Scenario: Production storage paths
- **WHEN** no directory overrides are present in a production environment
- **THEN** settings use the config root, mutable geodata and caches use the data root, and bundled artifacts use the application bundled-data root

#### Scenario: Directory overrides
- **WHEN** data or configuration directory environment overrides are present
- **THEN** every registration in the selected role observes the same effective roots and preserves the separation between configuration secrets/settings and regenerable data

#### Scenario: Database services resolve
- **WHEN** multiple repositories in one role resolve database access
- **THEN** they receive the same singleton database data source configured from the established environment-backed database settings

### Requirement: Web control-plane composition remains compatible
The Web role SHALL retain Razor/Blazor services, Web middleware dependencies, Data Protection, configuration and UI-facing services, lightweight country identity/profile catalogs, processing state, scheduling, and the finalized local coordinator/control-plane contracts. It SHALL preserve one concrete scheduler instance registered as the hosted scheduler and one concrete coordinator instance for all of its finalized aliases. Until their later migration blocks, the Web role SHALL also retain the existing Lookup, Data, reset, and in-process processing dependencies, including heavy geodata and cache services.

#### Scenario: Web hosted-service identity
- **WHEN** Web dependencies resolve the concrete scheduler and enumerate hosted services
- **THEN** concrete resolution and the hosted-service enumeration return the same scheduler object and hosted-service registration creates no duplicate instance

#### Scenario: Web coordinator aliases
- **WHEN** the finalized Dashboard, scheduler-start, reporter/control-plane, or host-lifecycle contracts resolve the local coordinator
- **THEN** every applicable alias resolves the one concrete coordinator singleton established by the applied Phase 2 API

#### Scenario: Transitional Web pages resolve
- **WHEN** the Web Lookup, Data, reset, Settings, Dashboard, or city-profile surfaces resolve their current dependencies
- **THEN** those dependencies remain available with their established singleton lifetimes and behavior

### Requirement: Internal-worker composition contains execution dependencies only
The internal-worker role SHALL include the finalized processing executor and its identity-preserving aliases, configuration and storage inputs, database and skipped-asset repositories, lightweight country identity/profile data, administrative resolution, Overture and GADM geodata/cache services, and any DI-backed worker-protocol collaborators required by the finalized execution API; dependency-free protocol codecs remain unregistered. It SHALL NOT register Kestrel or other Web-server services, Razor/Blazor components, antiforgery, static-asset or endpoint services, Data Protection, Web processing state, the Web scheduler, or the local Web coordinator.

#### Scenario: Worker execution graph resolves
- **WHEN** a composition test builds the internal-worker service provider and resolves the finalized executor
- **THEN** its database, skipped-store, configuration, geodata, cache, identity, logging, and protocol dependencies resolve from that worker graph

#### Scenario: Worker Web boundary
- **WHEN** the internal-worker descriptors and provider are inspected
- **THEN** no Web server, Blazor, scheduler, coordinator, Web state, or Data Protection registration is present or constructible

### Requirement: Stateful and initialization behavior is not duplicated by composition
Role extraction SHALL preserve the established singleton lifetimes and factory behavior of stateful repositories, caches, resolvers, catalogs, reporter adapters, and execution services. Composition SHALL NOT synchronously force country-index construction, replace an established asynchronous country-index initialization task with blocking resolution, move skipped-database startup ownership, or duplicate a stateful instance behind an interface or hosted-service alias. DuckDB HTTP, Azure, Linux curl-transport, and spatial initialization SHALL continue through the centralized bootstrap used by current geodata operations.

#### Scenario: Country-index service registration
- **WHEN** either transitional Web composition or worker composition is built without invoking geodata work
- **THEN** registration and provider construction do not eagerly build or synchronously block on the bundled country index

#### Scenario: Skipped storage startup
- **WHEN** the Web scheduler starts or a later worker execution startup invokes the finalized initialization owner
- **THEN** skipped storage initialization occurs at the established asynchronous lifecycle point rather than inside a synchronous DI factory

#### Scenario: DuckDB-backed operation
- **WHEN** a selected role later performs an Overture operation requiring DuckDB extensions
- **THEN** it uses the existing centralized extension bootstrap, including the Linux Azure curl transport setting

### Requirement: Composition boundaries are verified independently of worker execution
Tests SHALL prove service presence, absence, lifetime, alias identity, and role-selection ordering using service descriptors and disposable test providers or host fixtures. These tests SHALL NOT require Kestrel binding, a Blazor circuit, live PostgreSQL, live downloads, country-index materialization, stdin/stdout protocol loops, or child processes.

#### Scenario: Composition regression
- **WHEN** a role registration changes
- **THEN** focused tests fail if required services disappear, forbidden services enter the worker graph, or singleton/hosted aliases stop sharing identity
