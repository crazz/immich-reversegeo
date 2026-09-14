## Why

In-memory launcher tests cannot prove real operating-system process, redirected-pipe, exit, and cleanup behavior. A hermetic executable is needed now to exercise the finalized v1 worker streams through the block 24/25 seams without loading Immich, PostgreSQL, geodata, or production worker composition.

## What Changes

- Add a dedicated test-only worker fixture executable that reuses the finalized v1 protocol contracts and codecs but does not resolve production worker services.
- Make the fixture a build and publish dependency of the launcher test project, with a stable cross-platform invocation path and per-test scenario arguments.
- Provide deterministic scenarios for ready/request capture, successful and no-work completion, pre-ready and post-ready crashes, malformed and oversized frames, unknown messages, invalid sequences, terminal/exit mismatches, stderr flooding, cooperative cancellation, unresponsive execution, and mapped or unmapped exit codes.
- Add real-process tests through the immutable command descriptor and child-launcher seams, using protocol/process handshakes rather than timing sleeps.
- Add test-only process ownership, isolation, and cleanup support so parallel or failed tests cannot leave fixture processes behind.
- Keep production command building, worker role selection, geodata/DI composition, cancellation policy, crash classification, UI projection, and cross-process PostgreSQL behavior unchanged.

## Capabilities

### New Capabilities

- `worker-process-fixture`: Provides a hermetic real executable and deterministic scenario contract for process-boundary launcher and later lifecycle tests.

### Modified Capabilities

- None.

## Impact

The change affects test projects, solution/build wiring, test-only fixture support, and launcher process-boundary tests. It consumes the v1 protocol/codec from blocks 15, 17, and 21–23, the general `ChildProcessStartDescriptor` process seam introduced by blocks 24–25 (not block 24's production-only `WorkerCommandInvocation`), and the raw lifecycle/session seam from block 25. Block 28 may reuse cancellation scenarios, block 30 may reuse crash/protocol/terminal-exit scenarios, and block 32 may reuse the executable packaging, but their policy assertions remain outside this change.
