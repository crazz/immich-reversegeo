## Why

A Web shutdown can race child admission, startup, execution, and terminal cleanup. The host needs one idempotent teardown owner that closes admission first and never leaves a child process, redirected stream, or coordinator/state lease behind.

## What Changes

- Atomically close worker-job admission at the first Web-host stopping signal before requesting cancellation.
- Join or start block 28's finalized exact-session cancellation task across pending, starting, ready, running, and terminal-cleanup races; reuse its injected-`TimeProvider` deadline and fixed internal 10-second grace without resetting or shortening it.
- Compose that one graceful-cancel/tree-kill/drain/disposal lifecycle with the Generic Host shutdown budget without treating host-token cancellation as permission to orphan a worker.
- Make repeated shutdown notifications, concurrent user Stop, startup failure, and already-terminal sessions converge on the same idempotent cleanup.
- Close matching bridge activities and release coordinator ownership after session cleanup without inventing a terminal result or classifying raw launcher outcomes reserved for block 30.

## Capabilities

### New Capabilities

- `worker-shutdown-control`: Quiesces Web-owned child-worker admission and safely joins worker-session cleanup during host shutdown.

### Modified Capabilities

- `processing-run-coordination`: Extend the existing shutdown fence and exact-handle cleanup to child sessions; host-token expiry cannot abandon cleanup, and shutdown-owned failures do not invent a fatal projection.

## Impact

The future Web coordinator/session integration, Generic Host lifecycle registration, block-25 child session, block-27 bridge cleanup, and block-28 cancellation policy are affected. Block 29 adds composition and tests only after those prerequisites exist; it does not redefine their protocol, cancellation, launcher, projection, or outcome-classification contracts.

## Audit Reconciliation

Shutdown is clean only after the exact owned worker has exited and both stdout and stderr drains have reached finality, followed by exact-handle cleanup. A rejected or failed tree kill leaves the session unresolved: shutdown must retain ownership/failure evidence and must not report clean completion, release the handle as settled, or treat a terminal frame alone as sufficient.

