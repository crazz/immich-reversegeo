## Why

The three deployment modes share one executable but intentionally compose different hosts, trigger paths, and heavy-work placement. Focused tests in blocks 40–43 protect each feature in isolation; block 45 adds one hermetic cross-mode contract so later registration changes cannot make a forbidden scheduler, Web server, child launcher, or in-process executor reachable in the wrong role.

## What Changes

- Add a table-driven startup-selection matrix for the missing Standard default and the exact `standard`, `web-only`, and `run-once` environment values, plus invalid pre-host failure and private `--internal-worker` precedence.
- Add descriptor/provider composition assertions for Standard, Web-only, Run-once, and InternalWorker covering host type, Web/UI/server services, scheduler policy, coordinator/launcher, executor/geodata placement, startup validation, and singleton/hosted alias identity.
- Add hermetic trigger/lifecycle matrix coverage proving Standard manual and automatic child dispatch, Web-only manual-only child dispatch, and one direct in-process Run-once pass with no child.
- Keep every matrix case free of live PostgreSQL, geodata files/downloads, Docker, real child processes, and bound TCP ports; use explicit fakes and construction sentinels at external/heavy boundaries.
- Isolate immutable environment snapshots so cases can run in parallel without process-environment leakage; any unavoidable process-environment fixture must restore state and run non-parallel.
- Reuse, rather than duplicate, the focused selection, mode-specific lifecycle, UI, process-signal, and outcome tests owned by blocks 40–44. Leave production image, real entrypoint, port, UID, and mounted-volume smoke to block 46.

## Capabilities

### New Capabilities
- `deployment-mode-composition-tests`: Defines the hermetic cross-mode selection, service-graph, trigger, placement, identity, and isolation regression matrix.

### Modified Capabilities
- None.

## Impact

Implementation is limited to the smallest landed composition/startup test seam and MSTest coverage under `tests/ImmichReverseGeo.Tests/`. It consumes the finalized/applied block 40–44 contracts and may add test-only fakes or construction sentinels, but introduces no public mode, runtime behavior, persisted setting, database/geodata fixture, Docker harness, listener, or production worker process.
