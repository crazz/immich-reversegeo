## Why

Phase 3 defines a one-shot worker protocol, but the Web control plane still lacks a process-boundary owner that can start one worker, complete its readiness/execute handshake, and observe protocol and operating-system finality without redirected-pipe deadlock. This launcher establishes that transport/session boundary before coordinator integration, graceful cancellation policy, UI projection, or crash classification.

## What Changes

- Consume block 24's validated production `WorkerCommandInvocation` and return either a typed start failure or one owned child-worker session; the underlying general `ChildProcessStartDescriptor` remains available only to process mechanics and test fixture support; never expose the platform process object.
- Start independent stdout and stderr pumps immediately after process creation, concurrently await process exit, and complete the session only after exit and both redirected streams reach finality.
- Require a valid, flushed Phase 3 `ready` event within a deterministic timeout before writing and flushing exactly one execute request; retain stdin for later control commands instead of closing it after the request.
- Deliver only codec- and stream-validator-accepted stdout events to an asynchronous caller-provided sink in accepted order, including ready and a normal terminal event.
- Expose PID and the request run ID (also called job ID), a bounded 64 KiB stderr byte tail with truncation metadata, typed startup observations, and raw terminal/exit observations without interpreting them as crashes or domain state.
- Define caller-wait cancellation, process ownership, and asynchronous disposal boundaries while deferring cancel-command, grace-period, and forced-termination policy to block 28.
- Add a deterministic injected process/clock abstraction for block-25 tests; leave the real fixture executable and process-boundary scenario matrix to [block 26](../26-add-worker-process-fixture/proposal.md).

## Capabilities

### New Capabilities
- `child-worker-launching`: Starts, handshakes with, observes, and owns one protocol-speaking worker without redirected-pipe deadlock.

### Modified Capabilities
- None.

## Impact

The change adds Web-side launcher/session contracts and production process adapters, consumes the finalized block-24 command descriptor and Phase 3 codecs, and creates a seam for deterministic tests and block-26 fixture integration. It does not register a ProcessingState bridge, select a coordinator backend, define graceful cancellation/escalation, or classify crashes and protocol failures.
