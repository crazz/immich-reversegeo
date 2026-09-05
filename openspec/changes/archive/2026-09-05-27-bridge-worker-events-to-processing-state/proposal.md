## Why

The Dashboard and Logs observe the Phase 2 processing-state adapter, while a child worker reports the same run through validated Phase 3 envelopes. A controller-side bridge is needed so accepted worker events preserve existing WebUI behavior without making UI state protocol-aware or conflating readiness and transport faults with run lifecycle.

## What Changes

- Add one run-scoped, asynchronous accepted-event bridge between the block-25 launcher sink and the existing block-9 processing-state adapter.
- Treat process-scoped `ready` as handshake state only; map correlated run-started, eligibility, progress, activity, log, and terminal events into the transport-neutral adapter contract.
- Recheck accepted-event sequence, type/lifecycle, run identity, activity identity, and terminal-result coherence before projection, with typed rejection/no state mutation for block 30 to classify.
- Preserve block-9 lifecycle timing, legacy counter meanings, per-asset versus fatal-error behavior, ordered logs, scoped activity cleanup, synchronous observer notifications, and terminal summary behavior without duplicate accounting.
- Await every projection in launcher callback order so controller backpressure is lossless; define deterministic bridge disposal and nonterminal cleanup without fabricating a failed run.
- Keep worker PID and run/job identity in launcher/bridge control-plane ownership; do not add protocol or process fields to `ProcessingState` or change Razor components.

## Capabilities

### New Capabilities
- `worker-event-state-bridge`: Validates and projects accepted child-worker event streams through the existing Web processing-state adapter.

### Modified Capabilities
- None.

## Impact

The change affects the Web controller boundary that implements block 25's accepted-event sink, the block-9 adapter's narrow projection/cleanup surface, dependency registration for that bridge, and deterministic Web tests. It consumes the finalized block-9, block-15, block-21, and block-25 contracts. It does not change worker emission, launch policy, `ProcessingState`'s public model, Dashboard/Logs components, cancellation, process startup, or block-30 crash/protocol classification.

## Audit Reconciliation

A terminal received while this bridge has any open projected activity is a typed terminal-coherence rejection, not an instruction to close activities. Only a coherent accepted terminal performs normal terminal cleanup. Forced activity cleanup is limited to nonterminal bridge/session abandonment. A terminal that follows eligibility but no accepted progress is coherent only when all four result counts (`ProcessedCount`, `UpdatedCount`, `SkippedCount`, and `FailedCount`) are zero; eligibility alone never permits nonzero counts.

