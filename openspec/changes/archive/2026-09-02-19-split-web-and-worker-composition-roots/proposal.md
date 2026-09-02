## Why

The single inline Web composition graph makes role boundaries implicit and makes it easy for an internal worker to initialize Kestrel, Blazor, scheduling, or UI state accidentally. Block 18 is a hard prerequisite: its applied, tested pre-host role selection must exist before this change modifies composition. The checkout's existing Phase 2 executor/coordinator APIs must be inventoried at apply time, then receive explicit, testable registration roots before block 20 can construct a worker host.

## What Changes

- Define explicit shared/core, Web control-plane, and internal-worker registration roots selected from the parsed application role before the selected host is built.
- Preserve the current configuration, environment, storage-path, logging, Npgsql data-source, and singleton lifetime semantics instead of creating role-specific copies.
- Keep Blazor, Data Protection, UI state, scheduling, and coordinator/control-plane aliases on the Web path only.
- Put the executor, geodata resolvers, cache/download services, database repositories, and worker-protocol collaborators required to execute one request on the internal-worker path, without Kestrel, Razor/Blazor, Data Protection, scheduler, coordinator, or UI state.
- Temporarily retain the existing heavy Lookup/Data and in-process processing dependencies on the Web path; removing heavy geodata from Web remains block 55 work.
- Preserve concrete-singleton/interface/hosted-service alias identity, skipped-database initialization ownership, country-index initialization behavior, and centralized DuckDB extension bootstrap.
- Add composition and boundary tests that inspect and resolve each role graph without implementing a worker host loop.

## Capabilities

### New Capabilities
- application-composition-roots: Role-specific dependency composition, lifetime identity, configuration/path compatibility, and Web/worker dependency boundaries.

### Modified Capabilities
- None.

## Impact

Depends on the applied API from `18-add-application-role-parser` and the finalized Phase 2 executor, reporter, scheduler, and coordinator registrations. Expected implementation touches the executable entry/composition files and DI-focused tests in `tests/ImmichReverseGeo.Tests`. Block 20 consumes the worker registration root to build a Generic Host; this change does not add the worker loop, protocol stream handling, or exit behavior.
