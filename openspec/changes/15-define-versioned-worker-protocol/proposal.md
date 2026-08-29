## Why

Blocks 7–8 define transport-neutral run and event facts, but a worker and controller still lack a stable wire contract for identifying, correlating, ordering, validating, and evolving those facts. Defining that compatibility boundary before process I/O prevents later stdin, stdout, launcher, and failure-handling changes from inventing incompatible formats.

## What Changes

- Define the v1 worker-to-controller envelope, fixed protocol identifier/version, direction/category/type discriminators, run correlation, sequence, event timestamp, and typed payload rules.
- Make protocol `runId` exactly the block-7 processing run ID; “job ID” is an alias, not a second identity. Keep the first process-scoped `ready` message uncorrelated with a run.
- Map the block-8 event vocabulary to ready, lifecycle, progress, activity, diagnostic, and distinct completed/cancelled/failed terminal wire messages.
- Specify canonical JSON names and primitive representations, strict UTF-8 NDJSON framing, a 1,048,576-byte message limit, stdout purity, and stderr separation as transport invariants.
- Define additive unknown-field tolerance and fail-closed behavior for unknown types, protocol identifiers/versions, malformed data, invalid order, correlation changes, and cardinality violations.
- Add deterministic single-message codec and stream-lifecycle validation tests without starting a host or process.
- Defer controller requests/commands to block 17, stdout emission to 21, stdin reading to 22, exit codes to 23, and launcher-side parsing/fault classification to 25/30.

## Capabilities

### New Capabilities

- `worker-protocol-events`: Versioned worker-to-controller event envelopes, compatibility, framing, validation, and lifecycle semantics.

### Modified Capabilities

- None.

## Impact

Implementation is limited to a dependency-light worker-protocol contract/codec boundary and focused MSTest coverage. It consumes the block-7 request/result and block-8 event meanings without changing processing, `ProcessingState`, scheduling, hosting, process launch, standard streams, stdin, exit codes, or UI behavior.
