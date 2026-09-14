## Context

See [proposal.md](proposal.md) for motivation and [specs/worker-process-exit-outcomes/spec.md](specs/worker-process-exit-outcomes/spec.md) for normative behavior. Blocks 20–22 own a one-shot host, lossless stdout emitter, and bounded stdin loop. Block 15 makes a flushed terminal event the richest accepted-run result, while block 21 permanently breaks the sink after any uncertain write and forbids retry. Block 18 already uses exit 2 for an invalid private worker invocation. Blocks 30 and 31 require a stable abnormal-process and busy classification respectively.

A process can fail before readiness, after readiness but before request acceptance, during execution, while flushing the terminal event, or while disposing host resources. A forced kill or unhandled runtime crash can bypass all managed cleanup and cannot be normalized portably by worker code.

## Goals / Non-Goals

**Goals:**
- Centralize one closed mapped taxonomy and deterministic precedence for all managed worker exits.
- Preserve terminal-event authority while making expected terminal/exit relationships testable.
- Set the actual process result only after required output completion and lifecycle cleanup.
- Keep mapped values stable on Windows and Linux and diagnostics safe for operators.

**Non-Goals:**
- Implement the PostgreSQL advisory lock (block 31), child launcher or fixture (blocks 25–26), controller/UI failure classification (block 30), or any retry policy.
- Convert operating-system signals, forced process-tree termination, fail-fast, stack overflow, or unhandled runtime crashes into a worker-selected code.
- Add protocol terminal types beyond block 15's completed, cancelled, and failed vocabulary.

## Decisions

### Use a small closed mapping that fits portable process statuses

| Code | Stable mapped outcome | Included cases |
|---:|---|---|
| 0 | Completed | Accepted run completed, including zero eligible work |
| 2 | Invalid invocation/request/input protocol | Invalid private role invocation; clean/empty/partial pre-request EOF; malformed, oversized, incompatible, or semantically invalid controller-input frame; invalid stdin sequence/correlation |
| 3 | Busy | Non-blocking global advisory-lock contention, once block 31 supplies it |
| 4 | Executor/domain failure | Accepted executor result is Failed, including a caught and deliberately classified execution-path OutOfMemoryException |
| 5 | Host infrastructure failure | Startup, configuration, required dependency initialization, unexpected stdin I/O, host lifecycle, generic terminal-coordination exception, or non-output cleanup/disposal failure |
| 6 | Output transport failure | Typed block-21 mapping/lifecycle-validation/serialization failure, stdout write/flush, broken pipe, or emitter disposal failure |
| 130 | Cancelled or host shutdown | Cooperative request cancellation, SIGINT/SIGTERM translated by Generic Host, or explicit host stop before or during a request |

Code 4 retains the existing meaning of processing failure. Configuration moves out of code 2: syntactically valid invocation plus unusable runtime configuration is infrastructure failure (5), while invalid invocation/request/controller-input protocol remains caller/input failure (2). All mapped values are nonnegative and at most 255, so an orderly managed return is observable unchanged by conventional Windows and Unix process APIs.

Alternative considered: reuse code 4 for every failure. Rejected because block 30 could not distinguish bad input, unavailable dependencies, domain failure, and a broken protocol pipe. Alternative considered: BSD sysexits values. Rejected because this private protocol already reserves 2, 3, 4, and 130 and needs only a compact internal taxonomy.

### Treat forced termination and unhandled crashes as unmappable

The mapper receives only outcomes reached through managed control flow. A force kill, process-tree termination, fail-fast, stack overflow, unhandled exception, or OOM that prevents the managed boundary from completing has no selected worker code. The worker makes no promise about the raw platform status of such a death. Block 30 must combine the observed status with readiness, protocol validity, terminal presence, retained stderr, and whether the launcher initiated forced termination. For a no-terminal orderly path, a mapped value is a classification candidate rather than cryptographic proof of managed completion; when those observations conflict or remain ambiguous, block 30 classifies an abnormal/missing-terminal process instead of claiming a domain result.

Alternative considered: normalize every observed process status to 4 or 130. Rejected because the worker never executes such a mapping after abrupt death, and Windows exception codes and Unix signal conventions are not equivalent.

### Commit one primary outcome and apply explicit late-failure precedence

The host records a typed primary outcome at the first conclusive lifecycle boundary. If later managed failures occur before process completion, precedence is:

1. Output transport failure (6), because readiness or terminal delivery is absent, partial, or uncertain.
2. Host infrastructure/startup/configuration/dependency/cleanup failure (5).
3. Invalid invocation/request/protocol (2).
4. Busy/advisory-lock contention (3).
5. Executor/domain failure (4).
6. Cooperative cancellation or host shutdown (130).
7. Completion (0).

The ranking resolves races and late cleanup faults; it does not ask later code to replace an already emitted terminal. Stderr diagnostic failure is best effort and never changes the selected outcome. Abrupt unmappable death bypasses the ranking entirely.

Alternative considered: last exception wins. Rejected because scheduling changes would make exit values nondeterministic. Alternative considered: always preserve the first primary outcome. Rejected because reporting completion after a terminal flush or disposal failure would falsely claim process integrity.

### Keep terminal events authoritative and define consistency rather than equality

For an accepted request that enters the executor/reporter session, the executor/reporter still produces exactly one terminal when stdout remains healthy. With no higher-precedence process condition, a flushed completed terminal has code 0, cancelled has code 130, and failed has code 4 or 5 according to the causal class. A post-acceptance invalid control/input-protocol outcome does not cancel, mutate, suppress, or replace the executor/reporter terminal; it selects code 2 and can therefore coexist with completed, cancelled, or failed domain state. If block 31 later supplies advisory-lock contention, it must use a typed first executor-entry gate after the host has invoked the executor exactly once and the reporter session has emitted run-started, but before eligibility, snapshots, database mutation, or heavy geodata work. The executor/reporter completes its normal failed terminal with safe busy detail, while the typed contention fact selects code 3. This preserves block 20's exact-once executor and terminal ownership and gives block 30 a valid terminal. Code 6 indicates that delivery became unreliable and therefore does not require a valid terminal. A successfully flushed terminal remains authoritative for run/UI state when concurrent input failure or later disposal/output failure raises the final process code to 2, 5, or 6; block 30 records that mismatch as a supplementary process anomaly and must not rewrite the terminal result.

Before request acceptance there is no run and therefore no terminal. Invalid invocation, pre-ready startup/configuration failure, readiness transport failure, pre-request EOF/input failure, and pre-request host shutdown are represented by exit plus safe stderr only. After executor/session entry, host dependency failure, executor failure, cancellation, and completion use the existing executor/reporter terminal path; post-acceptance input failure is retained independently and does not synthesize or alter that terminal. Busy is a first executor-entry gate and therefore uses the existing failed terminal without doing domain/heavy work. An output failure prevents fabrication or retry through the broken sink.

Alternative considered: derive UI state solely from exit. Rejected because the terminal includes run identity, timing, counts, and safe failure detail. Alternative considered: force every failed terminal to code 4. Rejected because exit is the intended coarse process-failure classifier for block 30.

### Finalize output and resources before assigning the process result

Block 20's lifecycle remains the sole owner of terminal coordination, request-lease settlement, linked-token and execution-scope disposal, application stop, and host/provider cleanup. Block 21 remains the emitter owner. Those owners contribute typed finality facts to one block-23 outcome accumulator; they do not create a second cleanup path. The block-20 finality adapter catches a recognized block-21 mapping/lifecycle-validation/serialization/write/flush/broken-state result and returns a discriminated OutputTransportFailure fact without throwing; that typed return selects 6. If the generic block-20 terminal/finality hook itself throws or lets any exception escape instead of returning the discriminated fact, block 20's established contract applies and selects host-infrastructure 5. The top-level role branch awaits the lifecycle and host disposal, applies late-failure precedence to the accumulated facts, writes and flushes one best-effort final classification summary through an injected stderr writer kept alive outside the disposed provider, and only then returns the integer from the entry point. Services do not call Environment.Exit, assign Environment.ExitCode, or terminate the process because those mechanisms can bypass finally, asynchronous disposal, and buffered output.

Host shutdown before acceptance selects 130 without a terminal. Shutdown after executor/session entry propagates cooperative cancellation and attempts cancelled before returning 130. Expected stdin read cancellation, ObjectDisposedException, or equivalent teardown caused by terminal finalization, host stop, or lease disposal is neutral and never selects 5 or replaces an earlier input outcome. Shutdown observed only after a terminal was flushed does not retroactively select 130 or change that run; normal cleanup still decides whether a separate failure changes the final code.

Alternative considered: set Environment.ExitCode as soon as a failure is seen. Rejected because a later stdout or disposal failure has higher precedence and because early assignment obscures cleanup ordering.

### Emit bounded safe stderr diagnostics and attach no retry meaning

Every orderly nonzero result attempts one bounded final classification summary containing a stable outcome token, lifecycle phase, and safe predefined message. This summary is distinct from ordinary predecessor-owned stderr logs or optional earlier safe transport/input diagnostics; those may already exist, but block 23 emits exactly one line marked as the final exit summary through its still-live top-level stderr writer. Diagnostics do not echo raw command arguments, request bytes, payloads, configuration values, credentials, exception messages/stacks, SQL, or protocol frames. Stdout remains protocol-only. Stderr write/flush is best effort and never retries or changes the code.

Exit codes classify one attempt only. Code 3 means busy, not “retry now”; code 6 means transport failed, not “run again”; no mapped or unmapped outcome authorizes automatic retry. Block 30 may present operator guidance, but retry policy requires a separate future change with idempotency analysis.

## Risks / Trade-offs

- [A valid terminal can coexist with a nonmatching late-failure exit] → Keep terminal run state authoritative and require block 30 to report the process-integrity anomaly separately.
- [Code 130 resembles Unix signal-derived statuses] → Treat it as a worker-selected code only when orderly managed completion is known; retain raw signal/status metadata for abrupt termination.
- [Caught OOM handling may itself fail] → Map only OOM that reaches the managed executor boundary and can safely emit a failed terminal; otherwise leave the crash unmapped.
- [Cleanup failures can mask a lower-priority domain outcome in the exit code] → Preserve the terminal event and stderr classification so neither fact is lost.
- [Safe diagnostics may omit useful exception detail] → Prefer stable non-sensitive classification here; bounded retained stderr detail can be expanded deliberately in block 30 without exposing raw secrets.

## Migration Plan

1. Re-read the applied blocks 15, 20, 21, and 22 APIs; stop for reconciliation rather than creating competing host, emitter, request-loop, or protocol ownership.
2. Add the dependency-light mapped outcome values, precedence combiner, and safe diagnostic metadata at the worker boundary.
3. Make the one-shot host return typed lifecycle outcomes and have the top-level worker role assign the final integer only after output finalization and disposal.
4. Add pure mapping/precedence tests and deterministic gated host tests for readiness, pre-request, accepted-run, flush, shutdown, and disposal paths on Windows and Linux CI.
5. Leave code 3 unreachable until block 31 injects advisory-lock contention; rollback removes the mapper/wiring without data or wire migration.
