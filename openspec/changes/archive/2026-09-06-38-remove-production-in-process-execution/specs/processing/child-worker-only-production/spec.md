## Purpose

Ensures production Web processing remains a control-plane operation and every processing pass that performs heavy work runs only in the isolated child-worker role.

## ADDED Requirements

### Requirement: Production Web processing is child-worker only
The production Web application SHALL dispatch every accepted manual processing request and every accepted scheduled request with eligible work to exactly one child worker. It MUST NOT execute the authoritative processing pass in the Web process, select an in-process backend, fall back after child failure, launch a replacement child, replay the request, or retry automatically.

#### Scenario: Manual request is accepted
- **WHEN** the production Web control plane accepts a manual processing request
- **THEN** exactly one child worker receives that request and no in-process processing route is available

#### Scenario: Eligible scheduled request is accepted
- **WHEN** the production Web control plane accepts a scheduled request and its pre-launch detector reports eligible work
- **THEN** exactly one child worker receives that request and no in-process processing route is available

#### Scenario: Child execution fails
- **WHEN** child startup, protocol handling, execution, cancellation escalation, or cleanup fails
- **THEN** the established child outcome remains authoritative and the Web application does not execute, replay, replace, or retry the processing pass in-process

### Requirement: Empty scheduled detection remains Web-lightweight
The production Web application SHALL retain the scheduled pre-launch detector without making the detector an authoritative processing pass. A normally empty result, local contention, or cancellation/failure before dispatch MUST NOT launch or resolve a child backend and MUST NOT construct or invoke the authoritative executor or its heavy processing graph.

#### Scenario: Detector reports no eligible work
- **WHEN** an admitted scheduled request's detector completes normally with no eligible work
- **THEN** the established local zero-work finalization occurs without child dispatch or authoritative in-process execution

#### Scenario: Request does not reach dispatch
- **WHEN** local contention rejects a request or the admitted detector is cancelled or fails before dispatch
- **THEN** no child backend or authoritative processing executor is resolved

### Requirement: Authoritative executor is absent from production Web composition
The production Web composition SHALL contain no registration, alias, factory, constructor dependency, or callable fallback that can resolve or invoke the authoritative processing executor. The internal-worker composition SHALL retain that executor for one authoritative pass, and automated tests MAY construct it or substitute control-plane fakes outside production Web composition.

#### Scenario: Production roles are composed
- **WHEN** production Web and internal-worker service graphs are inspected independently
- **THEN** only the internal-worker graph can resolve the authoritative processing executor while the Web graph retains only its child-dispatch control path

#### Scenario: Control-plane test uses a fake
- **WHEN** a deterministic coordinator or scheduler test substitutes a fake child backend
- **THEN** the fake remains outside production registration and does not create an in-process production route

### Requirement: Existing Web features and configuration remain compatible
Removing production in-process processing SHALL add no persisted setting, environment variable, command-line option, endpoint, or UI control. Web services still required by Lookup and Data SHALL remain available until their separately planned migration, and rollback SHALL require deployment of a reverted application version rather than runtime backend selection.

#### Scenario: Existing deployment starts after removal
- **WHEN** an existing deployment upgrades without changing its public configuration
- **THEN** processing uses child workers and no configuration migration or backend choice is required

#### Scenario: Lookup or Data uses a retained heavy service
- **WHEN** Lookup or Data resolves a service still needed before the Phase 7 migration
- **THEN** that feature remains available without making the authoritative processing executor reachable from the Web processing path

## Audit Reconciliation

“Local contention” means only Web/coordinator admission rejection before dispatch; it is not PostgreSQL advisory-lock Busy. Preserve distinct authoritative committed terminals, local admission rejection without a child, canonical advisory Busy as a failed child terminal with no eligibility and four zero counts, and forced raw kill as classification evidence rather than a terminal. No case restores an in-process fallback.

