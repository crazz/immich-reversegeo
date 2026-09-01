## Why

`src/ImmichReverseGeo.Web/Program.cs` constructs a `WebApplication` before it can distinguish the normal Web process from the same-assembly internal worker process. A small fail-closed role boundary is needed now so later worker hosting cannot accidentally recurse into the Web host.

## What Changes

- Add a pure, deterministic application-role selection result with `Web`, `InternalWorker`, and a reserved `RunOnce` value.
- Define the only Phase 3 command-line role syntax as the exact, case-sensitive, argument-only invocation `--internal-worker`; this is a private launcher contract, not a public deployment mode or supported self-hoster interface.
- Default to the Web role when the internal selector is absent, preserving all ordinary ASP.NET command-line arguments unchanged for `WebApplication.CreateBuilder`.
- Reserve a typed run-once role boundary for later deployment-mode composition without exposing or implementing run-once invocation in this change.
- Select or reject the role before host construction, configuration/environment precedence, DI registration, filesystem initialization, or application logging setup. Invalid internal-role invocations terminate with a safe diagnostic and process exit code 2.
- Make a valid internal-worker selector authoritative over a separately supplied deployment-derived Web or RunOnce candidate, while leaving `IMMICH_REVERSEGEO_MODE` parsing and all deployment configuration to block 40.

## Capabilities

### New Capabilities
- `application-role-selection`: Deterministic, fail-closed selection of the private internal-worker role or the default/future public application role before host construction.

### Modified Capabilities
- None.

## Impact

Planning targets the executable startup seam in `src/ImmichReverseGeo.Web/Program.cs`, a framework-independent role model/parser, and focused MSTest coverage in `tests/ImmichReverseGeo.Tests`. It adds no command-line package, public deployment setting, worker host, run-once execution, or block-19 composition-root changes.
