## Why

Phase 6 needs the mode resolved as `standard`—including the block-40 unset default—to produce an explicit production composition without changing existing installation behavior or weakening the child-process processing boundary established in Phases 3–5.

## What Changes

- Compose the ASP.NET Core/Kestrel Web host, Razor/Blazor UI, internal scheduler, processing coordinator, scheduled work detector, and child-worker launcher for Standard mode.
- Keep manual processing and detector-positive scheduled processing child-only; keep detector-empty scheduled occurrences local and lightweight, and retain one process-local active heavy-worker admission at a time.
- Retain the current-phase Lookup and Data dependencies until their separately planned worker migration.
- Preserve the private internal-worker role in the same application assembly/image, graceful worker shutdown ownership, existing startup-failure behavior, ports, and separate config/data storage roots.
- Consume block 40's finalized resolved-mode contract without adding another parser, setting, or mode-selection path.
- Add Standard-specific composition tests and a hermetic behavioral smoke covering default startup, manual/scheduled dispatch, gating, contention, failure, and shutdown.
- Exclude mode UI/status work, Web-only and run-once composition, and Docker-wide mode smoke coverage; blocks 42–46 own those increments.

## Capabilities

### New Capabilities
- `standard-deployment-mode`: Defines the default production Web/control-plane composition and its child-only processing, compatibility, startup, and shutdown behavior.

### Modified Capabilities
- None.

## Impact

The change consumes block 40's finalized deployment-mode value and the applied Phase 3–5 role, scheduler, detector, coordinator, launcher, worker protocol, shutdown, and advisory-lock contracts. Expected implementation areas are the Web composition root and focused tests under `tests/ImmichReverseGeo.Tests/`; no persisted configuration or data migration is introduced.
