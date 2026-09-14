## Context

`Program.cs` currently calls `WebApplication.CreateBuilder(args)` before any application-owned decision. The builder consumes ASP.NET command-line and environment configuration, after which it is too late to guarantee that a child worker avoids Web composition. Block 24 will launch the same framework-dependent assembly as `dotnet <entry-assembly> --internal-worker`; block 40 later owns `IMMICH_REVERSEGEO_MODE`; blocks 20 and 43 own the worker host and run-once runtime respectively.

## Goals / Non-Goals

**Goals:**
- Put one pure and deterministic role decision at the first executable startup boundary.
- Preserve arbitrary ASP.NET arguments for normal Web startup rather than duplicating framework parsing.
- Fail closed for malformed, duplicated, or augmented private-worker invocations.
- Leave a typed RunOnce result boundary that future deployment-mode resolution can supply without changing the parser's worker precedence.

**Non-Goals:**
- Parsing `IMMICH_REVERSEGEO_MODE` or defining Standard/Web-only/run-once deployment configuration (block 40).
- Splitting service registrations (block 19), constructing a Generic worker host (block 20), implementing protocol I/O/exit outcomes, or launching a worker.
- Exposing a public `web`, `run-once`, `--role`, or internal-worker operational interface.
- Implementing run-once work or lifecycle behavior (block 43).
- Adding deployment, container, Compose, or environment documentation/configuration.

## Decisions

### Use a pure selector with an explicit upstream-role candidate

The selector receives an immutable argument sequence and a typed candidate public role. The candidate defaults to Web today and is restricted to Web or RunOnce; RunOnce exists only as a future composition boundary. The result is a discriminated success/failure value: success contains exactly one role, while failure contains a stable error category and safe diagnostic and never a partial/default role.

A valid private selector returns InternalWorker regardless of whether the candidate is Web or RunOnce. This models precedence without reading environment variables in the parser and lets block 40 later translate validated deployment configuration into the typed candidate. Program in block 18 passes Web only.

Alternative: read `IMMICH_REVERSEGEO_MODE` inside this parser. Rejected because it would implement block 40 early and couple a private process-role safety decision to public deployment configuration. Alternative: represent roles as strings or nullable values. Rejected because unknown values and accidental fallback would weaken exhaustive startup branching.

### Reserve one exact private invocation

The valid private syntax is exactly one argument whose ordinal, case-sensitive value is `--internal-worker`. It is a flag and accepts no value. Therefore:

- `--internal-worker` is valid;
- a second occurrence is `duplicate-internal-worker-selector`;
- any additional argument before or after it is `unexpected-internal-worker-argument` (the duplicate category takes precedence when another exact selector is present);
- `--internal-worker=<value>`, including an empty value, is `invalid-internal-worker-syntax`; there is no missing-value state because the flag never consumes a following value;
- case variants such as `--Internal-Worker` are `invalid-internal-worker-syntax`, not aliases;
- malformed reserved forms are rejected rather than falling back to Web.

Reserved-form detection is limited to an ordinal-ignore-case equality with `--internal-worker` or an ordinal-ignore-case `--internal-worker=` prefix. Other arguments are not application-role syntax.

Alternative: accept the selector among arbitrary host arguments. Rejected because the internal process has no supported host options in this phase, and silently accepting extras could hide launcher defects or secret-bearing command-line data. Alternative: expose positional `web`/`run-once` or `--role <value>`. Rejected because deployment modes will be selected by block 40 and the worker flag is intentionally private.

### Preserve ASP.NET ownership when no private syntax is present

If no exact or malformed reserved internal selector occurs, selection succeeds with the supplied public-role candidate and returns the complete argument sequence unchanged. Unknown options, duplicate ASP.NET options, and missing ASP.NET option values are not interpreted by this parser; their behavior remains owned by the relevant host/configuration provider. With today's Web candidate this preserves the current `WebApplication.CreateBuilder(args)` contract.

`--help`, `-h`, `--version`, and `-v` are not application-role features. Without private syntax they are passed through unchanged and retain existing framework/application behavior; combined with `--internal-worker` they are rejected as unexpected extra arguments. This change adds no help/version output.

Alternative: reject every unknown argument. Rejected because that would break ASP.NET URLs, environment, and future framework switches. Alternative: parse ASP.NET switches to detect missing values. Rejected because duplicating framework grammar creates precedence and compatibility drift.

### Branch before all host and DI side effects

Top-level startup calls the pure selector before `WebApplication.CreateBuilder` and before application-owned environment/deployment resolution. A valid Web result passes the unchanged arguments into the existing builder path. InternalWorker and RunOnce are explicit non-Web branches; this block establishes the branch but does not construct their hosts. Until their owning blocks are applied, an unavailable non-Web branch must terminate without falling through to Web or constructing DI.

A parse failure writes one bounded, constant-form diagnostic to stderr and sets exit code 2. Diagnostics identify the stable category and list only the supported private syntax; they do not echo raw argument values, environment values, exception text, credentials, or stacks. No application logger or service provider is created for this path.

Alternative: parse after builder creation and merely avoid `app.Run()`. Rejected because Web configuration and service initialization have already happened. Alternative: throw and rely on the host. Rejected because there is no host yet and exception output is less deterministic and may expose input.

### Keep the internal role unsupported as a public interface

`--internal-worker` is a same-image controller/launcher selector, not a deployment mode, execution mode, or public self-hoster interface. Its token is deliberately stable for blocks 19, 24, and later, but it does not accept user data and carries no credentials, job identifiers, settings, or processing request. Worker request data later arrives through the versioned stdin protocol. Direct human invocation has no public compatibility or support guarantee beyond deterministic safe rejection/startup behavior needed by internal components.

## Risks / Trade-offs

- [A misspelled unrelated option still reaches ASP.NET] → Reserve and fail closed for case variants and assignment forms of the private token, while leaving all unrelated framework arguments untouched.
- [The RunOnce enum could be mistaken for implemented behavior] → Name it as a selection boundary in code/tests and keep Program's non-Web path unavailable until block 43; add no public token in this change.
- [Future worker host options would break the exact-one-argument grammar] → Add them only through a reviewed extension of this internal contract; do not accept unused extras preemptively.
- [A safe diagnostic can be too vague for debugging] → Include the stable error category and canonical private syntax, but never raw input.

## Migration Plan

1. Add the pure role/result/error contracts and focused deterministic tests.
2. Invoke selection as the first application-owned startup operation and pass unchanged args only to the Web branch.
3. Verify malformed internal invocations exit 2 before builder/DI side effects and valid internal selection cannot fall through to Web, including a future RunOnce candidate.
4. Roll back by removing the pre-builder branch and parser; no persisted data, public configuration, or deployment migration is introduced.
