## Purpose

Defines the post-migration Web-host boundary that preserves interactive control-plane features while ensuring native and managed geodata workloads can exist only in disposable worker or Run-once processes.

## ADDED Requirements

### Requirement: Web hosts exclude worker-only geodata workloads
Standard and Web-only hosts SHALL contain no registration or reachable construction path for administrative geodata resolution, Overture or GADM query/download/export/cache mutation, DuckDB, geometry processing, bundled spatial indexes, or in-process processing execution. Heavy operations SHALL be composed only in a worker or Run-once process.

#### Scenario: Standard host starts
- **WHEN** a Standard host builds and starts after the Lookup and Data cutovers
- **THEN** no heavy geodata descriptor, factory, constructor, index loader, native library initialization, or in-process executor is activated in the Web process

#### Scenario: Web-only host starts
- **WHEN** a Web-only host builds and starts
- **THEN** it satisfies the same heavy-dependency exclusion as Standard

### Requirement: Control-plane feature parity remains available
Standard and Web-only SHALL retain equivalent Lookup, cache inventory, cache maintenance, reset, manual processing, mode/status, and settings experiences through approved control-plane contracts. A rejected, unavailable, or failed worker launch MUST NOT fall back to local geodata execution.

#### Scenario: Lookup is submitted
- **WHEN** a user submits a valid coordinate Lookup in either Web mode
- **THEN** the Web process submits the typed worker job and presents its authoritative result without constructing a resolver, cache mutator, geometry service, or country spatial index locally

#### Scenario: Data page is opened
- **WHEN** a user opens or refreshes Data or Administrative Areas
- **THEN** the Web process reads bounded filesystem and SQLite inventory metadata and does not query geodata rows, count area rows, load geometry, or initialize a heavy cache service

#### Scenario: Cache or database maintenance is requested
- **WHEN** a user requests cache refresh/deletion or a finalized reset operation
- **THEN** the existing control-plane admission and command contracts are used without a local heavy-geodata fallback

### Requirement: Only reviewed lightweight data dependencies remain in Web
Web composition MAY retain geometry-free country identity and resolver-profile data, Npgsql-backed repositories and eligibility detection, skipped/inventory SQLite metadata access, configuration, UI state, and worker-job/control-plane services. These dependencies MUST NOT reference or transitively activate Overture/GADM implementations, DuckDB, geometry packages, geodata tables, or a country spatial index.

#### Scenario: Country identity is required
- **WHEN** a Web control path maps or displays a country code
- **THEN** it uses bounded identity data that performs no geometry, geodata-database, network, cache mutation, or spatial-index work

#### Scenario: Scheduled eligibility is checked
- **WHEN** Standard checks whether scheduled work exists
- **THEN** only the lightweight Npgsql detector/repository path may run before worker admission and no geodata dependency is activated

#### Scenario: Inventory metadata is inspected
- **WHEN** the Web inventory reads a cache file
- **THEN** SQLite is used only for the bounded schema and metadata contract established by the cache-inventory change and no pooled handle or geodata content scan remains

### Requirement: Web startup remains lazy and worker-free
Building or starting Standard or Web-only SHALL NOT launch a worker, scan cache inventory, open a geodata database, build the bundled country index, initialize DuckDB or geometry infrastructure, or eagerly connect to PostgreSQL. Work SHALL begin only after the corresponding control-plane action or eligible scheduled trigger.

#### Scenario: Provider and host are constructed
- **WHEN** the production Web service provider is built and the host reaches ready without a user or schedule trigger
- **THEN** no worker process, inventory scan, PostgreSQL connection, geodata file access, or heavy constructor side effect occurs

### Requirement: The boundary is verified structurally and at runtime
Automated Standard and Web-only tests SHALL inspect the exact production composition and compiled Web dependency surface, and SHALL exercise representative component/control paths with forbidden constructor, factory, country-index-load, native-initialization, and worker-launch sentinels. The checks MUST fail with an actionable forbidden category or dependency path when the boundary is violated.

#### Scenario: Forbidden descriptor or static reference is introduced
- **WHEN** a worker-only implementation, geodata assembly, or native geometry/database dependency becomes part of the Web registration or compiled dependency surface
- **THEN** the structural boundary test fails before relying on runtime non-use

#### Scenario: Hidden lazy activation is introduced
- **WHEN** a Web component or control service reaches a forbidden factory or the bundled-country index load point during startup or a representative Web action
- **THEN** a runtime sentinel fails the Standard and Web-only composition test
