## Purpose

Keeps the long-lived Web control plane dependency-light as the codebase evolves, while proving disposable worker roles retain the complete heavy execution graph.

## ADDED Requirements

### Requirement: Static dependency policy is enforced
The automated boundary policy SHALL reject unapproved direct or transitive project, package, restore-asset, source-namespace, concrete-type, and compiled-assembly dependencies from the Standard or Web-only control-plane surface into worker-only geodata or native infrastructure. The policy MUST evaluate generated component dependencies and factory-exposed dependencies, not rely solely on textual source matching.

#### Scenario: Heavy project or package edge is introduced
- **WHEN** a Standard or Web-only control-plane assembly acquires a direct or transitive dependency on a worker-only geodata, geometry, native database, cache-export, or processing-execution implementation
- **THEN** the default architecture test fails and identifies the control-plane owner, forbidden category, and dependency edge

#### Scenario: Generated or factory dependency is introduced
- **WHEN** a generated Web component injection property or application-owned registration factory exposes a forbidden worker-only type
- **THEN** the boundary test fails even when no ordinary constructor or handwritten service declaration contains that dependency

### Requirement: Production control-plane composition is closed over approved dependencies
The exact production Standard and Web-only descriptor sets SHALL exclude worker-only service types, implementations, aliases, hosted registrations, open generics, and factories. A transitive dependency walk from every Web component, control-plane service, hosted service, and other production activation root MUST terminate only in reviewed lightweight dependencies and MUST report the complete root-to-forbidden path on failure.

#### Scenario: Forbidden transitive constructor path is introduced
- **WHEN** an otherwise approved control-plane type gains a constructor or injectable-property path that eventually reaches a worker-only implementation
- **THEN** the composition guard fails with every dependency step from the production root to the forbidden type

#### Scenario: Standard and Web-only policies are compared
- **WHEN** the policy evaluates the exact production descriptor sets for both Web modes
- **THEN** both satisfy the same heavy-dependency exclusion and differ only in their approved scheduling policy

### Requirement: Reviewed lightweight dependencies remain usable
The policy SHALL permit dependency-light worker transport DTOs and job clients, immutable inventory and maintenance contracts, bounded geometry-free country identity and resolver-profile data, configuration and UI state, lazy PostgreSQL repositories or work detection, and SQLite stores limited to skipped-asset or bounded inventory/control metadata. An allowed dependency MUST NOT transitively reference or activate worker-only geodata, native infrastructure, geodata-table scans, cache mutation, or spatial indexes.

#### Scenario: Approved control-plane feature is composed
- **WHEN** Lookup, Data/inventory, maintenance, processing controls, settings, status, or country display uses only the reviewed lightweight contracts
- **THEN** the architecture policy passes without requiring a broad namespace, assembly, or package exemption

#### Scenario: Allowed data provider crosses its scope
- **WHEN** an ostensibly allowed PostgreSQL, SQLite, identity, or profile dependency reaches geodata content, native processing, network download, cache mutation, or a worker implementation
- **THEN** its transitive edge is rejected and the diagnostic distinguishes the prohibited use from the allowed lightweight category

### Requirement: Runtime sentinels detect hidden activation
Hermetic Standard and Web-only tests SHALL fail deterministically if startup, provider validation, representative page/control activation, or rejected/unavailable work initializes native or DuckDB infrastructure, loads the bundled country index, opens or scans geodata, invokes cache download/export/mutation, enters an in-process executor, or unexpectedly launches a worker. Explicitly admitted control-plane work SHALL use only the fake child-worker boundary and MUST NOT fall back to local heavy execution.

#### Scenario: Web host starts without a trigger
- **WHEN** the exact production Standard or Web-only host is built and started with fake external boundaries
- **THEN** all heavy/native/geodata sentinels and the worker-launch count remain untouched

#### Scenario: Work is rejected or unavailable
- **WHEN** a representative control-plane operation is rejected, busy, unavailable, or fails before admission
- **THEN** no worker is launched and no local heavy sentinel is reached

#### Scenario: Work is admitted
- **WHEN** a representative manual or eligible scheduled operation is admitted
- **THEN** exactly one fake worker session is launched, no real process or geodata dependency is activated in Web, and no local fallback occurs

### Requirement: Disposable heavy-role composition remains complete
The same architectural policy SHALL positively verify that Internal-worker and Run-once production roots retain the intended processing handlers, resolver/cache/export graph, country-index ownership, and native geodata capabilities while excluding Web presentation, Web inventory, and control-plane-only service ownership. Positive verification MUST classify descriptors and graph reachability without requiring live geodata, a real database, native initialization, or a child process.

#### Scenario: Worker and Run-once roots are inspected
- **WHEN** the role-composition matrix is evaluated
- **THEN** each heavy role contains its required heavy root categories, contains no forbidden Web-only roots, and Standard/Web-only contain none of those heavy categories

#### Scenario: Heavy role is accidentally hollowed out
- **WHEN** an allow/deny policy change removes a required executor, handler, resolver, cache/export, or country-index path from Worker or Run-once composition
- **THEN** the positive composition guard fails with the missing role and required category

### Requirement: Boundary enforcement is fast, default, and self-verifying
Boundary tests SHALL run under the normal default-exclusion Web test command and CI job, SHALL NOT be categorized as Integration or Performance, and SHALL require no live PostgreSQL, geodata download, Kestrel port, native initialization, or real worker process. Deliberate in-memory negative fixtures MUST prove every policy layer detects representative violations and produces stable actionable diagnostics.

#### Scenario: Normal CI tests run
- **WHEN** the repository's default test command executes
- **THEN** static policy, production descriptor/graph, generated-component, runtime-sentinel, positive-role, and negative self-tests all run within the fast hermetic suite

#### Scenario: Intentional violation fixture is evaluated
- **WHEN** a synthetic forbidden reference, descriptor, dependency path, generated-style injection, or factory metadata edge is supplied to its policy layer
- **THEN** the assertion fails for the expected policy rule and includes the owner, role, category, offending dependency, and path or remediation hint

### Requirement: Policy evolution is explicit and narrow
The allowlist and denylist SHALL be centralized, reviewed data with documented ownership and update rules. A new exception MUST name the exact contract or dependency edge, justify why its full transitive closure is lightweight, include a positive allowed fixture and applicable negative near-neighbor fixture, and update diagnostics; broad project, namespace-prefix, or package exemptions MUST NOT be used to silence a failure.

#### Scenario: A lightweight contract is added
- **WHEN** architecture evolution requires a new control-plane dependency
- **THEN** the policy update records its exact scope and rationale, proves its transitive closure remains lightweight, and preserves rejection of adjacent worker implementations

#### Scenario: Worker implementation is renamed or moved
- **WHEN** a forbidden type, namespace, package, generated activation path, or factory shape changes
- **THEN** the deny catalog and self-tests are updated in the same change so coverage cannot silently disappear
