## Purpose

Defines a Web-hosted deployment policy that keeps interactive and manual control available while guaranteeing that this process never originates automatic scheduled processing.

## ADDED Requirements

### Requirement: Web-only starts the compatible Web application
When block 40's finalized deployment-mode contract resolves `web-only`, the system SHALL build and start the production ASP.NET Core/Kestrel Web host, expose the existing Razor/Blazor UI and routes, and preserve the existing middleware and endpoint behavior. Web-only composition MUST consume the immutable resolved mode and MUST NOT re-parse, persist, normalize, or independently default `IMMICH_REVERSEGEO_MODE`.

#### Scenario: Web-only selection starts one Web host
- **WHEN** the finalized startup decision supplies the resolved `web-only` value
- **THEN** exactly one Web-only Web composition starts with the existing UI and routes and without a second deployment-mode interpretation

### Requirement: Web-only structurally excludes internal scheduling
Web-only composition MUST NOT register or activate the internal scheduler hosted service, schedule-calculation loop, cron wait, disabled/invalid retry wait, due-occurrence callback, or scheduled-detector dispatch path. This prohibition SHALL apply regardless of the persisted enabled flag or cron expression, and entering Web-only MUST NOT mutate, normalize, clear, or save those settings.

#### Scenario: Enabled valid schedule is persisted
- **WHEN** Web-only starts with `Schedule.Enabled` true and a valid due cron expression
- **THEN** no scheduler lifecycle, cron wait, scheduled detector call, automatic admission, or automatic child launch occurs and the persisted values remain unchanged

#### Scenario: Disabled or invalid schedule is persisted
- **WHEN** Web-only starts with a disabled, invalid, empty, or otherwise non-runnable saved schedule
- **THEN** the host performs no scheduler retry wait or schedule-specific status transition and leaves the persisted values unchanged

### Requirement: Manual asset processing remains child-only
The existing manual processing control SHALL remain available in Web-only. An admitted request SHALL pass directly through the finalized process-local coordinator and child-worker launch boundary without using the scheduled detector. Web-only Web MUST NOT register or invoke the authoritative processing executor, expose an in-process processing fallback, retry or replay a failed child, or launch a replacement child. Existing local contention and worker-side cross-process exclusion outcomes SHALL remain authoritative.

#### Scenario: Manual processing is admitted
- **WHEN** an operator starts manual processing while the Web-only coordinator is available
- **THEN** exactly one private child-processing request is dispatched without invoking any scheduled detector

#### Scenario: Manual processing contends locally
- **WHEN** an operator starts manual processing while the Web-only coordinator owns another run
- **THEN** the established busy outcome is shown and no second child or in-Web processing path starts

#### Scenario: Manual child fails
- **WHEN** child startup, protocol handling, execution, cancellation, or cleanup fails
- **THEN** the established failure is final for that request and Web-only performs no in-process fallback, retry, replay, or replacement launch

### Requirement: Lookup and Data remain available through an explicit transition
Block 42 SHALL keep the existing Lookup and Data routes and operations available without a Web-only-specific feature gate. Until blocks 47–55 deliver their ordered worker-job, arbitration, page-routing, cache-operation, inventory, maintenance, and composition cutovers, Web-only SHALL retain the same current-phase dependencies and behavior as Standard and MUST NOT claim that those operations are worker-backed or that the whole Web host is geodata-free.

After the applicable Phase 7 cutovers are implemented, Web-only SHALL consume the same common control-plane registrations as Standard: heavy coordinate Lookup and cache download/export/refresh operations launch admitted worker jobs; lightweight inventory remains in Web; cache deletion and database maintenance follow their finalized block 52/54 coordination decisions. Block 42 MUST NOT pre-implement, partially emulate, or independently feature-flag that target state.

#### Scenario: Web-only is delivered before Phase 7 cutover
- **WHEN** an operator uses Lookup or Data after block 42 but before its applicable blocks 47–55 are complete
- **THEN** the feature remains available with current-phase behavior and is neither hidden nor described as worker-backed

#### Scenario: Common Web features are cut over in Phase 7
- **WHEN** the applicable Phase 7 worker-job and control-plane changes are later complete
- **THEN** Web-only receives those shared registrations without restoring internal scheduling or creating a Web-only-specific heavy-work path

### Requirement: Schedule policy and manual status are truthful
The schedule editor SHALL remain visible and editable in Web-only and SHALL state that this deployment mode disables internal scheduling even when the saved enabled flag is true. Existing manual processing pending, progress, terminal, failure, and cancellation status SHALL continue to follow the finalized child-event bridge. Block 42 MUST NOT introduce the resolved-mode and safe ProcessAssets lifecycle surface owned by block 44, and it assigns no PID, run/job identity, generic active-job card, or non-ProcessAssets page state to that change.

#### Scenario: Operator views an enabled schedule
- **WHEN** the saved schedule is enabled while the process runs in Web-only
- **THEN** the UI shows the saved setting together with an unambiguous mode-disabled notice and does not present an automatic run as pending or scheduled

#### Scenario: Manual child reports progress
- **WHEN** a Web-only manual child emits established lifecycle and progress events
- **THEN** the existing processing status reaches the same truthful pending, active, and terminal outcomes as Standard manual processing

### Requirement: Startup and shutdown preserve child lifecycle guarantees
Before accepting requests, Web-only SHALL validate the finalized same-image child-launch prerequisites and compatible host-shutdown budget required by manual processing. Invalid prerequisites MUST fail startup actionably without launching work, resolving the authoritative executor, or falling back in-Web. During shutdown, Web-only SHALL atomically close coordinator admission and join cleanup of any admitted or start-racing child through the finalized cancellation, process-tree termination, stream-drain, disposal, and exact-ownership rules. Absence of a scheduler MUST NOT weaken or duplicate lifecycle ownership.

#### Scenario: Manual child prerequisite is unavailable
- **WHEN** Web-only startup cannot validate the finalized private worker launch path or shutdown budget
- **THEN** startup fails before the UI accepts requests and no processing or in-Web fallback starts

#### Scenario: Shutdown begins during manual processing
- **WHEN** the Web-only host begins stopping while a manual child is starting or running
- **THEN** later admission is rejected and shutdown joins cancellation and cleanup of that exact child before reporting clean completion

#### Scenario: Shutdown begins while idle
- **WHEN** the Web-only host begins stopping with no admitted child
- **THEN** admission closes without starting scheduler work, detector work, or a child

### Requirement: Network and storage compatibility are unchanged
Web-only SHALL retain the existing listener configuration: production containers use port 8080 and local development uses port 5122. It SHALL retain separate configuration and data roots, production defaults of `/config` and `/data`, existing environment overrides, and independent volume semantics. The mode MUST NOT require a settings, database, cache, image, entrypoint, port, or volume migration.

#### Scenario: Existing mounts are used in Web-only
- **WHEN** an operator selects `web-only` for the existing production image and mounts the established config and data volumes
- **THEN** the UI uses the existing port-8080 listener and the same separate `/config` and `/data` semantics

### Requirement: External scheduling adds no Web-only trigger API
Web-only SHALL establish only that this Web process does not schedule automatically. Block 42 MUST NOT add an HTTP trigger endpoint, command-line trigger, settings field, queue consumer, or other public control surface. `IMMICH_REVERSEGEO_MODE` remains the only new public mode input. Operators MAY use the Web UI for manual runs; automated external execution depends on the separately owned run-once composition and its finalized exclusion behavior and MUST NOT be represented as a capability delivered by block 42.

#### Scenario: External scheduler is configured before run-once exists
- **WHEN** an operator selects Web-only but no separately implemented external execution composition is available
- **THEN** the UI remains usable and no automatic processing occurs; Web-only exposes no substitute automation endpoint

#### Scenario: Separate external execution is later available
- **WHEN** an external scheduler invokes the separately delivered execution composition while the Web-only UI is running
- **THEN** Web-only still originates no scheduled pass and adds no second public trigger surface

### Requirement: Web-only composition and behavior are regression tested
Focused automated tests SHALL prove Web/Kestrel/UI availability; absence of scheduler registration, hosted alias, waits, scheduled detector activation, and authoritative executor/in-process backend; presence of the coordinator, child launch path, startup validation, lifecycle owner, and current-phase Lookup/Data dependencies; settings non-mutation; manual child dispatch; startup failure; and active-child shutdown. The tests MUST remain hermetic: no live PostgreSQL, geodata, downloads, Docker, fixed-port binding, or real heavy worker. Cross-mode matrix and production-image assertions remain outside block 42.

#### Scenario: Forbidden scheduled component appears
- **WHEN** Web-only composition registers or activates a scheduler trigger, schedule wait, or scheduled detector path
- **THEN** focused composition or behavioral tests fail before external resources or a fixed listener are used

#### Scenario: Focused Web-only smoke runs
- **WHEN** a fake schedule source, clock/wait seam, detector, child boundary, and lifecycle observations are used
- **THEN** the smoke deterministically proves no automatic activity for enabled schedule settings, one manual child dispatch, prerequisite failure, and owned-child shutdown
