## Why

The Web host needs a deterministic way to describe how the same Immich ReverseGeo application should be invoked as the Phase 3 internal worker across local, container, and apphost layouts. Inferring a child target from ambient process state is unsafe under Microsoft.Testing.Platform because the current executable and entry assembly are the test host, not the Web application. The contract must therefore resolve and validate the current application entrypoint explicitly, fail closed when it cannot, and carry process-start settings without starting a process or exposing database credentials.

## What Changes

- Add a pure, immutable worker child-invocation descriptor containing the executable, ordered argument values, working directory, environment-inheritance policy, redirected-standard-stream settings, and shell/window settings.
- Resolve a validated same-application entrypoint for framework-dependent `dotnet <absolute-entry-assembly>` execution, the current application apphost, and the existing framework-dependent Docker layout; append the one exact `--internal-worker` argument defined by block 18.
- Reject null, missing, relative, mismatched, unsupported, or test-host-derived entrypoints with a typed safe failure instead of guessing from the current directory, `AppContext.BaseDirectory`, copied assemblies, or PATH candidates.
- Preserve the current working directory and complete inherited environment so `CONFIG_DIR`, `DATA_DIR`, ASP.NET settings, and `DB_*` secrets remain available to the child, while keeping all configuration and secret values out of arguments and redacted from diagnostics/log representations.
- Add deterministic OS/path-layout tests through injected runtime and filesystem seams, including Windows/Unix path behavior, local framework-dependent, Docker, apphost, and ambient MSTest-host cases.
- Keep process creation, request writes, stream draining, cancellation, waiting, exit classification, and disposal in block 25. Add no Docker socket dependency and build no separate worker image.

## Capabilities

### New Capabilities
- `worker-command-invocation`: Resolves and validates a safe cross-platform descriptor for invoking the same application as an internal worker.

### Modified Capabilities
- None.

## Impact

Planning targets a small Web-side descriptor/resolver service and focused tests. It consumes block 18's exact private role token and the current Web application identity, and block 25 will be the only component that turns the descriptor into a running child process. Existing Docker and Compose layouts remain unchanged.
