## 1. Verify prerequisite boundaries

- [x] 1.1 Re-read the applied block-15 protocol, block-20 host lifecycle, block-21 emitter, and finalized block-22 stdin-loop APIs; stop rather than duplicate their run identity, terminal, stream, or disposal ownership.
- [x] 1.2 Confirm the block-18 invalid private invocation remains code 2 and identify the single top-level InternalWorker return boundary without changing the Web role or export tool.

## 2. Define the outcome taxonomy

- [x] 2.1 Add dependency-light typed worker outcomes and stable constants for completed 0, invalid invocation/request/controller-input protocol 2, reserved busy 3, executor/domain failure 4, host infrastructure 5, output transport 6, and cancellation/host shutdown 130.
- [x] 2.2 Add the deterministic precedence combiner for output, infrastructure, invalid input, busy, executor failure, cancellation/shutdown, and completion, while keeping abrupt termination outside the mapper.
- [x] 2.3 Add bounded predefined stderr diagnostic metadata with stable outcome tokens and phases, excluding raw input, configuration, exceptions, stacks, secrets, SQL, and protocol payloads.

## 3. Integrate orderly process completion

- [x] 3.1 Consume block-20/22 typed finality to map invalid role/request/controller-input protocol, clean pre-request EOF, unexpected stdin I/O, startup/configuration/dependency, executor result including caught OOM, host shutdown, and reserved first-executor-step busy outcomes without duplicating host lifecycle ownership.
- [x] 3.2 Have the block-20 finality adapter return recognized block-21 mapping/lifecycle-validation/serialization/write/flush/broken-state failures as a discriminated non-throwing outcome mapped to 6; retain any exception escaping the generic finality hook as infrastructure 5, preserve the permanent broken state, and perform no retry or synthetic terminal.
- [x] 3.3 Ensure the exactly-once executor/reporter attempts one existing completed/cancelled/failed terminal when stdout is healthy, including failed-with-safe-busy-detail after a first-step contention gate; preserve the attempt across post-acceptance input failure, while pre-request outcomes start no run and emit no terminal.
- [x] 3.4 Let block 21 own emitter finality and block 20 own lease/scope/host cleanup, collect their typed facts in one outcome accumulator, then apply precedence and write one marked final summary through a still-live top-level stderr writer before returning the integer.
- [x] 3.5 Keep services free of Environment.Exit, Environment.ExitCode assignment, fail-fast, and process termination calls; leave code 3 unreachable until block 31 supplies contention.

## 4. Verify taxonomy and precedence

- [x] 4.1 Add pure mapping tests for every code, code-3 exclusivity, caught versus unhandled-OOM boundary, values in 0..255, and the complete pairwise/multi-failure precedence matrix.
- [x] 4.2 Add deterministic gated host tests for invalid invocation, pre-ready startup/configuration failure, ready failure, ready then clean/partial/invalid request, unexpected stdin I/O, accepted completion/no work/failure/cancellation, first-executor-step busy with the existing failed terminal and no domain/heavy work, neutral teardown-induced read cancellation/disposal, and shutdown before/during/after terminal.
- [x] 4.3 Add injected stdout tests for serialization, write, partial-write, flush, broken-pipe, terminal-flush, and emitter-disposal failures; assert code 6, no retry, no synthetic terminal, and terminal authority when a terminal had already flushed.
- [x] 4.4 Add disposal-order tests proving the process integer is returned only after output and lifecycle cleanup, late output/infrastructure faults override lower outcomes, and stderr failure neither retries nor changes classification.
- [x] 4.5 Add safe-diagnostic tests proving exactly one marked final exit summary after any earlier stderr logs, using a writer alive after provider disposal, without raw arguments, request bytes, payloads, configuration, exception text/stack, credentials, SQL, or stdout contamination.
- [x] 4.6 Run focused worker outcome/host tests on Windows and Linux CI, the normal default-exclusion suite, strict OpenSpec validation, and scope review confirming no block-22, launcher, advisory-lock implementation, block-30 classification, or retry behavior was added.
