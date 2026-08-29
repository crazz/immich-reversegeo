## Why

The internal worker role can now be selected and composed without Web dependencies, but it still has no process lifecycle that starts, accepts one processing request, coordinates execution, and releases all host resources. A one-shot Generic Host is needed before protocol transports and exit mapping can be attached without falling back to the Web application.

## What Changes

- Construct the exact `--internal-worker` role with a .NET Generic Host and the finalized shared/internal-worker composition roots, never a `WebApplication`.
- Add one hosted worker-lifecycle service that creates one execution scope, completes required asynchronous worker startup, crosses a readiness hook, waits for one execute-request lease, invokes the finalized processing executor once, coordinates terminal completion, and stops the host.
- Link accepted-request cancellation with Generic Host shutdown so valid control cancellation, SIGTERM/SIGINT-driven shutdown, and explicit host stop cooperatively cancel the same execution.
- Define pre-request EOF/startup/request-acquisition failure, accepted-request completion/failure, one-request-only behavior, resource-disposal ordering, and stdout/stderr ownership boundaries without implementing stdin framing, NDJSON emission, or process exit-code mapping.
- Add deterministic host tests using in-memory lifecycle, request, executor, terminal, and disposable collaborators; bind no ports and use no database, geodata, scheduler, UI, or real console pipes.

## Capabilities

### New Capabilities
- `internal-worker-host`: One-shot non-Web Generic Host construction, lifecycle ordering, cancellation, terminal coordination, and disposal for the private internal-worker role.

### Modified Capabilities
- None.

## Impact

Depends on finalized changes 18–19, the Phase 2 executor/reporter contracts, and the request/protocol contracts from changes 15 and 17. Implementation is expected at the executable role branch plus worker-host lifecycle seams and focused MSTests. Changes 21, 22, and 23 remain authoritative for NDJSON stdout emission, bounded stdin reading/control-loop mechanics, and process exit-code mapping respectively.
