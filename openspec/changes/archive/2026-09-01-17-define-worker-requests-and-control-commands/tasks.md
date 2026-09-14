## 1. Verify prerequisites and boundaries

- [x] 1.1 Re-read the applied block-7 immutable request/result contracts and finalized block-15 protocol constants, codec, framing, validation failures, and ready semantics; stop rather than duplicate or redefine them if prerequisites differ.
- [x] 1.2 Record source-boundary guards proving block 17 adds no stdin reader/loop, worker host, stdout emitter, launcher, exit-code mapping, executor/config snapshot change, second execute, second identity, or generalized worker-job abstraction.

## 2. Define controller input contracts and request mapping

- [x] 2.1 Add immutable v1 `controller-to-worker` envelopes and closed `request/execute` and `control/cancel` payload types under the existing Core worker-protocol boundary, reusing canonical field names and constants.
- [x] 2.2 Map execute envelope `runId` plus payload `trigger` exactly to one validated block-7 `ProcessingRunRequest` for Manual, Scheduled, and RunOnce; add no job ID, mode, settings, work-set, credential, or mutable-state fields.
- [x] 2.3 Define cancel with the same exact run ID and canonical empty payload, preserving request immutability and allowing no reason, token, deadline, or command identity.

## 3. Extend canonical codec and safe validation

- [x] 3.1 Extend canonical serialization/parsing for controller input with fixed envelope/payload order, case-sensitive tokens, canonical GUID/Int64/UTC forms, and only defined v1 fields.
- [x] 3.2 Reuse the exact 1,048,576-byte strict UTF-8/no-BOM single-line frame policy, LF emission/one LF-or-CRLF parse behavior, duplicate detection, and pre-JSON size/encoding/framing checks from block 15.
- [x] 3.3 Preserve same-version unknown-property tolerance at envelope/payload levels while failing closed on missing or invalid known fields and unsupported protocol/version/direction/category/type; return safe bounded structured failures without partial values, raw input, exceptions, stacks, or secrets.

## 4. Validate readiness, sequencing, correlation, and cancellation

- [x] 4.1 Add a pure transactional controller-input validator whose externally supplied readiness state permits exactly one execute at input sequence 1 only after ready, rejects cancel-first and every second execute, and keeps stdin sequence independent from stdout sequence.
- [x] 4.2 Validate exact +1 input sequencing without state advance on rejected input and exact correlation of every cancel to the immutable execute run ID.
- [x] 4.3 Model cancel before executor invocation as latched, during execution as cooperative cancellation, and after cancellation/terminal as an idempotent no-op; accept repeated correctly sequenced correlated cancels while rejecting replayed sequence values and wrong-run commands.
- [x] 4.4 Represent EOF states needed by block 22: no request on pre-frame EOF, invalid framing on partial-frame EOF, and clean post-execute half-close as no-more-controls rather than cancellation.
- [x] 4.5 Keep acknowledgement policy explicit: add no execute/cancel ack event, treat run-started as execution evidence, and leave host response/error logging and exit outcomes to blocks 22 and 23.

## 5. Add deterministic contract tests

- [x] 5.1 Add byte-stable golden and round-trip tests for execute across all three triggers and for empty-payload cancel, asserting exact request reconstruction, immutable field preservation, and no second identity or snapshot fields.
- [x] 5.2 Add frame/compatibility tests for exact-limit and one-byte-oversize input, multibyte UTF-8, BOM/invalid UTF-8, LF/CRLF/bare-CR/empty/partial/multiple frames, malformed JSON, duplicates, additive unknown properties, canonical primitives, and unsupported direction/type/version.
- [x] 5.3 Add lifecycle tests for ready-before-consume, execute sequence 1, cancel-first, second execute in every phase, independent direction sequences, gaps/regressions/replay/overflow, rejection without state advance, and wrong/empty run correlation.
- [x] 5.4 Add cancellation/EOF tests for cancel before/during/after execution, repeated correctly sequenced cancel idempotency, clean half-close after execute/cancel/terminal, partial-frame EOF, no acknowledgement types, and safe bounded failures.
- [x] 5.5 Prove tests use no console stream, async stdin loop, ASP.NET host, child process, launcher, executor invocation, `ProcessingState`, exit-code path, or generalized job abstraction; run focused MSTests, `npm run test`, and `openspec validate 17-define-worker-requests-and-control-commands --strict`.
