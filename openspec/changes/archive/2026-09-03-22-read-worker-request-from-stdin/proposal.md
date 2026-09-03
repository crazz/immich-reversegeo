## Why

The internal worker has pure controller-input contracts, a one-shot host seam, and a sole stdout emitter, but no bounded stdin transport that joins them. Without explicit incremental framing and command-loop lifetime rules, oversized input, EOF, cancellation races, and reader faults could create unbounded allocation, duplicate work, or ambiguous host outcomes.

## What Changes

- Give the worker one byte-oriented standard-input owner that begins reading only after the block-21 ready frame has been written and flushed.
- Incrementally validate strict UTF-8 and assemble LF/CRLF-delimited frames with the shared 1,048,576-byte object limit, without an unbounded line allocation; define clean EOF and partial-frame EOF behavior before and after request acceptance.
- Accept exactly one execute lease, preserve independent transactional controller-input sequencing, and reject duplicate execute, malformed, unknown, incompatible, or incorrectly correlated input without creating another run.
- Keep a bounded-lifetime control pump active beside execution so correctly sequenced correlated cancel is latched before entry, requests the same cooperative token during execution, and is an idempotent no-op after terminal.
- Hand safe structured EOF/input/reader outcomes to the worker-host coordination boundary for block 23 to map to process exits; emit no execute, cancel, or failure acknowledgement on stdout.
- Add deterministic chunked/faulting-stream tests for framing boundaries, cancellation races, command-loop disposal, and diagnostic safety.

## Capabilities

### New Capabilities
- `worker-stdin-request-loop`: Bounded one-run stdin framing, execute acquisition, concurrent cancel control, and safe input-finality handoff.

### Modified Capabilities
- None.

## Impact

Depends on finalized changes 15, 17, 20, and 21 plus the extracted one-shot executor. It supplies the block-20 request-lease implementation and consumes the block-21 readiness hook while preserving their host, terminal, and stdout ownership. Block 23 remains the sole owner of process exit-code mapping; launcher-side pipes and diagnostics presentation remain later work.
