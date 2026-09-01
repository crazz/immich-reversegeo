## Why

A child worker needs one immutable processing-run request and later cooperative cancellation without exposing job data in process arguments. The existing draft leaves identity, readiness, sequencing, EOF, compatibility, and error boundaries ambiguous enough for the controller and worker to implement incompatible v1 behavior.

## What Changes

- Define the v1 controller-to-worker envelope and its closed `request/execute` and `control/cancel` messages.
- Map execute input exactly to the block-7 immutable `ProcessingRunRequest`: envelope `runId` plus payload `trigger`, with no second job ID, processing mode, settings, credentials, or work-set snapshot.
- Define ready-before-request timing, exactly one execute request, independent input sequencing, correlated idempotent cancellation before/during/after execution, EOF and half-close semantics, and the decision that v1 adds no dedicated request/cancel acknowledgements.
- Reuse block 15's canonical JSON, bounded UTF-8 NDJSON framing, compatibility policy, and safe structured validation failures.
- Keep stdin stream reading and command-loop mechanics in block 22, process exit outcomes in block 23, and generic worker jobs outside this change until block 47.

## Capabilities

### New Capabilities
- `worker-control-requests`: Versioned controller-to-worker execute-request and cancellation-command contracts for one processing run.

### Modified Capabilities
- None.

## Impact

This change extends the Core worker-protocol contract and pure validation surface established by block 15. Blocks 22, 24, 25, and 28 consume the contract later; no worker host, console stream, process launcher, executor wiring, exit-code behavior, or current in-process processing behavior changes here.
