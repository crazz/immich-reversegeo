## Context

See `proposal.md` for motivation and `specs/control-plane-dependency-boundary/spec.md` for the contract. Block 39 introduces processing-focused graph/sentinel concepts, while finalized block 55 establishes the complete post-cutover allow/deny catalog, exact role-composition seams, dependency-path reporter, compiled/static checks, and constructor/index/native/launcher hooks. Block 56 owns their durable consolidation and default-CI enforcement; it must consume those landed assets rather than rebuild migration-only tests.

The checkout used during planning is pre-cutover and still has one broad `Program.cs` composition graph, direct heavy Web project/package references, heavy Razor injection properties, and no landed role-root symbols. Therefore apply starts by binding to block 55's actual handoff. Any mismatch in names is resolved against the landed production seams, not by inventing parallel roots or weakening the policy.

## Goals / Non-Goals

**Goals:**

- Encode the finalized Web/worker boundary once as reusable policy data and analyzers shared by structural, composition, generated-component, and runtime tests.
- Detect direct, transitive, lazy, factory-owned, generated, and compiled dependency regressions with deterministic root-to-offender diagnostics.
- Prove Standard/Web-only exclusion and Worker/Run-once completeness symmetrically.
- Keep all boundary checks hermetic, fast, and mandatory in the default Web test suite.
- Define a narrow policy-evolution process that makes legitimate architecture changes possible without broad suppressions.

**Non-Goals:**

- Reperform block 55's component migration, registration narrowing, DTO/identity relocation, or project/package removal.
- Duplicate block 39's transitional processing-only test or preserve allowances that block 55 removed.
- Change deployment behavior, worker protocol, scheduling/work detection, geodata algorithms, cache semantics, or block 57.
- Prove the memory boundary using RSS thresholds, load native libraries merely to test their absence, or run live databases, downloads, Kestrel, or real child processes.
- Adopt a third-party architecture framework unless the landed helpers demonstrably cannot inspect required metadata; MSTest and build/reflection metadata are sufficient by default.

## Decisions

### 1. Centralize one typed role/dependency policy

Create one test-support policy model populated from block 55's finalized handoff. It classifies production roles, activation roots, forbidden heavy categories, required heavy-role categories, approved exact contracts, allowed lightweight provider scopes, and forbidden project/package/assembly edges. Descriptor, graph, reflection, build-metadata, and sentinel assertions consume this model, so category names and diagnostics cannot drift across independent test lists.

The Web deny side covers administrative resolvers; Overture/GADM query, download, export, and cache mutation; DuckDB/native bootstrap; geometry readers/indexes/prepared geometry; bundled country-index loading; worker handlers; and in-process processing execution. The allow side names exact dependency-light contracts: transport DTOs/job clients, inventory/deletion/reset/controller contracts, configuration/UI state, geometry-free country/profile identity, lazy Npgsql repository/detector paths, and skipped/inventory metadata-only SQLite. Allowing an interface never implicitly allows its implementation assembly or transitive closure.

Alternative: separate ad hoc lists per test. Rejected because a renamed type can disappear from one layer silently. Alternative: ban all types from broad namespaces. Rejected as the primary mechanism because DTO relocation, generated names, and legitimate shared contracts create false positives; namespaces remain diagnostic/category signals backed by concrete type and assembly policy.

### 2. Enforce static closure from build and compiled metadata, not string grep alone

Inspect project references and direct package declarations, resolved restore assets, and compiled assembly references for every assembly in the Standard/Web-only control-plane closure. Compare them with the exact approved edge manifest produced after block 55. This catches both direct and transitive native/geodata re-entry. Source checks are retained only for high-signal leftovers such as forbidden global Razor imports or forbidden namespace/type tokens and provide remediation context; they are not the proof of absence.

Load the built Web assembly in an isolated metadata/reflection context and enumerate production component types, injectable properties, constructors, base types, generic arguments, and referenced assemblies. This covers generated Razor `[Inject]` properties that do not exist as tracked C# files. Exclude compiler/framework-generated support types only through precise predicates and negative fixtures, not a blanket generated-code exemption.

Application-owned registration factories must publish analyzable dependency metadata through the block-55 registration seam. The policy checks declared service/implementation/category inputs without invoking arbitrary factories. A factory lacking required metadata fails as `UnclassifiedFactory` and identifies the registration owner. Runtime sentinels then cover behavior hidden behind otherwise valid factory metadata.

Alternative: grep `.razor` and project XML. Rejected because generated injection, aliases, reflection, factories, and transitive restore assets evade it. Alternative: execute every factory during analysis. Rejected because classification must not trigger external side effects or conflate startup behavior with static ownership.

### 3. Walk exact production descriptor and dependency graphs

Reuse the exact block-55 Standard/Web-only registration output before provider construction. Inspect service type, implementation type/instance/factory metadata, lifetime, keyed/open-generic shape, concrete/hosted aliases, and alias identity. Start graph traversal from every compiled Web component plus production controllers, application/control services, hosted services, and other root categories in the central policy. Traverse constructor parameters, injectable properties, closed generic arguments, aliases, and declared factory dependencies with cycle detection and deterministic ordering.

A failure reports mode, root, each edge kind, offending type/assembly, forbidden category, and the nearest policy entry. Multiple violations are sorted and aggregated so one CI run is actionable. Standard and Web-only run the same closure assertion; only the explicit scheduler roots differ. The graph walker does not resolve the provider merely to discover edges.

Alternative: resolve a few representative services. Rejected because unused descriptors and new pages remain invisible. Alternative: inspect all assembly types without roots. Rejected because it creates false positives from worker code not reachable from Web; role ownership and reachable production roots are the unit of policy.

### 4. Pair exclusion with Worker and Run-once positive guards

Apply the same descriptor classifier to Internal-worker and Run-once production roots. Each role has a required category manifest for its intended entry handler/executor, administrative resolver, Overture/GADM cache/query/export capabilities as applicable, native/DuckDB bootstrap ownership, and country-index path. It also denies Razor presentation, Web inventory, and Web control services that the role must not own. Validate descriptors and graph reachability using fakes or metadata; do not initialize native libraries or open geodata.

This symmetry prevents the easiest false success: deleting a heavy registration from every role. It also detects accidental recombination of Web and Worker roots.

Alternative: test Web absence only. Rejected because boundary correctness requires heavy work to remain available in the disposable process.

### 5. Retain deterministic runtime sentinels for hidden activation

Promote block 55's hooks into reusable, instance-scoped test sentinels for native/DuckDB bootstrap, pre-country-index load, geodata file open/query/export/cache mutation, in-process executor entry, external database connection, inventory scan where relevant, and worker launch/session creation. Sentinels fail before real file/network/native work and record ordered category/count/owner details.

Build and start exact Standard and Web-only hosts with fake external boundaries, then activate representative compiled page/control graphs. Startup, rendering, and rejected/busy/unavailable actions must leave every heavy and launch sentinel at zero. Explicitly admitted Lookup, cache, manual processing, and Standard scheduled paths use a recording fake child boundary; each expected admission creates exactly one fake session and never reaches a local-heavy sentinel. Web-only has no scheduled positive case. Inventory uses only minimal metadata fixtures and releases handles.

Runtime tests complement structural checks; they do not compensate for a forbidden static reference. A real worker, database, geodata cache, native library, process, port, timing sleep, or RSS threshold is prohibited in this suite.

Alternative: trust static graph completeness. Rejected because reflection, delegates, lazy factories, and startup callbacks can conceal activation. Alternative: use real native/geodata failures as sentinels. Rejected as slow, environment-dependent, and too late.

### 6. Make negative fixtures prove the enforcement mechanism

Each layer receives synthetic in-memory fixtures: forbidden direct/transitive build edge, concrete and namespace/type edge, generated-style injectable property, descriptor/alias/open-generic violation, multi-hop constructor path, missing and dishonest factory metadata, hidden runtime activation, unexpected launcher call, and a missing required Worker/Run-once category. Fixtures exercise policy helpers directly and assert stable diagnostic fields rather than brittle full prose.

Add positive fixtures for every allowed category and near-neighbor negatives: metadata-only SQLite passes while geodata SQLite access fails; Npgsql detector/repository passes while a heavy executor closure fails; country identity passes while a spatial country resolver/index fails; transport DTO/client passes while a worker handler fails. This prevents both overbroad denial and accidental allowlist expansion.

Alternative: temporarily modify production files in tests. Rejected because it is slow, unsafe under parallel execution, and tests the build harness more than the policy.

### 7. Put fast architecture enforcement in default CI

Place production-policy tests and helpers under `tests/ImmichReverseGeo.Tests/` beside the existing Web composition tests. Do not mark them `Integration` or `Performance`. Split focused test classes by static closure, descriptor/graph, runtime sentinel, role matrix, and policy self-test so failures and local filters are clear. Reuse built outputs and parse restore metadata once per test run; cache immutable analysis by input path/timestamp and avoid repeated host starts where matrix-driven assertions can share a fixture without shared mutable sentinels.

The normal `npm run test`/default runsettings path is the required CI gate. Focused commands are documented for local policy work, but no separate opt-in job is authoritative. Add a CI assertion or test discovery check if the repository's existing command can silently omit the Web test assembly.

Alternative: classify architecture checks as integration tests. Rejected because a boundary that does not run by default will decay.

### 8. Govern false positives and policy evolution

Keep policy entries exact and reviewable, each with category, role applicability, rationale, owner, and diagnostic remediation. Adding an allowed contract requires proving its complete project/package/assembly and constructor closure is lightweight, adding a positive fixture, and adding a negative adjacent implementation fixture. Renames/moves of forbidden types update the catalog and its negative fixture in the same change. Broad assembly, namespace-prefix, package, or `object` exemptions are prohibited.

When a check fails after legitimate architecture evolution, first classify whether production ownership changed or analysis discovered a previously hidden edge. Update production composition when the edge is heavy; update precise factory metadata or policy only when evidence proves the edge is lightweight. Suppressions require an expiry/linked follow-up if the test infrastructure supports them; otherwise no suppression is accepted. Diagnostic schema stays stable enough for assertions: rule id, role, root/owner, category, offender, dependency path/edge, and remediation hint.

Alternative: freeze the initial catalog. Rejected because stale lists become false assurance. Alternative: permit inline suppressions. Rejected because they hide transitive ownership and make review difficult.

## Risks / Trade-offs

- [Block 55 handoff names or assembly boundaries differ from planning] → Bind only to its landed production seam and catalog; report exact missing inputs instead of creating a second composition root or guessing from the pre-cutover checkout.
- [Same entry assembly contains role dispatch while role-owned implementation closures differ] → Scope static policy to the finalized block-55 control-plane closure and explicit bootstrap manifest; do not exempt the entire entry assembly. Treat an unresolved heavy entry-assembly edge as a stop condition.
- [Reflection loads an assembly or static initializer] → Use metadata-only or isolated load context where possible, never instantiate analyzed types, and keep runtime activation in sentinel-controlled fixtures.
- [Generated/framework types create noise] → Filter by precise production component/root ownership and prove each exclusion with generated-style positive/negative fixtures.
- [Factory metadata lies or goes stale] → Require metadata on every application-owned factory, compare it with implementation signatures when available, and retain runtime sentinels for hidden activation.
- [Allowlisted provider becomes a geodata escape hatch] → Scope Npgsql/SQLite/identity allowances to exact contracts and require near-neighbor/transitive negative tests.
- [Positive heavy-role checks initialize native state] → Assert descriptor/category presence and graph reachability with fake leaves; never resolve native/geodata implementations in default tests.
- [Policy suite becomes slow] → Parse/build metadata once, use in-memory negative fixtures, matrix production roles, and set no Integration/Performance category; investigate regressions rather than moving checks out of default CI.

## Migration Plan

1. Verify block 55 is applied and import its finalized allow/deny catalog, exact production role-registration seam, dependency-path reporter, static closure checks, and sentinel hooks; compare with block 39 and consolidate rather than duplicate.
2. Introduce the central typed role/dependency policy and stable diagnostic model, then migrate the block-55 checks to consume it without changing production behavior.
3. Add project/package/restore/compiled-reference inspection, generated-component reflection, and required factory metadata enforcement; retain source text checks only as supplemental diagnostics.
4. Expand descriptor and transitive graph traversal over every Standard/Web-only production root, with precise positive allowances and deterministic aggregated path reporting.
5. Add Worker/Run-once required-category and Web-root exclusion matrices using production descriptors and fake leaves.
6. Promote runtime hooks into hermetic Standard/Web-only sentinel fixtures and cover startup, representative page/control activation, rejected work, and admitted fake-worker paths.
7. Add layer-specific negative self-tests, allowed-category positives, and adjacent negatives; document catalog update and false-positive triage policy next to the helper.
8. Run focused boundary tests, the normal default-exclusion suite, strict OpenSpec validation/status, and a block-56-only diff review. Do not edit or inspect block 57.

Rollback removes the consolidated policy/tests and restores the prior block-55 migration checks together; production composition and data require no rollback. A partial rollback that leaves default CI without the block-55 invariant is invalid.
