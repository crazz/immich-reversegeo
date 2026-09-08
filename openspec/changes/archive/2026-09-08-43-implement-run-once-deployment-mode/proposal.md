## Why

Cron, Docker Compose jobs, and external schedulers need a public way to run one Immich ReverseGeo processing pass and receive a stable process result without keeping a Web server alive. Reusing the private child-worker protocol would add an unnecessary controller/process boundary and would expose the wrong operational contract.

## What Changes

- Make the public Run-once deployment mode compose the existing worker execution services in the invoking process, create one RunOnce processing request, perform one authoritative pass, dispose, and exit.
- Exclude Kestrel, HTTP endpoints, Razor/Blazor, the internal scheduler, Web control-plane services, child launch, and the private stdin/stdout worker protocol from Run-once composition.
- Reuse the established advisory-lock gate immediately after run-started, the executor's exact eligibility count and zero-work behavior, and the established stable process outcome taxonomy where it applies.
- Emit human-readable operator logs instead of NDJSON because Run-once has no controller; expose automation outcomes through stable exit codes.
- Treat SIGTERM/SIGINT as cooperative cancellation, perform no automatic retry, and document one-shot Docker Compose/cron operation with separate config and data mounts.

## Capabilities

### New Capabilities
- `run-once-deployment-mode`: Defines one-shot, non-Web execution, lifecycle, operator output, process outcomes, and external-scheduler operation for public Run-once mode.

### Modified Capabilities
- None.

## Impact

The public startup branch and role-specific composition in `src/ImmichReverseGeo.Web` gain a Run-once host path that consumes the finalized block 40 mode snapshot and the existing worker-only executor, reporter, initialization, advisory-lock, and outcome contracts. Focused composition, lifecycle, exit-mapping, signal, and documentation tests are added; the neutral image, entrypoint, database schema, persisted settings, ports, and private `--internal-worker` grammar remain unchanged.
