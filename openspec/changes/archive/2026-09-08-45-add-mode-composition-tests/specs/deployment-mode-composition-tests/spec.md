## Purpose

Defines one hermetic cross-mode regression contract that compares startup selection, host composition, trigger policy, heavy-work placement, and isolation without exercising production infrastructure.

## ADDED Requirements

### Requirement: The matrix covers the authoritative startup inputs
The automated matrix SHALL cover an absent `IMMICH_REVERSEGEO_MODE` value and each exact accepted value `standard`, `web-only`, and `run-once`. Absence SHALL select the same Standard composition as exact `standard`. Empty, whitespace-only, padded, case-varied, and unknown values SHALL retain the block-40 pre-host failure contract and MUST NOT be normalized or defaulted.

#### Scenario: Missing and explicit Standard agree
- **WHEN** otherwise identical isolated startup cases use a missing mode value and exact `standard`
- **THEN** both select Standard and expose equivalent host, service, and trigger composition

#### Scenario: Exact public values select distinct roots
- **WHEN** isolated startup cases use exact `standard`, `web-only`, and `run-once`
- **THEN** each selects its finalized Standard, Web-only, or Run-once composition without rereading the source

#### Scenario: Invalid public input fails before composition
- **WHEN** the startup source contains an empty, whitespace-only, padded, case-varied, or unknown non-null value
- **THEN** the matrix observes the stable invalid-deployment-mode exit-2 result before builder, host, provider, logging, path, filesystem, settings, listener, or work side effects

### Requirement: Private worker precedence is compared with public selection
The exact sole `--internal-worker` invocation SHALL select InternalWorker without reading or validating the public mode source. Malformed, duplicate, or augmented reserved syntax SHALL retain its existing private-role failure and SHALL also bypass the public mode source. InternalWorker MUST NOT be selectable from any public environment value.

#### Scenario: Exact private invocation inherits every mode value
- **WHEN** the exact private invocation is tested with missing, each accepted, and an invalid canary public mode value
- **THEN** InternalWorker is selected in every case, the mode-source read count is zero, and no public composition is constructed

#### Scenario: Invalid private syntax and invalid mode coexist
- **WHEN** malformed, duplicate, or augmented reserved private syntax is supplied while the public mode value is invalid
- **THEN** the private-role failure wins without a mode-source read or public host construction

#### Scenario: Public values never select the private role
- **WHEN** each accepted public mode value is supplied without the exact private token
- **THEN** no case selects InternalWorker or initializes its controller transport

### Requirement: Host and service graphs match the cross-mode contract
The matrix SHALL inspect the landed descriptors and providers for Standard, Web-only, Run-once, and InternalWorker. Standard and Web-only SHALL expose the common Web host, server, Razor/Blazor UI, middleware/endpoint composition, coordinator, child-launch boundary, startup validator, lifecycle owner, and finalized Web-hosted status services. Run-once and InternalWorker SHALL use non-Web hosts and SHALL expose no Web server, endpoint, UI, Data Protection, HTTP listener, coordinator, or child launcher. Provider inspection MUST NOT start a listener or bind, probe, or reserve a TCP port.

#### Scenario: Standard and Web-only expose Web composition
- **WHEN** Standard and Web-only descriptors and providers are inspected without starting a network listener
- **THEN** each contains the common Web/UI/server and manual coordinator/launcher graph with its finalized Web-hosted status services

#### Scenario: Run-once is non-Web and direct
- **WHEN** the Run-once descriptors and provider are inspected
- **THEN** the root contains the finalized non-Web one-shot host and direct execution services but no server, UI, endpoint, coordinator, launcher, child bridge, or private controller protocol

#### Scenario: InternalWorker is non-Web and private
- **WHEN** the InternalWorker descriptors and provider are inspected
- **THEN** the root contains the private controller transport and worker execution services but no server, UI, scheduler, coordinator, or child launcher

### Requirement: Scheduler policy and registration identity are structural
Standard SHALL register exactly one scheduler concrete singleton and expose that same instance through its hosted-service alias. Web-only, Run-once, and InternalWorker SHALL contain neither the scheduler concrete registration nor its hosted alias, schedule waits, due callback, scheduled pending path, or no-op substitute. Other finalized singleton services exposed through multiple aliases SHALL retain one shared instance per provider rather than duplicate ownership.

#### Scenario: Standard scheduler identity is inspected
- **WHEN** the Standard provider resolves the scheduler concrete type and its hosted-service alias
- **THEN** exactly one applicable hosted registration exists and both resolutions refer to the same singleton instance

#### Scenario: Non-Standard roots are inspected for scheduling
- **WHEN** Web-only, Run-once, and InternalWorker descriptors and providers are inspected with fail-on-construction scheduler sentinels
- **THEN** no scheduler registration, alias, wait, due callback, scheduled detector activation, or substitute is present or constructed

#### Scenario: Providers are isolated
- **WHEN** multiple mode providers are built concurrently from independent startup snapshots
- **THEN** each provider preserves its own singleton identities and shares no mode-scoped singleton instance with another provider

### Requirement: Executor and geodata placement remains role-correct
Standard and Web-only Web processing roots SHALL NOT resolve the authoritative executor, an in-process processing backend, or processing geodata through scheduler, detector, coordinator, or launcher paths. Those Web roots MAY retain the transitional Lookup/Data dependencies explicitly allowed by blocks 41–42, but the matrix MUST distinguish them from an asset-processing path. Run-once SHALL compose the authoritative executor and required processing geodata directly in its invoking non-Web root. InternalWorker SHALL compose them behind the private controller boundary.

#### Scenario: Web processing roots reject direct execution
- **WHEN** Standard and Web-only processing roots are inspected and their manual/scheduled fakes are exercised
- **THEN** the authoritative executor and processing geodata are absent or unreachable and fail-on-resolution sentinels remain untouched

#### Scenario: Transitional Web features are not mistaken for processing
- **WHEN** current-phase Lookup/Data registrations are present in either Web mode
- **THEN** the matrix proves those registrations are unreachable from asset-processing trigger roots rather than claiming that the entire Web host is geodata-free

#### Scenario: Direct execution roles own heavy services
- **WHEN** Run-once and InternalWorker roots are inspected independently
- **THEN** each contains the finalized authoritative executor and required processing geodata identities while retaining its distinct one-shot or private-controller boundary

### Requirement: Trigger behavior agrees across the matrix
Standard SHALL allow one manual child dispatch without detector use and one detector-positive scheduled child dispatch; a detector-empty scheduled occurrence SHALL launch no child. Web-only SHALL allow one manual child dispatch without detector use and SHALL produce no scheduled waits, detector calls, pending transitions, or automatic child for any saved schedule. Run-once SHALL create one fresh RunOnce request, invoke the authoritative executor directly exactly once, and terminate without a detector, child, retry, replay, replacement, or second pass. Matrix behavior SHALL use fakes and MUST NOT spawn a real child process.

#### Scenario: Standard compares manual and scheduled paths
- **WHEN** Standard receives one manual request, one detector-empty scheduled occurrence, and one detector-positive scheduled occurrence through deterministic fakes
- **THEN** manual launches one child with zero detector calls, detector-empty launches none, and detector-positive launches exactly one child

#### Scenario: Web-only remains manual-only
- **WHEN** Web-only is exercised with enabled-valid, disabled, empty, and invalid saved schedules plus one manual request
- **THEN** it launches exactly the manual child and records zero schedule waits, detector calls, scheduled transitions, automatic children, or settings mutations

#### Scenario: Run-once performs one direct pass
- **WHEN** a Run-once provider executes an eligible or authoritative no-work attempt
- **THEN** it creates one fresh RunOnce request, calls one executor once, resolves no child launcher or detector, performs no second pass, and disposes the one-shot scope and host

### Requirement: Startup validation is lazy and precedes work
Standard and Web-only SHALL execute their finalized child-launch and shutdown-budget validation before request acceptance. Successful validation MUST NOT launch a child, resolve the authoritative executor, or materialize processing geodata; failed validation SHALL prevent request acceptance. Run-once and InternalWorker SHALL run only their finalized owning-root initialization before execution and MUST NOT initialize a foreign host graph.

#### Scenario: Web validation succeeds without work
- **WHEN** Standard or Web-only startup validation succeeds against fake launch prerequisites
- **THEN** the host may become acceptance-ready with zero children, executor resolutions, geodata constructions, or port bindings

#### Scenario: Web validation fails early
- **WHEN** Standard or Web-only launch prerequisites or shutdown budget are invalid
- **THEN** startup does not become acceptance-ready and no child, in-process fallback, executor, or geodata path starts

#### Scenario: Direct roots initialize only their dependencies
- **WHEN** Run-once or InternalWorker initialization is inspected with foreign-graph construction sentinels
- **THEN** required direct-worker initialization may run while Web, scheduler, coordinator, launcher, and the other role's transport remain unconstructed

### Requirement: Matrix execution is hermetic and parallel-safe
Normal matrix cases SHALL use injected or dictionary-backed mode sources and independent providers so they can execute in parallel without process-environment leakage. If an explicit entrypoint fixture must mutate `IMMICH_REVERSEGEO_MODE`, it SHALL restore the previous value in a guaranteed cleanup path and SHALL be isolated from parallel execution. No normal matrix case SHALL use live PostgreSQL, real geodata files or downloads, Docker, a real worker process, real HTTP traffic, or fixed/ephemeral port binding.

#### Scenario: Parallel snapshots do not leak
- **WHEN** missing, Standard, Web-only, and Run-once cases build and exercise providers concurrently
- **THEN** each observes only its own one-read immutable snapshot, service graph, fakes, and counters

#### Scenario: Explicit environment fixture restores state
- **WHEN** a separately identified entrypoint fixture temporarily changes the process environment
- **THEN** it runs non-parallel, restores the exact prior missing-or-value state even after failure, and does not leak into another test

#### Scenario: Forbidden infrastructure is guarded
- **WHEN** the default composition matrix runs under the normal test command
- **THEN** database, geodata, download, Docker, process-spawn, HTTP-client, and socket sentinels record zero real external activity

### Requirement: Cross-mode tests retain clear ownership boundaries
Block 45 SHALL compare the common selection and composition contract without replacing the exhaustive focused tests from blocks 40–44. It SHALL NOT claim production-image, real entrypoint, actual HTTP reachability, container UID, mounted-volume, or real process-exit evidence, which remains block 46 ownership.

#### Scenario: A predecessor-focused behavior fails
- **WHEN** a parser edge, mode-specific outcome, signal race, disposal detail, or UI rendering transition is not needed to compare mode roots
- **THEN** its owning block 40–44 focused suite remains the authoritative detailed coverage rather than duplicating it in every matrix row

#### Scenario: Production packaging needs verification
- **WHEN** the same image, entrypoint, actual port, Run-once process exit, InternalWorker HTTP absence, UID, or mounted-volume behavior must be proven
- **THEN** block 46 Docker smoke supplies that evidence and the block-45 hermetic matrix does not substitute for it
