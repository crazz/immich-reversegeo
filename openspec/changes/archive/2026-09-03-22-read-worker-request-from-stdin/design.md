## Context

See [proposal.md](proposal.md) for motivation and [specs/worker-stdin-request-loop/spec.md](specs/worker-stdin-request-loop/spec.md) for normative behavior. Finalized block 15 fixes strict UTF-8 NDJSON and a 1,048,576-byte JSON-object limit; block 17 supplies the pure execute/cancel codec and transactional input validator; block 20 consumes a transport-neutral initial-request lease and owns host/executor/disposal ordering; block 21 exclusively writes and flushes stdout, including ready and the accepted run's terminal event.

This change supplies the concrete stdin side of the block-20 seam. It must read cancellation controls while execution is active, but it must neither turn invalid input or EOF into cancellation nor keep the one-shot process alive indefinitely after terminal. It also must expose enough typed finality for block 23 to choose an exit without choosing that exit here.

## Goals / Non-Goals

**Goals:**
- Own standard input once, begin after flushed readiness, and frame strict UTF-8 safely across arbitrary stream chunks.
- Enforce the protocol byte limit before unbounded allocation or JSON parsing.
- Publish exactly one execute lease and run one independently sequenced transactional control pump beside execution.
- Make cancel-before/during/after races, EOF, invalid input, reader faults, and structured pump disposal deterministic.
- Hand safe typed input outcomes to host finality without adding stdout messages or exit codes.
- Prove behavior with deterministic chunked, gated, and faulting streams.

**Non-Goals:**
- Redefining block-15/17 schema, canonical serialization, validation categories, or sequence rules.
- Emitting ready, run events, acknowledgements, diagnostics, or terminals on stdout; block 21 remains the sole output owner.
- Selecting process exit codes or precedence between execution and input outcomes; block 23 owns that mapping.
- Controller-side pipes, process launch, stderr-tail retention, crash classification, worker reuse, or multiple jobs.
- Changing executor-owned configuration, eligibility, work-set snapshots, processing semantics, or Web scheduling.

## Decisions

### 1. Use one dedicated byte stream and start it through the readiness hook

Production opens a dedicated `Console.OpenStandardInput()` stream once in the InternalWorker composition path and gives it only to the request-source component. The component never touches `Console.In`, does not replace global console readers, and performs no read until block 20 awaits block 21's successful ready write/flush and then asks for the initial lease.

Tests inject a stream directly. Ownership transfers to the component so terminal/host shutdown can dispose that dedicated handle to unblock a pending read; no peer is allowed to read or dispose it. This mirrors block 21's dedicated stdout-handle ownership without combining the two transports.

Alternative: use `Console.In.ReadLineAsync`. Rejected because it decodes to UTF-16 and can allocate an arbitrarily long line before the 1 MiB UTF-8 limit is known. Alternative: begin a reader hosted service during host startup. Rejected because buffered input could be consumed before ready has flushed.

### 2. Scan bytes incrementally with fixed chunks and bounded retained state

Use an injectable asynchronous frame reader over `Stream`. It reads a fixed-size reusable/pool-backed chunk, scans for literal LF, and carries strict UTF-8 validation state across reads so a multibyte scalar can straddle any boundary. It retains the frame's original bytes for the block-17 byte codec, not a growing decoded string. A CR is delimiter data only when it is the one byte immediately before LF; CR elsewhere remains content and the existing framing rules reject it.

The accumulator is capped at `MaxMessageBytes` plus one byte that can only be a pending delimiter CR. If another object byte arrives, the reader returns `message-too-large` immediately, before JSON parsing and without draining or retaining the rest of the line because input failure is fatal to this one-shot pump. LF finalizes the decoder; incomplete multibyte state is invalid encoding. The delimiter is removed and the complete bounded byte frame is passed unchanged to block 17. Empty LF/CRLF, BOM, bare CR, and codec-invalid framing remain failures.

A chunk may contain execute plus later frames. The reader preserves only the bounded unread suffix needed for subsequent scans; it does not discard a cancel already delivered in the same read. Therefore execute can publish the lease and an immediately following cancel can latch before executor entry.

Alternative: use `PipeReader`. Deferred unless already present in the applied dependency surface; the required behavior needs only BCL streams and bounded pooled storage. Alternative: count decoded characters. Rejected because UTF-8 byte length is normative and multibyte data would make the bound incorrect.

### 3. Run one state machine that settles initial acquisition once

The input pump owns the block-17 controller-input validator and has closed phases: `WaitingForExecute`, `Accepted`, `Terminal`, and `Stopped`. Its initial completion source settles exactly once with block 20's accepted lease, clean pre-request EOF, or safe pre-request failure.

In `WaitingForExecute`, only a valid sequence-1 execute can transition to `Accepted`. The pump constructs one lease with the exact immutable request, one internally owned cancellation source/token, and pump-finality access, then publishes it. It never returns to acquisition. Any second execute is submitted to the finalized validator and fails lifecycle validation; it cannot replace the lease or reach the executor.

Input sequence is held only by the block-17 validator and is unrelated to stdout sequence. The pump does not increment or repair it. A rejected candidate leaves validator state unchanged, but because runtime input failure is fatal for this process, the pump records that failure and stops rather than attempting resynchronization.

Alternative: read only execute and let the host start a separate cancel reader. Rejected because two readers could lose buffered suffix bytes and split sequence/lifecycle ownership. Alternative: let block 20 await execute directly from stdin. Rejected because block 20 intentionally owns no transport mechanics.

### 4. Continue the same pump beside execution and serialize cancel/terminal races

After publishing the lease, the pump immediately continues reading controls; it does not wait for executor entry. The lease cancellation source is linked by block 20 with host stopping. A valid correlated cancel calls an idempotent cancellation latch. This permits a buffered cancel to win before executor invocation, an active-run cancel to request the same token, and repeated exact-next cancels to consume sequence while having one effect.

Terminal notification from block 20/21 finality and valid-cancel application share a small atomic state gate. If cancel commits first, the latch is requested even if execution is about to finish. If terminal commits first, that cancel is accepted for sequence/correlation but has no cancellation effect. The pump does not wait after terminal for hypothetical future input: terminal signals pump stop immediately. Only a complete frame already in the read/validation race can observe the post-terminal no-op ordering; unread later bytes are irrelevant because the one-shot process is closing.

Invalid cancel, cancel-first, wrong correlation, replay/gap, duplicate execute, and unknown input never touch the latch. A post-acceptance input failure likewise records host finality but does not cancel execution; otherwise malformed bytes would become an undocumented cancellation command contrary to block 17.

Alternative: cancel execution on any stdin failure. Rejected because block 17 explicitly requires invalid controls and EOF to have no cancellation effect. Alternative: ignore duplicate execute and continue. Rejected because fail-closed lifecycle validation must expose controller bugs.

### 5. Distinguish clean EOF, partial EOF, validation failure, and reader failure

The frame reader reports EOF with no buffered bytes separately from EOF with a partial frame. Before execute, clean EOF settles the initial boundary with no run; partial EOF is invalid framing. After execute, clean EOF records `ControlsClosed` and ends only the pump, while execution and terminal output continue. Partial EOF records a safe accepted-run input failure without cancellation.

Codec/validator rejection records its existing stable safe category. A stream exception is normalized to a bounded `reader-failure` category; raw exception text is retained only as an internal exception relationship if repository policy requires it and is never sent across protocol/diagnostic boundaries. Expected cancellation caused by terminal, host stop, or lease disposal is neutral pump shutdown and does not overwrite a previously committed EOF/failure.

The request lease exposes a single pump-finality result so block 20 can await disposal and hand the combined accepted-run facts to block 23's later outcome mapper. Pre-request failure uses block 20's existing pre-request hook. This change defines the typed distinctions and precedence of primary input outcomes, but not numeric process exits or execution-vs-input exit precedence.

Alternative: collapse every end into EOF. Rejected because partial framing and read faults are protocol/infrastructure failures while clean half-close is explicitly non-failure and non-cancellation.

### 6. Keep stdout silent and hand diagnostics to host/stderr

The pump never calls the block-21 emitter for acknowledgement, protocol error, log, or terminal output. Ready is emitted before the pump starts; run-started is executor-entry evidence; the executor/reporter remains the single accepted-run terminal producer. A failure before request acceptance therefore has no run terminal, while a post-acceptance input failure does not synthesize or duplicate one.

Every EOF/failure handoff is a typed host-coordination value containing a stable category and bounded safe message assembled from known constants. A best-effort `ILogger` entry may write that safe category to stderr. Diagnostics exclude raw frame bytes/text, arbitrary JSON values, parser/stream exception prose, stacks, credentials, and secret sentinels. Logger failure cannot change the primary outcome or recurse into stdout.

Alternative: emit a failed terminal for malformed input. Rejected before acceptance because no run exists, and rejected after acceptance because input transport is not the owner of domain terminal facts. Alternative: add execute/cancel acknowledgements. Rejected by block 17's closed v1 vocabulary.

### 7. Dispose the pump as part of the accepted lease

The lease owns the pump stop source, cancellation latch source, frame reader/buffer, and dedicated input stream. Block 20 marks terminal/finality, signals pump stop, and awaits the lease's pump completion before disposing lease/scope/host resources. Stopping closes/disposes the dedicated stream as needed to unblock implementations whose pending `ReadAsync` does not promptly honor cancellation. Cleanup observes exactly one primary pump outcome and exactly one reader task; it never starts a replacement task or initial acquisition.

If EOF/failure already completed the pump, finalization simply observes it. If host shutdown begins first, linked execution cancellation remains block 20's responsibility while pump shutdown is disposal, not a synthetic input failure. Cleanup errors are normalized and handed to host infrastructure coordination without changing stdout or retrying input.

Alternative: fire-and-forget the control task until process exit. Rejected because in-process host tests, pooled buffers, stream handles, and cancellation sources require deterministic release, and background exceptions must be observed.

### 8. Test physical streaming and races without sleeps

Use custom deterministic streams that return caller-selected chunk sizes, gate a pending read, throw on a selected read, count reads/disposal, and expose supplied byte sequences. Table-driven framing tests split valid UTF-8 frames at every byte boundary, including each multibyte scalar and between CR/LF; cover multiple frames in one chunk, one-byte chunks, LF and CRLF, empty lines, BOM/invalid/truncated UTF-8, bare CR, exact limit, max-plus-one, and EOF with zero versus partial buffered bytes.

Host/lease tests gate ready flush, execute publication, executor entry, cancel validation, terminal marking, and disposal with task completions/barriers rather than timing. They cover cancel before entry, during execution, repeated cancels, and both deterministic terminal-race orders; duplicate execute and every invalid/unknown/incompatible class; pre/post-request reader faults; clean and partial EOF before/after acceptance; pending-read shutdown; no second lease/executor; exactly one terminal owner; no stdout acknowledgements; safe stderr/host diagnostics; and no unobserved pump task.

Successful frame tests use the production block-17 codec/validator rather than duplicating JSON assumptions. Source/composition tests prove sole stdin ownership and ready-before-first-read. Allocation-focused tests use a counting/guard stream and bounded-buffer observability to prove over-limit input is rejected without reading/buffering an unbounded line.

## Risks / Trade-offs

- [Disposing standard input to unblock a read is process-global at the OS handle] → Make the worker input component its sole owner and dispose only during terminal/host shutdown; tests enforce no competing reader.
- [A post-acceptance malformed frame can coexist with a completed domain terminal] → Preserve both typed facts for block 23 rather than cancelling work or fabricating protocol output.
- [Terminal may race a fully read cancel] → Serialize their effect decision through one atomic gate and test both orderings.
- [Pooled or segmented buffering can leak stale data] → Track exact written lengths, clear sensitive ranges where repository policy requires, and never include retained bytes in diagnostics.
- [A stream implementation may ignore read cancellation] → Own and dispose the dedicated handle during structured shutdown, then await the task.
- [A huge no-delimiter line remains unread after overflow] → Treat overflow as fatal and stop/dispose the one-shot input stream; there is no safe reason to drain attacker-controlled bytes.

## Migration Plan

1. Re-read applied blocks 15, 17, 20, and 21 and bind to their exact codec, validator, readiness, request-lease, reporter, and finality APIs; stop for reconciliation rather than duplicate them.
2. Add the bounded incremental frame reader and deterministic byte/chunk/EOF/fault tests without wiring process exits.
3. Implement the one-pump state machine, exact-once lease publication, cancellation latch, terminal race gate, and typed finality handoff.
4. Register the sole dedicated stdin owner in the InternalWorker composition path and connect it to block 20 after block 21 readiness.
5. Add in-process host tests for control concurrency, terminal ownership, diagnostics, and structured disposal; run focused/default tests plus strict OpenSpec validation.

Rollback removes the block-22 stdin transport registration and request-source implementation, returning block 20 to its transport-neutral seam. It does not change persisted data, public configuration, the protocol contracts, stdout emitter, or exit mapping.
