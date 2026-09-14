## Purpose

Defines the backward-compatible default production composition that hosts the Immich ReverseGeo Web control plane while isolating authoritative processing in temporary child workers.

## ADDED Requirements

### Requirement: Standard mode starts the compatible Web application
When block 40's finalized deployment-mode contract resolves `standard`, including its unset default, the system SHALL build and start the production ASP.NET Core/Kestrel Web host, expose the existing Razor/Blazor UI and routes, and preserve the existing middleware and endpoint behavior. Standard composition MUST consume the resolved mode and MUST NOT re-parse, persist, or independently default the deployment-mode input.

#### Scenario: Unset mode resolves to Standard Web hosting
- **WHEN** block 40 resolves an unset deployment-mode setting to `standard`
- **THEN** the application starts the same Web UI and routes as an explicit `standard` selection

#### Scenario: Explicit Standard selection
- **WHEN** block 40 supplies the resolved `standard` value
- **THEN** exactly one Standard Web composition is built without a second deployment-mode interpretation

### Requirement: Standard mode enables internal scheduling and manual control
Standard composition SHALL register and activate the finalized internal scheduler, singleton processing coordinator, scheduled work detector, child-worker dispatch boundary, and host-lifecycle owner. Manual processing SHALL remain available through the existing Web control surface. A due scheduled occurrence SHALL use the finalized lightweight detector before child resolution, while a manual request MUST NOT use that scheduled-only detector.

#### Scenario: Manual processing is accepted
- **WHEN** the Standard Web control plane admits a manual processing request
- **THEN** it dispatches exactly one child-processing request through the coordinator without invoking the scheduled detector

#### Scenario: Scheduled detector reports work
- **WHEN** the Standard scheduler admits a due occurrence and the detector reports eligible work
- **THEN** the coordinator dispatches exactly one child-processing request after that decision

#### Scenario: Scheduled detector reports no work
- **WHEN** the Standard scheduler admits a due occurrence and the detector reports no eligible work
- **THEN** the occurrence completes through the established local zero-work lifecycle without resolving or launching a child worker

#### Scenario: Local processing contention
- **WHEN** a manual or scheduled request arrives while the Standard coordinator owns another run
- **THEN** the later request follows the established local contention outcome and no second heavy child is started

### Requirement: Standard processing is child-only
Every accepted manual request and every detector-positive scheduled request in production Standard mode SHALL perform authoritative processing only in the private internal-worker role. The child SHALL be launched from the same application assembly and deployment image as Web. Standard Web composition MUST NOT register or invoke the authoritative processing executor, expose an in-process backend, fall back after child failure, retry automatically, replay a request, or launch a replacement child.

#### Scenario: Standard service graphs are inspected
- **WHEN** production Standard Web and private internal-worker compositions are inspected independently
- **THEN** Web contains one child dispatch path but no authoritative executor or in-process backend, while the private worker contains the executor and no Web host

#### Scenario: Child execution fails
- **WHEN** child startup, readiness, protocol handling, execution, cancellation escalation, or cleanup fails
- **THEN** the established failure outcome remains authoritative and no processing occurs in the Web process

#### Scenario: Private worker command is built
- **WHEN** Standard dispatches an accepted processing request
- **THEN** the command selects the internal-worker role from the same packaged application rather than a public deployment mode or separate worker artifact

### Requirement: Standard preserves current-phase Web features
Standard composition SHALL retain the existing Lookup and Data routes and every current-phase dependency they require, including transitional heavy Web services, until their separately planned migration. Those retained services MUST NOT become reachable from the Web processing coordinator, scheduler, detector, or child-dispatch roots.

#### Scenario: Lookup and Data are used in Standard
- **WHEN** an operator opens or runs an existing Lookup or Data operation
- **THEN** its current-phase dependencies resolve with established behavior while asset processing remains child-only

#### Scenario: Processing graph is exercised
- **WHEN** a manual, detector-empty scheduled, or detector-positive scheduled route executes in Standard Web
- **THEN** no authoritative executor or processing geodata service is resolved or invoked in Web

### Requirement: Standard preserves network and storage compatibility
Standard mode SHALL retain the existing listener configuration: production containers use port 8080 and local development uses port 5122. It SHALL retain separate configuration and data roots, with production defaults of `/config` and `/data`, existing environment overrides, and independent volume semantics. The change MUST NOT persist deployment mode, move secrets into settings storage, or introduce a settings/data migration.

#### Scenario: Existing production deployment omits mode
- **WHEN** an existing container deployment starts without a deployment-mode setting and uses the established mounts
- **THEN** Standard listens through the existing port-8080 configuration and continues using separate `/config` and `/data` roots

#### Scenario: Local development starts Standard
- **WHEN** Standard is started through the existing local development profile
- **THEN** it retains the existing port-5122 listener configuration and development path rules

### Requirement: Standard validates startup prerequisites without doing work
Before the Standard Web host accepts requests, the system SHALL validate the finalized child-launch prerequisites and compatible host-shutdown budget. An unavailable or invalid private worker launch path MUST fail startup with an actionable diagnostic and MUST NOT start processing, fall back to in-Web execution, or materialize the worker's heavy execution graph. Invalid deployment-mode input remains governed by block 40's earlier failure contract.

#### Scenario: Private worker prerequisite is unavailable
- **WHEN** Standard startup cannot locate or compose the finalized same-assembly internal-worker launch path
- **THEN** startup fails before the Web application accepts requests and no worker or in-process processing pass starts

#### Scenario: Standard prerequisites are valid
- **WHEN** the resolved Standard composition passes launch and shutdown validation
- **THEN** the Web host may start without launching a child or constructing worker-only geodata and execution services

### Requirement: Standard shutdown owns active child cleanup
When Standard Web shutdown begins, the system SHALL atomically close local admission, reject later requests, and join cleanup for any admitted or start-racing child. It SHALL reuse the finalized bounded worker cancellation, process-tree termination, stream-drain, disposal, and exact-handle cleanup policy and MUST NOT report clean shutdown while it still owns a live child.

#### Scenario: Shutdown occurs during child execution
- **WHEN** the Standard Web host begins stopping while a child is starting or running
- **THEN** no later run is admitted and shutdown cancels or joins that exact child through process and stream finality

#### Scenario: Shutdown occurs while idle
- **WHEN** the Standard Web host begins stopping with no active run
- **THEN** admission closes and shutdown completes without launching a worker

### Requirement: Standard composition and behavior are regression tested
The automated test suite SHALL include Standard-specific composition assertions and a hermetic behavioral smoke. Composition coverage SHALL prove required Web/Kestrel/UI, scheduler, coordinator, detector, child-dispatch, lifecycle, Lookup, and Data presence; singleton/hosted identity where applicable; private worker isolation; and authoritative executor/in-process-backend absence from Web. The smoke SHALL prove default startup, manual dispatch, scheduled positive and empty gating, one-local-worker contention, startup failure, and active-child shutdown without live PostgreSQL, geodata, downloads, Docker, fixed-port binding, or real heavy processing.

#### Scenario: Standard composition regresses
- **WHEN** a required Standard registration disappears, a duplicate scheduler or child path appears, or a forbidden processing executor becomes reachable from Web
- **THEN** focused composition tests fail before external resources or a fixed HTTP listener are used

#### Scenario: Standard behavioral smoke runs
- **WHEN** the smoke uses fake time, detector, child boundary, and lifecycle observations
- **THEN** it deterministically demonstrates the Standard trigger, gating, contention, failure, and shutdown contract with at most one simulated active child
