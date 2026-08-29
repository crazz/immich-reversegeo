## 1. Prerequisite and ownership reconciliation

- [ ] 1.1 Re-read the applied block-15 frame limit/codec, block-17 execute/cancel validator, block-20 request-lease/finality seams, and block-21 readiness/emitter APIs; stop rather than introduce duplicate contracts if they differ from this plan.
- [ ] 1.2 Define the worker-only standard-input registration so one component owns a dedicated byte stream, no path uses `Console.In` or unbounded text-line reads, and the first read cannot begin until ready write/flush completes.

## 2. Bounded incremental frame reader

- [ ] 2.1 Implement fixed-chunk byte scanning with strict incremental UTF-8 state across reads, LF framing, optional single CR before LF, and bounded preservation of unread suffix bytes when one read contains multiple frames.
- [ ] 2.2 Enforce the shared 1,048,576-byte JSON-object limit before JSON parsing while retaining at most the limit, one possible delimiter CR, and bounded reader state; return safe size/encoding/framing failures without draining an oversized line.
- [ ] 2.3 Distinguish clean frame-boundary EOF from partial-frame EOF and normalize stream faults/cancellation without exposing raw input or exception details.

## 3. Exact-once execute acquisition

- [ ] 3.1 Drive the finalized block-17 codec and transactional input validator from one pump whose initial result settles exactly once as accepted execute lease, clean pre-request EOF, or safe pre-request failure.
- [ ] 3.2 Publish one lease containing the exact immutable request and cooperative cancellation signal for valid sequence-1 execute, preserve stdin sequence independence from stdout, and make any second execute incapable of publishing another lease or invoking another executor.
- [ ] 3.3 Hand pre-request EOF/failure through block-20 coordination with no request, executor invocation, stdout acknowledgement, or fabricated terminal.

## 4. Concurrent controls and race semantics

- [ ] 4.1 Continue the same reader pump immediately after execute acceptance so a same-read or queued correlated cancel can latch before executor entry and a during-run cancel requests the lease's existing cooperative token.
- [ ] 4.2 Serialize cancel application against terminal notification so both before-terminal cancellation and after-terminal no-op orderings are deterministic, while repeated exact-next correlated cancels advance sequence with one idempotent effect.
- [ ] 4.3 Fail closed on cancel-first, wrong correlation, replay/gap, malformed/unknown/incompatible input, and duplicate execute without mutating validator state, cancelling the accepted run, creating another run, or emitting stdout output.
- [ ] 4.4 Record clean post-execute EOF as controls closed and partial EOF/validation/read faults as accepted-run input finality while allowing the executor/reporter's sole terminal attempt to continue.

## 5. Pump finality, diagnostics, and disposal

- [ ] 5.1 Expose one typed pump-finality result on the request lease so block 20 can await and hand clean EOF, safe input failure, or reader failure to later exit mapping without selecting a numeric exit in block 22.
- [ ] 5.2 Route only stable bounded categories through host coordination and best-effort stderr logging; prove raw frames, payloads, parser/stream messages, stacks, credentials, and secret sentinels never appear in diagnostics.
- [ ] 5.3 On terminal, host stop, or lease disposal, stop intake, unblock a pending read through owned cancellation/stream disposal, await the background pump exactly once, preserve the first primary outcome, and release buffers, decoder state, cancellation sources, and the dedicated stream.
- [ ] 5.4 Verify the stdin component emits no ready/run/acknowledgement/diagnostic/terminal frame and block 21 remains the only stdout owner and accepted-run terminal path.

## 6. Deterministic transport and host tests

- [ ] 6.1 Add table-driven chunked-stream tests that split valid frames at every byte boundary, including multibyte UTF-8 and CR/LF, and cover one-byte/multi-frame chunks, LF, CRLF, empty/bare-CR input, BOM, invalid/truncated UTF-8, exact-limit, max-plus-one, and bounded overflow behavior.
- [ ] 6.2 Add EOF and fault matrices for zero versus partial buffered bytes before and after execute, read failures at selected calls, expected shutdown cancellation, preserved primary outcome, and no unobserved background task.
- [ ] 6.3 Add barrier-driven lease/host tests for ready-before-first-read, exact identity and one executor, cancel before entry/during execution/repeated/after terminal, both cancel-terminal race orders, and clean EOF continuing execution.
- [ ] 6.4 Add rejection matrices for malformed JSON, unknown protocol/version/direction/category/type, invalid payload, sequence gap/replay, wrong cancel correlation, duplicate execute, and reader faults, asserting no cancellation side effect, second lease, second executor, stdout acknowledgement, or duplicate terminal.
- [ ] 6.5 Add pending-read disposal and source/composition tests that prove sole stdin ownership, deterministic stream/buffer/token cleanup, safe stderr/host handoff, and no process-exit mapping added by this change.

## 7. Verification

- [ ] 7.1 Run the focused block-22 MSTests repeatedly and the normal default-exclusion test suite.
- [ ] 7.2 Run `openspec validate 22-read-worker-request-from-stdin --strict` and confirm clean `openspec status --change 22-read-worker-request-from-stdin` output.
- [ ] 7.3 Review the final diff to confirm only MASTERPLAN block 22 and change 22 planning/implementation scope changed, with block 23 exit mapping untouched.
