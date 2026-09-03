## 1. Reconcile prerequisites and boundaries

- [x] 1.1 Re-read the applied block-8 reporting session and block-15 protocol/codec/stream-validator APIs plus block-16 compatibility evidence; stop for planning reconciliation rather than recreating or weakening missing contracts.
- [x] 1.2 Inventory the block-19/20 worker-only composition seam and standard stream/logger ownership without editing request input, exit mapping, launcher parsing, or progress coalescing concerns.
- [x] 1.3 Record the named production queue-capacity constant and expose only test injection for capacity, clock, stdout/stderr streams, and deterministic fault control.

## 2. Build the single-owner emitter

- [x] 2.1 Add one worker-process emitter with explicit asynchronous initialization that maps, validates, writes, and flushes the sole ready frame at stream sequence 1 before accepting run events.
- [x] 2.2 Add the complete block-8-to-block-15 mapping for run-started, eligibility, progress, activity start/end, typed logs, and completed/cancelled/failed terminal frames using the exact request `RunId`.
- [x] 2.3 Add a bounded wait-on-full multi-producer/single-consumer FIFO with no drop, replacement, batching, sampling, or coalescing and with sequence allocation only inside the single writer.
- [x] 2.4 Return a per-candidate receipt that completes only after write plus flush; cancel a candidate only while it awaits queue acceptance and preserve it after acceptance despite later caller cancellation.
- [x] 2.5 Drive the block-15 stream validator transactionally before stdout writes, close intake when terminal is accepted, drain prior accepted activity ends/events first, and reject late/post-terminal candidates without sequence allocation.

## 3. Enforce byte framing and stream ownership

- [x] 3.1 Reuse the block-15 canonical codec and message-size policy, append one literal LF to one contiguous strict UTF-8/no-BOM buffer, and issue one serialized stream write followed by one flush per frame.
- [x] 3.2 Use an injected byte `Stream` and production `Console.OpenStandardOutput()`; do not use or globally replace `Console.Out`, `TextWriter.WriteLine`, `Console.Write`, or `Console.WriteLine`.
- [x] 3.3 Bind ordinary worker `ILogger` providers and best-effort safe transport diagnostics to stderr only, while keeping intentional block-8 `LogEmitted` events on the protocol path.
- [x] 3.4 Add source/composition guards proving only the emitter receives managed stdout and no startup banner, framework log, exception, or ordinary text reaches it.

## 4. Define terminal and fault behavior

- [x] 4.1 Add one atomic broken-emitter transition shared by mapping, validation, serialization, size, write, flush, disposal, and broken-pipe failures; stop intake/output and fan one stable safe transport failure to queued and future callers.
- [x] 4.2 Prohibit retries and synthetic protocol diagnostics/activity ends/terminals after an uncertain transport fault; preserve the original failure if best-effort stderr logging also fails.
- [x] 4.3 Ensure a successfully flushed terminal is the last frame and successful emitter completion requires it, while early disposal/failure writes no fabricated terminal and leaves exit/runtime classification to blocks 23/25/30.
- [x] 4.4 Serialize only block-15 typed fields, reject invalid/blank/oversized/unsafe values before stdout, and ensure transport exceptions and stderr diagnostics never echo raw payloads, exceptions, stacks, credentials, connection strings, SQL, or secret-like test sentinels.

## 5. Add deterministic transport tests

- [x] 5.1 Add in-memory success tests for ready sequence 1/null correlation and every processing event/terminal mapping, parsing all captured output through the production block-15 codec and stream validator.
- [x] 5.2 Add UTF-8/LF/no-BOM tests for compact bytes, multibyte values, escaped CR/LF data, exact named size limits, one physical line per frame, one write plus one flush, and no `TextWriter` newline transformation.
- [x] 5.3 Add barrier-driven concurrent asset/activity/log tests proving stable run correlation, exact-next stream sequences beginning at 2 after ready, one frame per accepted event, no byte interleaving, activity ends before terminal, and terminal last.
- [x] 5.4 Add capacity-one blocking-stream tests proving bounded memory behavior, asynchronous wait-on-full backpressure, no drop/coalescing, cancellation before acceptance without sequence consumption, and post-acceptance cancellation without event retraction.
- [x] 5.5 Drive mapper, validator, canonical-codec oversize, write, partial-write, flush, disposal, and broken-pipe faults through production behavior and injected fault streams, proving one broken transition, no retry or later stdout bytes, consistent queued/future failures, and no synthetic terminal; do not add callback or protocol-substitution seams.
- [x] 5.6 Add stderr logger-routing and logger-failure tests plus payload-sentinel tests proving stdout purity, safe bounded diagnostics, no recursion, and no raw sensitive/event/error detail leakage.
- [x] 5.7 Add source-boundary tests showing change 21 does not introduce stdin handling, exit-code policy, launcher parsing, process-fault classification, or progress coalescing.

## 6. Verify the planning contract during apply

- [x] 6.1 Run focused reporter/emitter in-memory, concurrency, saturation, cancellation, encoding, logger-routing, and fault tests repeatedly without sleeps or real child processes.
- [x] 6.2 Run the retained block-15/16 protocol suites and the repository's normal default-exclusion test command.
- [x] 6.3 Run `openspec validate 21-emit-worker-events-as-ndjson --strict` and inspect change status before handing off to blocks 22/23/25.
