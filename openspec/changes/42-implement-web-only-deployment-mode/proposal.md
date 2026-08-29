## Why

Operators need the Immich ReverseGeo Web UI and manual processing while an external scheduler owns automatic cadence. Web-only must make that ownership unambiguous without changing saved schedule preferences or weakening the child-worker processing boundary established by Standard mode.

## What Changes

- Add an explicit `web-only` Web composition selected only from block 40's resolved deployment mode.
- Keep Kestrel, Razor/Blazor UI, manual processing, the processing coordinator, child-worker launcher, startup validation, lifecycle ownership, and existing status flow.
- Omit the internal scheduler hosted service, its cron/config waits, and its scheduled detector/dispatch path regardless of `Schedule.Enabled` or saved cron text; do not rewrite persisted settings.
- Keep the schedule editor visible with a clear Web-only policy notice, while leaving resolved-mode display and safe ProcessAssets lifecycle presentation to block 44; block 42 assigns no PID, run/job identity, or generalized worker-job UI.
- Preserve current Lookup and Data behavior during Phase 6 without hiding or gating those features. Their worker-job target state depends explicitly on blocks 47–55 and is not delivered or claimed here.
- Preserve existing ports, config/data roots, volumes, middleware, endpoints, and shutdown semantics.
- Document the external-scheduler boundary without adding a trigger endpoint, command-line alias, or any public configuration beyond `IMMICH_REVERSEGEO_MODE`.
- Add focused Web-only composition and behavioral tests; leave the all-mode matrix and production-image smoke to blocks 45–46.

## Capabilities

### New Capabilities
- `web-only-deployment-mode`: Defines the Web-hosted, manually controlled composition that structurally excludes internal automatic scheduling.

### Modified Capabilities
- None.

## Impact

Depends on the finalized block 40 mode snapshot and block 41's landed common Web, coordinator/launcher, startup-validation, and lifecycle contracts. Primary implementation areas are the Web composition root, the schedule-settings presentation, focused Web-only tests, and concise deployment-mode documentation. Block 44 owns resolved-mode and safe ProcessAssets lifecycle presentation, blocks 45–46 own cross-mode/image verification, and blocks 47–55 own Lookup/Data worker-job migration, capability-owned non-processing page state, generic arbitration diagnostics, and final heavy-Web-registration removal. Block 43 remains independently owned and is not changed here.
