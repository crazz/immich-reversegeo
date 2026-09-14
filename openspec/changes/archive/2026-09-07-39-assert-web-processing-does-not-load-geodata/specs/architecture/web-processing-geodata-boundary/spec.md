## Purpose

Enforces the transitional process-isolation boundary so Web processing can dispatch child work without activating worker-only geodata or execution services that remain registered for unrelated Web features.

## ADDED Requirements

### Requirement: Accepted Web processing delegates only across the child boundary
The automated test suite SHALL verify that an accepted manual request and an accepted detector-positive scheduled request each delegate exactly once to the production child-dispatch boundary contract and perform no in-process worker execution or heavy geodata access in the Web process.

#### Scenario: Accepted manual processing
- **WHEN** production Web composition accepts a manual processing request
- **THEN** it delegates the request exactly once to the child boundary and does not resolve, construct, or call a worker executor, country geometry index, administrative resolver, Overture or GADM resolver/cache, or airport lookup in Web

#### Scenario: Detector-positive scheduled processing
- **WHEN** production Web composition accepts a scheduled request and its detector reports eligible work
- **THEN** it delegates the request exactly once to the child boundary after detection and does not resolve, construct, or call a worker executor, country geometry index, administrative resolver, Overture or GADM resolver/cache, or airport lookup in Web

### Requirement: Detector-empty scheduling remains lightweight and local
The automated test suite SHALL verify that an accepted scheduled request whose detector reports no work may perform only the detector's lightweight repository access before local completion and SHALL activate neither a child boundary nor heavy geodata or execution services.

#### Scenario: Detector reports no eligible work
- **WHEN** production Web composition accepts a scheduled request and the detector's repository-backed query reports no eligible work
- **THEN** the request completes through the established local zero-work path with no child delegation and no worker executor, country geometry index, administrative resolver, Overture or GADM resolver/cache, or airport lookup resolution, construction, or call

### Requirement: Boundary enforcement remains route-specific during the transition
The automated test suite SHALL inspect the production processing dependency graph and exercise processing routes with deterministic sentinels, while allowing lightweight identity catalogs and heavy registrations used only by Lookup and Data to remain in the Web composition until the later whole-Web cutover.

#### Scenario: Heavy services remain registered for unrelated Web features
- **WHEN** the processing boundary test inspects production Web composition before the Lookup and Data cutover
- **THEN** it rejects forbidden dependencies reachable from processing roots without requiring all heavy geodata descriptors or assembly references to be absent from the Web host

#### Scenario: Lightweight country identity remains legal
- **WHEN** Web control-plane composition resolves lightweight country-code or resolver-profile identity data without geometry
- **THEN** the processing boundary does not classify that identity access as country-index or heavy geodata activation

## Audit Reconciliation

The test substitutes and proves the finalized child-dispatch boundary contract, not a real child process. Assertions about coordinator/detector/boundary names, registration roots, and available test seams are conditional on their landed forms after prerequisite application; bind to those exact contracts and do not claim process startup, protocol, or real worker execution occurred.

