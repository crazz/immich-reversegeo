## Context

See [proposal.md](proposal.md) for motivation and [specs/worker-ndjson-output/spec.md](specs/worker-ndjson-output/spec.md) for normative behavior. Block 8 defines a validating run-scoped event session and already requires serialized event acceptance, bounded awaited backpressure, activity cleanup, terminal finality, and non-recursive reporter failure. Block 15 defines canonical worker-to-controller values, codec, and stream validator: one process-scoped ready at sequence 1, one accepted run using the request `RunId`, exact-next stream sequence, strict UTF-8/no-BOM compact JSON, LF emission, a 1,048,576-byte JSON-object limit, and terminal-last lifecycle. Block 16 preserves that boundary with fixtures but deliberately excludes console and emission tests.

Blocks 19–20 provide role-specific composition and the Generic Host integration point. This change supplies the worker-side output component and reporter adapter without owning request reads, exit codes, launcher parsing, or later progress coalescing.

## Goals / Non-Goals

**Goals:**
- Give managed stdout one byte-oriented owner that emits block-15 frames and nothing else.
- Map the ready boundary and every accepted block-8 session event to one exactly correlated frame.
- Preserve deterministic order and complete lines under parallel asset/resolver reporting with bounded, lossless backpressure.
- Make write/flush completion, cancellation, terminal closure, and broken transport behavior explicit and fault-testable.
- Keep ordinary worker logs and safe transport diagnostics on stderr without leaking event payloads.

**Non-Goals:**
- Reading or validating controller stdin and cancel commands (22).
- Selecting process exit codes or deciding how missing terminals affect exit (23).
- Launching workers, parsing stdout, retaining stderr tails, or runtime crash classification (25/30).
- Dropping, sampling, batching, or coalescing progress (65).
- Redefining block-8 event/session semantics, block-15 JSON/size/lifecycle rules, or block-20 host lifecycle.

## Decisions

### 1. Separate process readiness from the run-scoped reporter

Use one emitter instance per worker process. Its explicit asynchronous initialization emits and flushes `ready` before the run-session adapter can accept an event. Ready uses sequence 1, null `runId`, the block-15 clock/timestamp policy, and an empty payload. The reporter adapter then consumes exactly one finalized block-8 session; `RunStarted` binds the sole non-empty processing `RunId`, and all subsequent events must match it.

The mapping is fixed:

| Source | Protocol category/type |
|---|---|
| emitter initialization | `lifecycle/ready` |
| `RunStarted` | `lifecycle/run-started` |
| `EligibilityDetermined` | `lifecycle/eligibility-determined` |
| `ProgressChanged` | `progress/progress-changed` |
| `ActivityStarted` | `activity/activity-started` |
| `ActivityEnded` | `activity/activity-ended` |
| `LogEmitted` | `diagnostic/log-emitted` |
| `RunFinished(Completed)` | `terminal/completed` |
| `RunFinished(Cancelled)` | `terminal/cancelled` |
| `RunFinished(Failed)` | `terminal/failed` |

Alternative considered: let the block-8 session emit ready. Rejected because ready precedes request acceptance and has no run identity. Alternative considered: have block 20 write ready directly. Rejected because it would create a second stdout owner and split sequence allocation.

### 2. Allocate one stream sequence in the single writer

A bounded FIFO channel accepts immutable source-event candidates. Exactly one consumer owns the next sequence value, block-15 mapping/validation/serialization, stdout write, and flush. It commits candidates in channel acceptance order: ready receives 1 and the first run event receives 2. There is no per-job allocator; v1 permits one ready and at most one accepted run per process. The production capacity is a named internal policy constant rather than configuration or protocol surface; tests inject a capacity of one to prove saturation semantics.

Sequence is assigned only by the consumer immediately before validation/serialization. If that step fails, the stream becomes permanently broken, so no later observable gap or retry can occur. Each queued candidate carries a completion source that resolves only after its frame flushes or fails with the shared transport fault.

Alternative considered: serialize under a `SemaphoreSlim` in every producer. Rejected because queued semaphore waiters are not an explicit bounded buffer and cancellation/terminal intake closure is harder to reason about. Alternative considered: assign sequence before enqueue. Rejected because cancellation while awaiting capacity could consume a number without a frame.

### 3. Backpressure is bounded, awaited, and lossless

The bounded channel uses wait-on-full behavior with one reader and multiple writers. No full-mode may drop, replace, or coalesce. Cancellation is honored only while a producer is waiting for channel acceptance. After acceptance, the candidate is committed independently of caller cancellation and the caller awaits its flush receipt; this preserves block-8 post-acceptance accounting semantics. Intake closure wakes blocked producers with a stable closed/broken result.

Alternative considered: an unbounded channel. Rejected because a slow/broken controller could turn event volume into worker memory growth. Alternative considered: drop-oldest or progress replacement. Rejected because block 8 requires lossless reporting and block 65 alone may define coalescing.

### 4. Build and write a whole LF-terminated UTF-8 frame

Reuse the block-15 codec rather than general-purpose polymorphic serialization. The writer creates one contiguous byte buffer containing the canonical JSON object and a literal `0x0A`, then performs one serialized `WriteAsync` followed by `FlushAsync`. It writes to an injected `Stream`; production supplies a dedicated handle from `Console.OpenStandardOutput()`. It never uses `Console.Out`, `Console.SetOut`, `TextWriter.WriteLine`, or platform newline APIs, so encoding, BOM, CRLF, and buffering cannot drift. Report completion means write plus flush succeeded.

Single ownership prevents application-level interleaving. No abstraction can guarantee rollback after an operating-system mid-write/broken-pipe failure, so a single trailing truncated physical line is an acknowledged fault boundary; after any uncertain write the emitter fails permanently and never retries or writes another byte.

Alternative considered: `Console.Out.WriteLineAsync`. Rejected because global mutable ownership, text encoding/BOM choices, newline policy, and unrelated console writers can violate the byte contract. Alternative considered: flush only at terminal. Rejected because readiness and progress must be observable promptly and an unflushed successful report would weaken acceptance semantics.

### 5. Validate outgoing lifecycle before touching stdout

The emitter drives the block-15 outgoing stream validator in the same single-writer order and commits validator state transactionally only for a fully mapped candidate. It rejects run events before ready, correlation changes, illegal activity ordering, invalid completed-before-eligibility, duplicate lifecycle events, and post-terminal output before writing bytes. Terminal acceptance closes channel intake so a concurrently late event cannot queue behind it. Previously accepted FIFO items drain before terminal; block-8 session cleanup supplies activity-ended events before `RunFinished`. A successfully flushed terminal ends the writer and is the last frame.

Emitter disposal, active cancellation, mapper/codec fault, or broken stdout does not synthesize activity ends or terminal output. Doing so could recurse through a broken sink, invent domain facts, or hide a missing-terminal failure that blocks 23/25/30 must classify.

Alternative considered: trust producer order without validating output. Rejected because stdout is the compatibility boundary and invalid bytes must be stopped before controller observation.

### 6. Fail the emitter once and fan out a safe transport failure

Mapping, payload validation, serialization, size, write, flush, and broken-pipe failures converge on one atomic broken state. The first failure stops intake, cancels/drains the writer as appropriate, and completes all queued/future receipts with a stable transport exception/failure category. The public failure contains only stage/category and safe fixed context; it excludes raw event data, codec/parser exception prose, stack traces, and secrets. No retry occurs because a write/flush failure may have partially reached the pipe and retry could duplicate a frame.

A best-effort `ILogger` diagnostic may identify the safe stage on stderr. Failure in that logger is swallowed after preserving the original transport failure; it never produces a protocol `log-emitted` event and cannot recurse into the emitter. Exit-code selection remains block 23.

Alternative considered: convert transport faults to `terminal/failed`. Rejected because the same sink is unavailable or uncertain, and a fabricated domain terminal would misrepresent execution.

### 7. Enforce stdout ownership and stderr logging at worker composition

Expose registration that takes separate stdout/stderr streams for tests and binds worker `ILogger` providers to stderr. In production only the emitter receives the standard-output stream. Do not redirect `Console.Out` globally; source-boundary tests forbid direct `Console.Out`/`Console.Write*` calls in worker execution/composition paths. This establishes exclusive managed ownership without claiming control over hostile native libraries that bypass .NET and write directly to file descriptor 1.

Reporter events and ordinary logs remain separate: a `LogEmitted` event is intentional protocol data, while `ILogger` output is operational text on stderr. The emitter serializes only block-15 typed fields and never enriches log/failure payloads with logger scopes, structured state, exceptions, environment/configuration, credentials, connection strings, SQL, or raw input. Safe-message production remains the block-8/15 contract; this boundary validates and rejects unsafe/oversized typed values but does not claim heuristic secret detection in otherwise valid free text.

Alternative considered: globally set `Console.SetOut(TextWriter.Null)`. Rejected because it hides ownership violations, can affect libraries unpredictably, and still does not provide the protocol writer's byte guarantees.

### 8. Test through injected memory, blocking, and fault streams

Keep transport tests in-process and deterministic. Inject a clock, channel capacity, codec seam only where block-15 public behavior permits, and custom streams that capture bytes, block writes/flushes with task signals, count calls, throw before/during write, throw on flush, and emulate disposal/broken pipe. Parse successful captured lines through the production block-15 codec and validator rather than asserting only strings.

Concurrency tests start many producers behind a barrier, then assert exact-next sequence, stable correlation, one frame per accepted event, no interleaving, and valid activity/terminal order. Capacity-one tests prove asynchronous backpressure and cancellation before versus after acceptance. Fault matrices prove single transition, no retry/further bytes, queued/future fan-out, safe stderr diagnostics, and absence of payload sentinels. Encoding tests prove UTF-8/LF/no-BOM and multibyte/escaped content. Source/composition tests prove stdout exclusivity and stderr `ILogger` routing.

## Risks / Trade-offs

- [Per-frame flushing lowers throughput] → Preserve correctness and prompt observability now; measure before block 65 introduces any explicitly planned reduction in event volume.
- [Bounded lossless output can slow asset tasks when the controller drains slowly] → Use awaited backpressure so memory remains bounded and no event semantics are silently lost.
- [A broken pipe can leave a partial final physical line] → Treat the stream as permanently broken, never retry, and defer incomplete-stream classification to launcher/exit owners.
- [Managed ownership cannot prevent native code from writing directly to fd 1] → Keep worker dependencies controlled and use end-to-end process tests in blocks 25/26; do not overstate this block's source-level guarantee.
- [Free-text diagnostics can contain accidental sensitive values] → Admit only pre-sanitized block-8/15 typed messages, add no metadata, reject invalid values, and never echo payloads on transport failures; heuristic redaction is explicitly not promised.

## Migration Plan

1. Verify finalized block-8 and block-15/16 public APIs and stop for reconciliation if their mapping, codec, validator, or failure semantics differ from this plan.
2. Add the injectable emitter and deterministic transport tests without changing controller input, exits, or launcher code.
3. Register the emitter through the worker-only composition seam from blocks 19–20, route worker `ILogger` to stderr, and remove/forbid managed non-protocol stdout use in that role.
4. Have the existing worker-host lifecycle call emitter readiness and pass the reporter adapter to the processing executor; do not modify block-20 ownership beyond consuming its integration seam.
5. Roll back by removing the worker-only registration/integration; no persisted data or wire migration is involved because this is the first output transport.
