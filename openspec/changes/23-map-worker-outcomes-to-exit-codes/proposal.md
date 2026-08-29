## Why

Operators and future launchers need one stable, portable classification for every worker process that exits normally, without treating the coarse process code as a replacement for the richer terminal protocol event. The earlier mapping does not distinguish invalid input, host infrastructure, executor failure, and stdout transport loss, leaving block 30 unable to diagnose abnormal sessions consistently.

## What Changes

- Define a closed mapped exit taxonomy: completed/no work (0), invalid invocation/request/controller-input protocol (2), busy/advisory-lock contention (3), executor/domain failure including caught OOM (4), startup/configuration/dependency/host-lifecycle failure (5), worker-output protocol generation or stdout transport failure including broken pipe (6), and cooperative cancellation or host shutdown (130).
- Explicitly leave forced termination, unhandled crashes, and uncatchable OOM unmapped because operating systems expose signals and exception-status codes differently.
- Define deterministic precedence when more than one mapped condition is observed, including failures during terminal flush and host disposal.
- Preserve a successfully flushed terminal NDJSON event as authoritative for a run that entered the executor/reporter session; post-acceptance input failure and later lifecycle faults remain independent process classifications, while future busy contention enters the exactly-once executor/reporter session, emits the existing failed terminal with safe busy detail, and performs no domain/heavy work.
- Define readiness and pre-request behavior, safe bounded stderr diagnostics, assignment of the process exit code only after required stdout flush and lifecycle disposal, and no automatic retry meaning for any code.
- Reserve code 3 exclusively for block 31 advisory-lock contention; this block defines and tests the mapping but does not implement locking.

## Capabilities

### New Capabilities
- worker-process-exit-outcomes: Stable, portable worker outcome classification, precedence, terminal-event relationship, diagnostics, and lifecycle completion contract.

### Modified Capabilities
- None.

## Impact

Depends on the worker host, stdout emitter, stdin request loop, protocol terminal events, and Phase 2 typed run result. It establishes the exit contract consumed by future launcher/runtime classification in block 30 and the busy outcome implemented by block 31. It changes only worker-role process completion behavior; Web-role startup and the Overture export tool remain unchanged.
