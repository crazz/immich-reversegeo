# application-role-selection Specification

## Purpose

Selects the private internal-worker process role, the default Web role, or a future run-once boundary deterministically before any host or dependency-injection construction.

## Requirements

### Requirement: Pure deterministic role result
The system SHALL expose a framework-independent role selector whose immutable input is an argument sequence and a typed public-role candidate restricted to Web or RunOnce. It SHALL return either exactly one of Web, InternalWorker, or RunOnce, or exactly one structured failure with a stable category and safe bounded diagnostic. A failure MUST NOT contain a partial/default role, raw arguments, environment values, exceptions, stacks, or secrets.

#### Scenario: Repeated pure selection
- **WHEN** the same arguments and candidate role are selected repeatedly
- **THEN** each result is value-equivalent and no host, environment, filesystem, logging, or dependency-injection state is read or mutated

#### Scenario: Invalid selection result
- **WHEN** private role syntax is malformed, duplicated, or augmented
- **THEN** selection returns one failure and no role value

### Requirement: Default Web role and ASP.NET argument preservation
When no reserved internal-worker syntax is present, the system SHALL select the supplied public-role candidate, which SHALL default to Web in this change. It SHALL preserve the complete argument sequence unchanged for the selected host path. Unknown, duplicate, or missing-value behavior for non-reserved ASP.NET arguments SHALL remain owned by ASP.NET or its configuration providers and MUST NOT be reinterpreted as an application-role error.

#### Scenario: No arguments
- **WHEN** the executable receives no arguments and no later deployment-derived candidate has been supplied
- **THEN** it selects Web and preserves an empty argument sequence

#### Scenario: Ordinary ASP.NET arguments
- **WHEN** arguments contain no reserved internal-worker form
- **THEN** the selected public role is returned and every argument is preserved in its original value and order

#### Scenario: Unknown non-role option
- **WHEN** the executable receives an option that is not a reserved internal-worker form
- **THEN** role selection does not reject or normalize it and leaves its interpretation to the selected host path

### Requirement: Exact private internal-worker syntax
The only command-line role syntax introduced by this change SHALL be the single exact ordinal case-sensitive flag `--internal-worker`. The flag SHALL accept no value and no companion argument. The selector SHALL reserve ordinal case variants of that name and assignment forms beginning `--internal-worker=` so malformed private invocations fail closed instead of selecting Web.

#### Scenario: Valid internal worker invocation
- **WHEN** the complete argument sequence is exactly `--internal-worker`
- **THEN** the selector returns InternalWorker and no remaining host arguments

#### Scenario: Duplicate internal worker selector
- **WHEN** the exact selector occurs more than once
- **THEN** selection fails with `duplicate-internal-worker-selector`, taking precedence over the general extra-argument failure

#### Scenario: Additional internal worker argument
- **WHEN** one exact selector occurs with any other argument before or after it
- **THEN** selection fails with `unexpected-internal-worker-argument`

#### Scenario: Assigned or missing value form
- **WHEN** an argument is `--internal-worker=` or `--internal-worker=<value>`
- **THEN** selection fails with `invalid-internal-worker-syntax` because this flag accepts no value and therefore has no separate missing-value state

#### Scenario: Case-varied private selector
- **WHEN** an argument differs from `--internal-worker` only by case
- **THEN** selection fails with `invalid-internal-worker-syntax` rather than selecting InternalWorker or falling back to Web

### Requirement: Internal-worker precedence over deployment-derived roles
A syntactically valid internal-worker invocation SHALL select InternalWorker before and independently of public deployment-mode configuration. It MUST override either a Web or RunOnce typed candidate and MUST NOT read, validate, or be rejected by an ASP.NET environment value or future deployment-mode value.

#### Scenario: Internal worker with Web candidate
- **WHEN** the complete arguments are `--internal-worker` and the public-role candidate is Web
- **THEN** InternalWorker is selected

#### Scenario: Internal worker with future RunOnce candidate
- **WHEN** the complete arguments are `--internal-worker` and the public-role candidate is RunOnce
- **THEN** InternalWorker is selected without interpreting deployment configuration

### Requirement: Future run-once boundary without current run-once interface
The role contract SHALL contain RunOnce as a typed public-role candidate/result for later deployment-mode composition. This change MUST NOT introduce a public `run-once`, `web`, or `--role` command-line form, read `IMMICH_REVERSEGEO_MODE`, execute processing, or construct a run-once host.

#### Scenario: Typed future run-once selection
- **WHEN** the supplied public-role candidate is RunOnce and no reserved internal-worker syntax is present
- **THEN** the selector returns RunOnce and preserves all arguments without performing run-once work

#### Scenario: Positional run-once text
- **WHEN** today's executable receives `run-once` merely as an argument with the default Web candidate
- **THEN** the role parser does not treat it as a role selector and preserves it for the Web path

### Requirement: Selection and failure precede host construction
The executable SHALL complete role selection before `WebApplication.CreateBuilder`, other host construction, application-owned deployment/environment resolution, DI registration, service-provider creation, filesystem initialization, or application logging setup. A selection failure SHALL write a constant-form bounded diagnostic to stderr, set process exit code 2, and terminate without falling through to Web. InternalWorker and RunOnce branches MUST NOT fall through to the Web builder; their hosts and runtime behavior remain owned by later changes.

#### Scenario: Invalid private invocation
- **WHEN** private role selection fails
- **THEN** the process reports the stable category and canonical supported syntax to stderr, exits with code 2, and performs no host or DI construction

#### Scenario: Internal worker is selected
- **WHEN** valid private syntax selects InternalWorker
- **THEN** no Web builder is constructed before control reaches the internal-worker composition boundary

#### Scenario: RunOnce boundary is selected
- **WHEN** a future typed candidate selects RunOnce
- **THEN** no Web builder is constructed before control reaches the run-once composition boundary

### Requirement: Help, version, and support boundary
This change SHALL add no application-owned help or version behavior. Without reserved private syntax, `--help`, `-h`, `--version`, and `-v` SHALL be preserved like other ASP.NET arguments. With an exact internal-worker selector, any such argument SHALL cause `unexpected-internal-worker-argument`. The internal-worker flag SHALL be documented and tested as a private controller/launcher contract, not a public deployment mode or supported self-hoster interface, and it MUST NOT carry credentials, settings, run identifiers, or job data.

#### Scenario: Help argument on default path
- **WHEN** `--help` is supplied without reserved internal-worker syntax
- **THEN** it is preserved for the Web path and role selection emits no help text

#### Scenario: Help argument on private path
- **WHEN** `--internal-worker` and `--help` are supplied together
- **THEN** selection fails with `unexpected-internal-worker-argument` and does not expose a worker help surface

#### Scenario: Private selector carries a value
- **WHEN** data is attached to the private selector with an equals sign
- **THEN** selection rejects the invocation without echoing that data
