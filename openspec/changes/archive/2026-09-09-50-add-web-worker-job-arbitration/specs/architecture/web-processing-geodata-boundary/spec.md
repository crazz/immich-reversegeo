## MODIFIED Requirements

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
