## Purpose

Defines the pure, validated process-start contract by which the Web control plane describes invocation of the same Immich ReverseGeo application as the private internal worker without starting a process or exposing configuration secrets.

## ADDED Requirements

### Requirement: Immutable shell-free child invocation descriptor
The system SHALL return either one immutable validated `WorkerCommandInvocation` or one typed resolution failure. `WorkerCommandInvocation` is the production-only subtype of the general `ChildProcessStartDescriptor` consumed by process mechanics; it SHALL contain an executable path, an ordered sequence of discrete argument values suitable for `ProcessStartInfo.ArgumentList`, an absolute working directory, an inherit-current-environment policy, and the values `RedirectStandardInput=true`, `RedirectStandardOutput=true`, `RedirectStandardError=true`, `UseShellExecute=false`, and `CreateNoWindow=true`. It MUST NOT contain a shell command string, pre-quoted or escaped argument text, process/session state, or lifecycle behavior.

#### Scenario: Descriptor settings
- **WHEN** a supported current application layout is resolved
- **THEN** the descriptor requests all three standard streams redirected, disables shell execution and window creation, preserves the current working directory and environment, and represents every argument as a separate unquoted value

#### Scenario: Path containing spaces
- **WHEN** a valid executable, entry assembly, or working-directory path contains spaces or platform-significant characters
- **THEN** the original path value is retained as one descriptor field or one argument-list item without adding shell quoting or escaping

### Requirement: Exact private worker selector
The invocation SHALL contain block 18's exact ordinal argument `--internal-worker` exactly once and as the final argument. It MUST NOT add any other worker, configuration, credential, request, job, deployment-mode, help, or host argument.

#### Scenario: Framework-dependent argument order
- **WHEN** a framework-dependent application layout is resolved
- **THEN** the ordered argument values are exactly the absolute Web entry-assembly path followed by `--internal-worker`

#### Scenario: Apphost argument order
- **WHEN** a valid current-application apphost layout is resolved
- **THEN** the ordered argument values contain only `--internal-worker`

### Requirement: Validated current-application entrypoint resolution
The resolver SHALL consume an injected immutable snapshot of the current process executable, known Web application identity, current entry-assembly identity and location, current working directory, operating-system path semantics, and filesystem observations. It SHALL distinguish only these supported layouts:

1. a framework-dependent host whose current executable is the platform's `dotnet` host and whose current entry assembly is the known Web application, producing the current host executable plus the absolute existing Web assembly path; or
2. a current-application apphost whose executable identity and entry-assembly identity both match the known Web application, producing that absolute existing executable with no assembly argument.

The resolver MUST NOT discover a target from the current directory, `AppContext.BaseDirectory`, the first DLL or executable in an output directory, a copied Web assembly, a PATH search, or an unrelated ambient entry assembly. Resolution SHALL perform no process creation and no ambient filesystem, environment, assembly, or operating-system reads outside the injected snapshot/seams.

#### Scenario: Local framework-dependent application
- **WHEN** the injected facts describe the current `dotnet` executable and an absolute existing local `ImmichReverseGeo.Web.dll` as the matching entry assembly
- **THEN** resolution succeeds with that exact executable and assembly path

#### Scenario: Framework-dependent Docker application
- **WHEN** the injected facts describe the current container `dotnet` executable, working directory `/app`, and matching existing entry assembly `/app/ImmichReverseGeo.Web.dll`
- **THEN** resolution succeeds without requiring a Docker socket, container control API, or separate worker image

#### Scenario: Current application apphost
- **WHEN** the injected facts describe an absolute existing apphost whose platform-normalized executable identity and entry-assembly identity both match the known Web application
- **THEN** resolution succeeds with the current executable and no assembly argument, whether that apphost is framework-dependent or self-contained

#### Scenario: Ambient Microsoft test host
- **WHEN** the current executable or entry assembly identifies the Microsoft.Testing.Platform test application rather than the known Web application, even if a copied `ImmichReverseGeo.Web.dll` exists beside it
- **THEN** resolution fails and MUST NOT describe the test executable, test assembly, copied Web assembly, or any guessed target

### Requirement: Fail-closed validation
Resolution SHALL fail with one stable typed category when the process executable is unavailable, a required path is empty or non-absolute, the working directory is unavailable or non-absolute, a required executable or assembly does not exist, the entry assembly does not match the known Web application, the apphost identity does not match, or the layout is unsupported or ambiguous. Failure MUST NOT return a partial descriptor or silently fall back to another launch mode.

#### Scenario: Framework-dependent entry assembly unresolved
- **WHEN** the current executable is `dotnet` but the known Web entry-assembly path is empty, relative, missing, or mismatched
- **THEN** resolution returns a typed entrypoint failure and no descriptor

#### Scenario: Unsupported executable layout
- **WHEN** the current executable is neither a validated `dotnet` host nor the validated current Web apphost
- **THEN** resolution returns a typed unsupported-layout failure and no descriptor

#### Scenario: Invalid working directory
- **WHEN** the current working directory is empty, relative, or unavailable
- **THEN** resolution returns a typed working-directory failure and no descriptor

### Requirement: Environment and filesystem context inheritance
The descriptor SHALL instruct block 25 to inherit the current process environment without a clear-and-rebuild pass or secret-to-argument translation. It SHALL preserve configuration-provider settings and paths, including `CONFIG_DIR`, `DATA_DIR`, ASP.NET/.NET settings, and `DB_HOST`, `DB_PORT`, `DB_USERNAME`, `DB_PASSWORD`, and `DB_DATABASE_NAME`. Reserved child-only protocol selectors are the sole exception: a v1 launch SHALL remove `IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION`; block 47 may set its exact v2 value only during explicit protocol negotiation. Settings and database credentials SHALL continue to be resolved by the child through inherited environment and mounted config/data paths, not copied into command arguments.

#### Scenario: Credential-bearing parent environment
- **WHEN** the current environment contains database credentials and configuration/data directory values
- **THEN** the descriptor preserves those environment entries unchanged, removes only the reserved child protocol-version selector for v1, and adds none of those names or values to the executable or argument list

#### Scenario: Empty optional environment value
- **WHEN** the current environment contains an empty or otherwise valid provider value
- **THEN** the builder does not normalize, remove, log, or convert that value into an argument

### Requirement: Safe diagnostics and representations
Descriptor and failure types SHALL be safe-by-construction for application logging: default string/debug representations and resolution diagnostics MUST NOT enumerate environment entries, render a reconstructable command line, or include raw environment values, configuration contents, database credentials, injected invalid paths, exception text, or stacks. Stable bounded categories and constant-form remediation MAY identify which required fact class was invalid without echoing its value. The full descriptor MUST NOT be logged or structurally serialized as an operational event.

#### Scenario: Secret canaries
- **WHEN** executable, path, environment, or injected invalid input values contain secret canaries
- **THEN** success/failure string representations and diagnostics contain none of the canaries

### Requirement: Deterministic cross-platform resolution
For the same injected snapshot and path-semantics seam, resolution SHALL produce value-equivalent results without reading mutable ambient state. Tests SHALL cover Windows executable suffix and case behavior, Unix case-sensitive paths, separators, roots, spaces, and local/Docker/apphost/test-host layouts without launching a child process.

#### Scenario: Repeated pure resolution
- **WHEN** the same runtime snapshot is resolved repeatedly
- **THEN** the results are value-equivalent and no process, shell, environment read, filesystem probe outside the seam, or current-directory mutation occurs

#### Scenario: OS-specific apphost identity
- **WHEN** equivalent Windows and Unix snapshots use their respective executable suffix and path-comparison rules
- **THEN** each valid Web apphost is accepted only according to its injected platform semantics and unrelated test/app executables remain rejected

### Requirement: Launcher lifecycle remains separate
This capability SHALL only construct and validate `WorkerCommandInvocation`; it SHALL not construct unrestricted `ChildProcessStartDescriptor` values for fixtures or other callers. It MUST NOT instantiate `ProcessStartInfo`, call `Process.Start`, write stdin, drain stdout/stderr, parse worker events, wait for exit, classify outcomes, cancel or kill a process, expose a PID/session, retry, or dispose process resources; those behaviors remain owned by block 25 and later lifecycle blocks.

#### Scenario: Successful construction
- **WHEN** descriptor construction succeeds
- **THEN** no child process or stream is opened and the caller receives only the immutable descriptor
