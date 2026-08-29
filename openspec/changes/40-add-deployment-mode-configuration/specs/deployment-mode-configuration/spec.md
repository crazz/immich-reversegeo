## Purpose

Defines a deterministic, compatible, and secret-safe public startup configuration contract for selecting an Immich ReverseGeo deployment mode before later changes implement each mode's composition.

## ADDED Requirements

### Requirement: Deployment mode has one public source and exact values
The system SHALL source public deployment mode only from the process environment variable `IMMICH_REVERSEGEO_MODE`. Its accepted values SHALL be the exact ordinal lowercase strings `standard`, `web-only`, and `run-once`, mapping respectively to Standard, Web-only, and Run-once. Command-line arguments, `AppConfig`, `settings.json`, and the Settings UI MUST NOT provide aliases or precedence for this public setting.

#### Scenario: Each exact value is accepted
- **WHEN** `IMMICH_REVERSEGEO_MODE` is exactly `standard`, `web-only`, or `run-once`
- **THEN** the corresponding Standard, Web-only, or Run-once deployment-mode value is selected

#### Scenario: Case and whitespace are not normalized
- **WHEN** the variable contains a case variant, leading or trailing whitespace, an empty string, or only whitespace
- **THEN** deployment-mode resolution fails rather than trimming, case-folding, or defaulting the value

### Requirement: Missing mode preserves Standard compatibility
Only an absent environment variable SHALL resolve to Standard. This default MUST preserve the existing deployment behavior as the input to block 41; this change itself MUST NOT implement or alter Standard composition.

#### Scenario: Variable is absent
- **WHEN** `IMMICH_REVERSEGEO_MODE` is not present in the process environment
- **THEN** the immutable effective deployment mode is Standard

#### Scenario: Existing deployment omits the new variable
- **WHEN** an existing Docker or host deployment upgrades without adding `IMMICH_REVERSEGEO_MODE`
- **THEN** configuration selection succeeds with Standard and requires no settings-file migration

### Requirement: Invalid configuration fails safely before startup side effects
An unsupported deployment-mode value SHALL fail with category `invalid-deployment-mode`, write one bounded constant-form diagnostic to stderr naming `IMMICH_REVERSEGEO_MODE` and listing `standard`, `web-only`, and `run-once`, and terminate with exit code 2. The diagnostic MUST NOT echo the supplied value, environment contents, command-line arguments, exception text, connection strings, credentials, or other secrets. Failure MUST occur before role-specific host construction or startup, dependency registration/provider creation, application logging, path resolution, or filesystem access.

#### Scenario: Unsupported value contains a canary secret
- **WHEN** the variable contains an unsupported secret-bearing value
- **THEN** startup exits 2 with `invalid-deployment-mode` and the accepted values, without reproducing the supplied value or performing startup side effects

#### Scenario: Empty value is explicitly configured
- **WHEN** the environment contains `IMMICH_REVERSEGEO_MODE` with an empty value
- **THEN** startup fails as invalid rather than treating the variable as absent

### Requirement: Private internal-worker selection has precedence
The finalized private role syntax owned by block 18 SHALL be evaluated before public deployment-mode resolution. A valid exact sole `--internal-worker` invocation MUST select InternalWorker without reading or validating `IMMICH_REVERSEGEO_MODE`, even when the inherited variable is missing, invalid, Standard, Web-only, or Run-once. Malformed, duplicate, or augmented reserved private syntax SHALL retain block 18's existing failure category, diagnostic, and exit behavior and MUST NOT be replaced or masked by a deployment-mode error. The private token MUST NOT become a documented public deployment mode.

#### Scenario: Internal worker inherits an invalid public mode
- **WHEN** the complete invocation is the exact `--internal-worker` token and the inherited deployment-mode variable is invalid
- **THEN** InternalWorker is selected and the deployment-mode source is not read

#### Scenario: Reserved private syntax is malformed while mode is invalid
- **WHEN** block 18 rejects reserved internal-worker syntax and `IMMICH_REVERSEGEO_MODE` is also invalid
- **THEN** the existing private-role failure wins and deployment-mode resolution is not attempted

#### Scenario: Ordinary host arguments are supplied
- **WHEN** no reserved private syntax is present and ordinary ASP.NET arguments are supplied
- **THEN** deployment mode is resolved and block 18 preserves those arguments unchanged for the selected public role

### Requirement: Effective mode is an immutable startup-only snapshot
For a non-internal public invocation, the system SHALL read the deployment-mode source once during the pre-host startup decision and expose one immutable effective value to later composition. The running process MUST NOT poll, watch, reload, or change deployment mode after startup; changing the environment requires a process or container restart.

#### Scenario: Environment changes after resolution
- **WHEN** the process environment is changed after the effective mode has been resolved
- **THEN** the running process retains its original deployment-mode value until restart

### Requirement: Deployment mode is excluded from persisted settings
Deployment mode SHALL NOT be a member of persisted `AppConfig`, SHALL NOT be serialized to `<configDir>/settings.json`, and SHALL NOT be read through `ConfigService`. Saving other settings MUST NOT copy the environment-backed mode or any environment value into settings storage.

#### Scenario: Settings are saved while mode is configured
- **WHEN** `IMMICH_REVERSEGEO_MODE` is set and `AppConfig` is saved
- **THEN** `settings.json` contains no deployment-mode field or value

### Requirement: One container image exposes the environment contract
The production image SHALL continue to use one image and the existing entrypoint for all public modes. The Dockerfile MUST NOT bake any `IMMICH_REVERSEGEO_MODE` value into the image; absence therefore exercises the compatible Standard default. Docker and Compose users SHALL be able to set one of the exact accepted values through the container environment without changing the image or entrypoint. The reference Compose file and public Docker-first configuration documentation SHALL describe the optional variable, exact values, Standard default, restart requirement, and exclusion from `settings.json` without advertising `--internal-worker`.

#### Scenario: Reference Compose omits the optional variable
- **WHEN** the reference Compose service is started without a deployment-mode entry
- **THEN** the container uses the same image and entrypoint and resolves Standard

#### Scenario: Operator selects a public mode
- **WHEN** a supported value is supplied through the container environment
- **THEN** the same image accepts that startup selection without a settings-file change
