## Why

Block 55 establishes the post-migration Web/control-plane boundary and one-time migration checks, but those checks can decay as projects, packages, generated components, factories, and composition roots evolve. A durable, default-CI architecture policy is needed so Standard and Web-only cannot silently regain worker-only geodata reachability while worker and Run-once remain provably complete.

## What Changes

- Turn block 55's finalized allow/deny catalog, production-composition seam, dependency-path reporter, and runtime hooks into one reusable architectural boundary policy instead of duplicating migration tests or block 39's processing-only checks.
- Enforce direct and transitive project, package, restore-asset, compiled-assembly, namespace, and concrete-type rules, including generated Razor injection properties and application-owned factory registrations.
- Walk exact Standard and Web-only service descriptors and transitive constructor/property graphs; permit only reviewed control-plane contracts and bounded lightweight Npgsql, metadata-only SQLite, country identity/profile, configuration, UI-state, and worker-client dependencies.
- Use deterministic runtime sentinels for native/DuckDB initialization, bundled country-index loading, geodata file/query/export/cache mutation, in-process execution, and worker launch; verify startup and rejected work remain inert while admitted work launches only the expected fake worker.
- Add positive Internal-worker and Run-once composition guards so enforcement cannot make heavy roles incomplete merely to keep Web clean.
- Keep the policy hermetic and fast in the default test/CI path, with deliberate negative fixtures, actionable root-to-offender diagnostics, and a documented review process for narrow allowlist or denylist evolution.

## Capabilities

### New Capabilities
- `control-plane-dependency-boundary`: Durable structural, composition-graph, runtime, and policy-evolution enforcement of the finalized control-plane/worker dependency boundary.

### Modified Capabilities
- None.

## Impact

The existing block-39/block-55 architecture and composition test helpers, exact production role-registration seams, Web generated-component metadata, project/restore/assembly dependency inspection, runtime sentinel hooks, and default tests under `tests/ImmichReverseGeo.Tests/` are affected. Production behavior, worker protocol, public deployment modes, geodata algorithms, and block 57 are not changed.
