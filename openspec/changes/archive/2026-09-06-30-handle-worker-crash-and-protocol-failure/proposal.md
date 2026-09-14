## Why

A child worker can fail before readiness, violate the protocol, lose its output transport, exit without a terminal event, or require forced termination outside the executor's ordinary exception path. The Web control plane needs one deterministic finalization policy that converts those raw observations into an authoritative run outcome without leaving UI activity or coordinator ownership stuck.

## What Changes

- Add one run-scoped classifier/finalizer over the raw observations already owned by the launcher, event bridge, cancellation policy, host-shutdown policy, and worker exit mapper.
- Classify start and readiness failures; malformed, oversized, unknown, incompatible, sequence, correlation, lifecycle, activity-cardinality, EOF, and missing-terminal faults; output/sink failures; crashes and unmapped exits; cancellation, forced kill, and host shutdown; and terminal/exit contradictions.
- Preserve a valid terminal that was committed through the state bridge as authoritative, while recording later or independent process anomalies without a second UI terminal mutation.
- For a run with no committed terminal, use one bounded internal fault-containment path when needed, publish exactly one control-plane terminal outcome after process and stream finality, clean up activities, and release the matching coordinator handle.
- Retain the launcher's bounded stderr tail, but expose only bounded redacted diagnostics derived from typed facts and safe worker summaries.
- Define deterministic seam and block-26 fixture coverage with no automatic retry and no changes to future advisory-lock acquisition.

## Capabilities

### New Capabilities
- `worker-failure-recovery`: Deterministically reconciles child-process, protocol, projection, cancellation, shutdown, and terminal evidence into one control-plane outcome and cleanup decision.

### Modified Capabilities
- `processing-run-coordination`: Adds identity-checked abnormal finalization when no worker terminal commits.
- `worker-event-state-bridge`: Adds typed semantic-rejection, definite-noncommit, and indeterminate-receipt handoff facts.

## Impact

Planning affects the future Web control-plane finalizer that composes finalized block-23 exit semantics with block-25 launcher observations, block-27 projection receipts and abandonment cleanup, block-28 cancellation/kill facts, and block-29 shutdown ownership. It does not change protocol records, launcher stream ownership, worker exit mapping, retry policy, PostgreSQL locking, or block 31.

## Audit Reconciliation

There is one exact-session internal deadline, started by whichever happens first: accepted Stop, host shutdown, or fault containment. It is the block-28 internal exact 10-second `TimeProvider` deadline, never a second timer. Classification must keep semantic rejection (a definite invalid/contradictory event), noncommit (no authoritative terminal commit), and indeterminate receipt (a terminal/projection attempt whose authoritative commit cannot be known) distinct; none may be silently upgraded to a committed terminal. The coordination and worker-event bridge capability contracts are modified to expose these bounded observations and finalization handoff without changing UI projection ownership.

