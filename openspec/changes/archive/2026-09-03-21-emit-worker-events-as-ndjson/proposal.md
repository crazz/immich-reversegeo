## Why

The controller needs a byte-safe, machine-readable view of worker readiness and processing events, while parallel asset tasks and ordinary logs must never corrupt stdout framing. Block 15 defines the protocol values but deliberately leaves worker-side stream ownership, sequencing, backpressure, flushing, and transport failures to this change.

## What Changes

- Add a worker-side emitter that exclusively owns managed stdout protocol writes and emits the process-scoped `ready` frame before any run-scoped frame.
- Adapt every accepted block-8 processing-session event to its exact block-15 v1 frame, using the processing request's `RunId` as the sole run/job correlation identity.
- Allocate one strictly consecutive sequence across the worker stdout stream (`ready` is 1; run events continue from 2), serialize concurrent producers through one bounded lossless writer, and flush every accepted frame.
- Emit one contiguous strict UTF-8/no-BOM buffer containing compact JSON plus LF per frame; prohibit `Console.Out`/`Console.Write*` use on worker execution paths and route ordinary `ILogger` output to stderr.
- Define cancellation, serialization, write, flush, and broken-pipe behavior so accepted events are not silently lost, transport failure breaks the emitter without recursive protocol output, and a successfully flushed terminal is last.
- Add deterministic in-memory, saturation, concurrency, cancellation, encoding, logging-separation, sensitive-payload, and injected stream/codec fault tests.

## Capabilities

### New Capabilities
- `worker-ndjson-output`: Lossless, ordered, protocol-safe worker stdout emission for readiness and one correlated processing run.

### Modified Capabilities
- None.

## Impact

This planning change consumes the finalized block-8 reporting session and block-15 protocol codec/validator, with block 16 as compatibility evidence, and integrates through the worker composition/host supplied by blocks 19–20. It affects only worker-side event-to-frame adaptation, stdout emission, and worker logger routing. Request input (22), process exit mapping (23), launcher parsing/runtime classification (25/30), and progress coalescing (65) remain out of scope.
