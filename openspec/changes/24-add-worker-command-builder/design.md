## Context

The current Web project targets .NET 10 with `UseAppHost=false`. Local framework-dependent output is `ImmichReverseGeo.Web.dll`; the Dockerfile publishes framework-dependent output into `/app` and starts `dotnet ImmichReverseGeo.Web.dll`. A future apphost layout starts the native application executable directly. These layouts need different argument lists but must select the same assembly/application role.

Ambient runtime discovery is hazardous in tests. Microsoft.Testing.Platform emits and starts `ImmichReverseGeo.Tests`/`ImmichReverseGeo.Tests.dll`, while copying `ImmichReverseGeo.Web.dll` into the same output directory. `Environment.ProcessPath`, `Assembly.GetEntryAssembly()`, `AppContext.BaseDirectory`, and the current directory therefore describe the test application during unit tests. A builder that scans or blindly trusts those values can recursively start the test runner or choose a copied dependency rather than the deployed Web entrypoint.

Block 18 owns the exact private role syntax: the complete worker role argument sequence is `--internal-worker`, case-sensitive, with no values or companion arguments. Block 25 owns process creation and all lifecycle/pipe behavior. Block 24 must end at an immutable, validated invocation descriptor.

## Goals / Non-Goals

**Goals:**
- Resolve the currently running Web application in framework-dependent `dotnet`, current-apphost, and existing Docker layouts.
- Produce a pure cross-platform descriptor with discrete argument values, working directory, environment-inheritance policy, redirected standard streams, `UseShellExecute=false`, and `CreateNoWindow=true`.
- Preserve child access to inherited configuration, data paths, ASP.NET/.NET settings, and database secrets without putting values into arguments or diagnostics.
- Fail closed for unavailable, mismatched, ambiguous, test-host, relative, or missing entrypoints.
- Make every layout and OS/path branch deterministic through injected facts and filesystem/path seams.

**Non-Goals:**
- Starting, monitoring, cancelling, killing, retrying, or disposing a process.
- Writing requests, draining or parsing stdout/stderr, waiting for exit, or classifying worker outcomes.
- Changing the worker protocol, role parser, worker host, coordinator, Dockerfile, Compose files, images, mounts, or deployment modes.
- Using the Docker socket/API or building a second worker image.
- Advertising `--internal-worker` as a public self-hoster interface.

## Decisions

### Represent intent as an immutable descriptor, not ProcessStartInfo

The builder returns a discriminated success/failure result. Success contains an immutable descriptor with:

- absolute executable path;
- immutable ordered argument values;
- absolute current working directory;
- an explicit inherit-current-environment policy with no per-command mutations;
- redirect-stdin/stdout/stderr flags set to true;
- shell execution set to false; and
- create-no-window set to true.

The descriptor does not own `ProcessStartInfo`, `Process`, streams, PID, cancellation, exit, or disposal. Block 25 translates it to `ProcessStartInfo` by adding each argument through `ArgumentList`; it never joins, quotes, or reparses a command string. Keeping start mechanics out of this change makes construction pure and lets block 25 own the resource lifecycle without two competing abstractions.

Alternative: return a configured `ProcessStartInfo`. Rejected because it is mutable, couples construction to launcher mechanics, and makes it easier for later code to append a shell command or environment mutation. Alternative: return one command string. Rejected because quoting rules differ across Windows and Unix and invite injection/escaping bugs.

### Capture runtime facts once behind an injected seam

A narrow runtime-facts provider captures the production facts needed for resolution: current process path, current entry-assembly identity/location, the compile-time known Web application identity, current working directory, and OS path semantics. A filesystem seam supplies absolute-path normalization and existence/type observations. The pure resolver consumes only the resulting immutable snapshot.

Tests construct snapshots directly. They do not ask the running MSTest process to identify the Web target and do not start a child. Production wiring may use `Environment.ProcessPath`, the actual entry assembly, and the current directory only at this adapter boundary. The adapter must identify the known Web application explicitly; it does not treat an arbitrary entry assembly as the target.

Alternative: derive `ImmichReverseGeo.Web.dll` from `AppContext.BaseDirectory` or scan nearby files. Rejected because the test output contains a copied Web DLL next to a test apphost. Alternative: inject only a path string. Rejected because it cannot prove that a `dotnet` assembly or native apphost represents the current Web application.

### Accept exactly two validated launch shapes

For framework-dependent execution, the current executable must be an absolute existing platform-recognized `dotnet` host, and the current entry assembly must match the known Web application and have an absolute existing DLL path. The descriptor uses the exact current host path and arguments:

1. absolute Web assembly path;
2. exact `--internal-worker`.

This covers local `dotnet run`/`dotnet ImmichReverseGeo.Web.dll` and the current container, where the resolved values are the current dotnet executable and `/app/ImmichReverseGeo.Web.dll`. It does not search PATH for another dotnet host.

For apphost execution, the current executable must be absolute and existing; its platform-normalized filename identity and the entry-assembly identity must both match the known Web application. The descriptor uses the current executable and one argument, `--internal-worker`. This supports a valid current-apphost layout whether framework-dependent or self-contained; choosing or producing such a publish layout is not part of this change.

Windows normalization accounts for `.exe` and Windows path comparison; Unix uses its native case-sensitive identity/path rules. The injected path policy owns these differences so tests do not depend on the host OS running the suite.

Alternative: infer mode from whether a sibling DLL happens to exist. Rejected because publish/test directories can contain both forms. Alternative: silently fall back from an invalid apphost to `dotnet` or vice versa. Rejected because ambiguity can launch the wrong program.

### Reject ambient test hosts and unresolved entrypoints

Resolution requires agreement among current executable shape, current entry assembly, and known Web identity. A Microsoft.Testing.Platform process fails this check even when `ImmichReverseGeo.Web.dll` is copied beside it. The resolver never substitutes the test entry assembly, never scans the test output, and never returns a partial descriptor.

Use a small stable failure taxonomy for unavailable process path, invalid/non-absolute path, missing target, invalid working directory, entry-application mismatch, apphost mismatch, and unsupported/ambiguous layout. Diagnostics name only the fact category and safe remediation; they do not echo raw paths, input, environment, exceptions, or stacks. The exact category names can be finalized against existing block 18 result conventions during implementation without merging the two responsibilities.

### Inherit environment and preserve working-directory context

The descriptor records the validated absolute current working directory and a policy to inherit the current environment without a clear-and-rebuild pass, preserving `CONFIG_DIR`, `DATA_DIR`, `DB_*`, ASP.NET/.NET variables, and deployment-specific providers without storing secret values. Reserved child-only protocol selectors are the sole exception: v1 launch removes `IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION`, while block 47 may set its exact v2 value during explicit protocol negotiation.

Configuration remains file/environment based: settings continue through `CONFIG_DIR/settings.json` and mounted config/data roots; database settings remain environment-only. The executable and argument list contain only entrypoint material and the private selector. Descriptor formatting, default `ToString`, failures, and logs must not enumerate environment, render a reconstructable command line, or echo raw invalid paths. Tests use canary secrets in every input channel to prove redaction.

Alternative: copy only a hard-coded environment allow-list. Rejected because it can drop ASP.NET/.NET or deployment-provider settings and drift from the parent. Alternative: serialize environment into the descriptor. Rejected because the builder needs only an inheritance policy and secret-bearing snapshots create logging/retention risk.

### Describe pipe topology without owning pipe lifecycle

All three redirect flags are part of the descriptor because the worker protocol requires stdin requests and clean protocol stdout/stderr separation. This is static process-start intent only. Block 25 remains solely responsible for creating the process, concurrently draining output, request delivery, exit/wait/cancellation ordering, and resource cleanup. No tests in block 24 launch a process; real process-boundary fixture coverage starts in blocks 25–26.

## Risks / Trade-offs

- [Runtime APIs are absent or differ in unusual hosts] → Fail with a stable unresolved/unsupported category rather than guess; keep facts injectable for supported-host additions.
- [A renamed apphost could be legitimate] → Reject it until there is an explicit trusted identity source; strict same-application identity is safer than filename heuristics.
- [File existence can change after construction] → Validate at snapshot time; block 25 still owns normal start failure handling without redefining resolution.
- [Full environment inheritance carries unrelated parent values] → Match normal child-process semantics and current configuration behavior; never enumerate or log the values in this layer.
- [Descriptor redirection flags appear close to launcher work] → Treat them only as declarative requirements and prohibit process/stream ownership here.
- [Current source does not yet contain applied blocks 18–20] → Re-read their finalized source before implementation and stop for reconciliation if exact role/host seams differ; do not invent parallel role logic.

## Migration Plan

1. Re-read applied blocks 18–20 and identify the exact known-Web identity and role-token constants without editing those changes.
2. Add immutable runtime facts, path/filesystem seams, typed result/failure, and descriptor values.
3. Implement pure validation and resolution for framework-dependent dotnet and current-apphost layouts.
4. Register the runtime adapter/builder for later block-25 consumption without starting a process.
5. Add deterministic Windows/Unix local, Docker, apphost, invalid-entrypoint, redaction, and ambient-testhost tests.
6. Run focused/default tests and strict OpenSpec validation. Rollback removes only the descriptor/resolver registration and tests; no data, image, deployment, or protocol migration exists.
