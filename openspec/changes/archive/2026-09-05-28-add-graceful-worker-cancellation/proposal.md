## Why

A child worker needs a single bounded Stop path that preserves cooperative cancellation when possible without allowing an unresponsive process, redirected pipe, or concurrent caller to strand coordinator ownership. The existing plan does not yet define command eligibility, races, escalation evidence, disposal, or deterministic timing precisely enough for blocks 29 and 30 to reuse safely.

## What Changes

- Extend the owned block-25 child-worker session with one idempotent cancellation operation bound to its exact run and process generation.
- Accept Dashboard Stop only for the coordinator's current active run; concurrent or repeated callers join the same operation and never emit duplicate commands, deadlines, or kills.
- Latch Stop during startup, but write and flush exactly one canonical Phase 3 cancel frame only after that same session has successfully written and flushed execute and remains eligible for control input.
- Use an injected `TimeProvider` and one fixed internal 10-second grace policy. This change adds no persisted setting or Settings-page ownership.
- Preserve cooperative terminal, raw exit, stdout/stderr finality, stdin failure, kill, and platform-failure facts for block 30 without classifying or rewriting run state.
- On grace expiry while the exact process remains alive, request `Kill(entireProcessTree: true)`, then await the existing process-exit and stdout/stderr-drain lifecycle before releasing resources.
- Link the worker request lease's cooperative signal into the executor token while documenting that synchronous native work which does not observe cancellation may require forced termination.
- Reuse block 26's cooperative and unresponsive fixture modes for deterministic command, race, grace, kill, drain, and cleanup tests.

## Capabilities

### New Capabilities
- `child-worker-cancellation`: Provides exact-session cooperative Stop, deterministic grace escalation, raw cancellation evidence, and single-owner cleanup for active child workers.

### Modified Capabilities
- `child-worker-launching`: Replace the temporary non-escalating disposal rule with disposal joining the exact session's cancellation and settlement lifecycle.

## Impact

Planning affects the Phase 2 coordinator's active-handle/Stop surface, the block-25 session and process abstraction, the block-22 worker cancellation-token handoff, Dashboard Stop binding, and block-26 fixture tests. It preserves block-17 wire semantics, block-23 terminal/exit authority, block-27 projection ownership, and block-30 classification ownership. Block 29 may call this same operation during host shutdown but owns admission closure and shutdown-timeout composition. Mode/deployment setting ownership remains later work.

## Audit Reconciliation

The one bounded escalation decision uses exactly one internal, exact-session 10-second deadline measured through `TimeProvider`; it is not configurable and creates no current or future public setting. After that deadline, raw process exit suppresses one tree-kill attempt; a live owned process receives at most one attempt. A terminal frame alone never settles process ownership.
