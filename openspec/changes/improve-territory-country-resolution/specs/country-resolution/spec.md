## Purpose

Provide deterministic offline coordinate-to-country resolution, including distinct ISO territories, so valid assets can enter the administrative lookup pipeline with an unambiguous country identity.

## ADDED Requirements

### Requirement: Country resolution operates offline
The system SHALL determine the initial country or territory for a coordinate from bundled data without requiring a live network request.

#### Scenario: First lookup without network access
- **WHEN** a coordinate falls within bundled country or territory coverage and no network connection is available
- **THEN** the system returns the matching country identity from bundled data

### Requirement: Supported territories retain distinct ISO identity
The system SHALL return a supported territory's own country name, ISO Alpha-3 code, and ISO Alpha-2 code rather than normalizing it to an administering sovereign state.

#### Scenario: Hong Kong Island coordinate resolves as Hong Kong
- **WHEN** country resolution receives latitude `22.2812` and longitude `114.1719`
- **THEN** it returns country name `Hong Kong`, ISO Alpha-3 `HKG`, and ISO Alpha-2 `HK`

#### Scenario: Second Hong Kong Island coordinate resolves as Hong Kong
- **WHEN** country resolution receives latitude `22.2783` and longitude `114.1719`
- **THEN** it returns country name `Hong Kong`, ISO Alpha-3 `HKG`, and ISO Alpha-2 `HK`

#### Scenario: Macao coordinate resolves as Macao
- **WHEN** country resolution receives latitude `22.1987` and longitude `113.5439`
- **THEN** it returns country name `Macao`, ISO Alpha-3 `MAC`, and ISO Alpha-2 `MO`

#### Scenario: Greenland coordinate resolves as Greenland
- **WHEN** country resolution receives latitude `64.1814` and longitude `-51.6941`
- **THEN** it returns country name `Greenland`, ISO Alpha-3 `GRL`, and ISO Alpha-2 `GL`

#### Scenario: Faroe Islands coordinate resolves as Faroe Islands
- **WHEN** country resolution receives latitude `62.0079` and longitude `-6.7900`
- **THEN** it returns country name `Faroe Islands`, ISO Alpha-3 `FRO`, and ISO Alpha-2 `FO`

#### Scenario: Jersey coordinate resolves as Jersey
- **WHEN** country resolution receives latitude `49.1868` and longitude `-2.1066`
- **THEN** it returns country name `Jersey`, ISO Alpha-3 `JEY`, and ISO Alpha-2 `JE`

#### Scenario: Guernsey coordinate resolves as Guernsey
- **WHEN** country resolution receives latitude `49.4568` and longitude `-2.5820`
- **THEN** it returns country name `Guernsey`, ISO Alpha-3 `GGY`, and ISO Alpha-2 `GG`

#### Scenario: Isle of Man coordinate resolves as Isle of Man
- **WHEN** country resolution receives latitude `54.1523` and longitude `-4.4861`
- **THEN** it returns country name `Isle of Man`, ISO Alpha-3 `IMN`, and ISO Alpha-2 `IM`

#### Scenario: Puerto Rico coordinate resolves as Puerto Rico
- **WHEN** country resolution receives latitude `18.4655` and longitude `-66.1057`
- **THEN** it returns country name `Puerto Rico`, ISO Alpha-3 `PRI`, and ISO Alpha-2 `PR`

#### Scenario: Guam coordinate resolves as Guam
- **WHEN** country resolution receives latitude `13.4443` and longitude `144.7937`
- **THEN** it returns country name `Guam`, ISO Alpha-3 `GUM`, and ISO Alpha-2 `GU`

#### Scenario: U.S. Virgin Islands coordinate resolves as U.S. Virgin Islands
- **WHEN** country resolution receives latitude `18.3419` and longitude `-64.9307`
- **THEN** it returns country name `U.S. Virgin Islands`, ISO Alpha-3 `VIR`, and ISO Alpha-2 `VI`

#### Scenario: Bermuda coordinate resolves as Bermuda
- **WHEN** country resolution receives latitude `32.2949` and longitude `-64.7814`
- **THEN** it returns country name `Bermuda`, ISO Alpha-3 `BMU`, and ISO Alpha-2 `BM`

#### Scenario: Gibraltar coordinate resolves as Gibraltar
- **WHEN** country resolution receives latitude `36.1408` and longitude `-5.3536`
- **THEN** it returns country name `Gibraltar`, ISO Alpha-3 `GIB`, and ISO Alpha-2 `GI`

#### Scenario: Cayman Islands coordinate resolves as Cayman Islands
- **WHEN** country resolution receives latitude `19.2866` and longitude `-81.3744`
- **THEN** it returns country name `Cayman Islands`, ISO Alpha-3 `CYM`, and ISO Alpha-2 `KY`

#### Scenario: British Virgin Islands coordinate resolves as British Virgin Islands
- **WHEN** country resolution receives latitude `18.4286` and longitude `-64.6185`
- **THEN** it returns country name `British Virgin Islands`, ISO Alpha-3 `VGB`, and ISO Alpha-2 `VG`

#### Scenario: Aruba coordinate resolves as Aruba
- **WHEN** country resolution receives latitude `12.5211` and longitude `-69.9683`
- **THEN** it returns country name `Aruba`, ISO Alpha-3 `ABW`, and ISO Alpha-2 `AW`

#### Scenario: Curaçao coordinate resolves as Curaçao
- **WHEN** country resolution receives latitude `12.1696` and longitude `-68.9900`
- **THEN** it returns country name `Curaçao`, ISO Alpha-3 `CUW`, and ISO Alpha-2 `CW`

#### Scenario: Åland Islands coordinate resolves as Åland Islands
- **WHEN** country resolution receives latitude `60.0973` and longitude `19.9348`
- **THEN** it returns country name `Åland Islands`, ISO Alpha-3 `ALA`, and ISO Alpha-2 `AX`

#### Scenario: Réunion coordinate resolves as Réunion
- **WHEN** country resolution receives latitude `-20.8789` and longitude `55.4481`
- **THEN** it returns country name `Réunion`, ISO Alpha-3 `REU`, and ISO Alpha-2 `RE`

#### Scenario: French Polynesia coordinate resolves as French Polynesia
- **WHEN** country resolution receives latitude `-17.5516` and longitude `-149.5585`
- **THEN** it returns country name `French Polynesia`, ISO Alpha-3 `PYF`, and ISO Alpha-2 `PF`

#### Scenario: New Caledonia coordinate resolves as New Caledonia
- **WHEN** country resolution receives latitude `-22.2758` and longitude `166.4580`
- **THEN** it returns country name `New Caledonia`, ISO Alpha-3 `NCL`, and ISO Alpha-2 `NC`

#### Scenario: Nearby mainland coordinate remains China
- **WHEN** country resolution receives a coordinate inside mainland China and outside Hong Kong and Macao coverage
- **THEN** it returns country name `China`, ISO Alpha-3 `CHN`, and ISO Alpha-2 `CN`

### Requirement: Mandatory territory fixtures gate bundled releases
The generated bundled country artifact MUST resolve every mandatory territory fixture to its own canonical identity and MUST NOT normalize a fixture to its administering sovereign state.

#### Scenario: Mandatory territory fixture is missing or normalized
- **WHEN** generated bundled data does not resolve any mandatory territory coordinate to its specified name, Alpha-3 code, and Alpha-2 code
- **THEN** artifact validation fails and the generated database is not accepted for release

### Requirement: Bundled identities have usable ISO mappings
Every standard country or territory identity emitted by bundled spatial resolution MUST have a non-empty ISO Alpha-3 and ISO Alpha-2 mapping suitable for selecting country-specific downstream data.

#### Scenario: Bundled identity coverage is validated
- **WHEN** bundled country data is validated during development or release preparation
- **THEN** every emitted standard spatial identity has a corresponding bidirectional Alpha-2 and Alpha-3 mapping

### Requirement: Successful country resolution unlocks administrative lookup
The system SHALL continue with configured administrative data sources after resolving a country or territory with a usable ISO identity.

#### Scenario: Hong Kong country bootstrap succeeds
- **WHEN** an asset coordinate resolves to `HKG` and `HK`
- **THEN** the system attempts the configured Overture, GADM, and place-resolution stages instead of returning an initial country no-match result

#### Scenario: Mandatory territory bootstrap succeeds
- **WHEN** an asset coordinate resolves to any mandatory territory identity
- **THEN** the system uses that territory's Alpha-2 and Alpha-3 codes for configured downstream stages instead of substituting an administering sovereign identity

### Requirement: Country lookup failures identify their cause
The system SHALL distinguish a coordinate outside bundled spatial coverage from a matched spatial identity that lacks a usable ISO mapping.

#### Scenario: Coordinate has no bundled spatial match
- **WHEN** no bundled country or territory geometry covers the coordinate
- **THEN** diagnostics report that bundled spatial coverage found no match

#### Scenario: Matched identity has no ISO mapping
- **WHEN** bundled spatial coverage matches an identity whose country code cannot be mapped for downstream use
- **THEN** diagnostics report an ISO mapping failure and do not describe the result as a spatial no-match

### Requirement: Country selection is deterministic at territory boundaries
When bundled geometries overlap or meet, the system MUST apply a stable selection policy that preserves a matching distinct territory and does not depend on data iteration order.

#### Scenario: Coordinate covered by territory and sovereign candidates
- **WHEN** a coordinate is covered by both a mandatory distinct territory candidate and a broader sovereign candidate
- **THEN** repeated lookups return the distinct territory identity consistently
