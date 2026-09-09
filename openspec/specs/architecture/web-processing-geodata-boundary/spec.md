# Web processing geodata boundary Specification

## Purpose

Enforces the transitional process-isolation boundary so Web processing can dispatch child work without activating worker-only geodata or execution services that remain registered for unrelated Web features.

## Requirements

### Requirement: Accepted Web processing delegates only across the child boundary
The automated test suite SHALL verify that an admitted manual request and a detector-positive, subsequently admitted scheduled request each delegate exactly once to the production child-dispatch boundary and perform no in-process worker execution or heavy geodata access in Web. Scheduled detection itself occurs before identity and admission; manual processing bypasses detection.

#### Scenario: Accepted manual processing
- **WHEN** production Web composition admits a manual ProcessAssets request
- **THEN** it delegates exactly once without detector use or any Web executor/geodata activation

#### Scenario: Detector-positive scheduled processing
- **WHEN** scheduled detection reports work and the resulting ProcessAssets request wins shared admission
- **THEN** it marks pending after admission, delegates exactly once, and activates no Web executor or heavy geodata service

### Requirement: Detector-empty scheduling remains lightweight and local
The automated test suite SHALL verify that a scheduled detector no-work result performs only the detector's lightweight repository access before local scheduler completion. It SHALL create no JobId, processing pending state, adapter, coordinator admission, child delegation, worker executor, or heavy geodata activation.

#### Scenario: Detector reports no eligible work
- **WHEN** production Web composition's scheduled detector reports no eligible work before identity and admission
- **THEN** the scheduler closes the occurrence locally and all identity, processing-state, admission, child, executor, country-index, resolver, Overture/GADM, cache, and airport observations remain zero

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
