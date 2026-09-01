## Context

See [proposal.md](proposal.md) for motivation and [specs/worker-control-requests/spec.md](specs/worker-control-requests/spec.md) for normative behavior. Block 7 defines the immutable transport-neutral `ProcessingRunRequest(Guid RunId, ProcessingRunTrigger Trigger)` and fixes trigger vocabulary to Manual, Scheduled, and RunOnce. Block 11 keeps eligibility, skipped-ID, and non-empty processing-configuration snapshots inside execution rather than admission or the request. Finalized block 15 defines protocol identity, canonical primitive forms, a 1,048,576-byte UTF-8 NDJSON frame, additive-field compatibility, safe validation failures, and worker stdout readiness/events, but deliberately excludes controller input.

Block 17 defines immutable controller input values plus pure single-message and input-sequence validation. It performs no stdin reads or writes. Block 22 owns the bounded reader and command loop, block 23 maps outcomes to process exits, block 25 owns controller serialization and process pipes, block 28 owns production cancel delivery and graceful cancellation behavior, and block 47 is the first change allowed to generalize processing execution into multiple worker job kinds.

## Goals / Non-Goals

**Goals:**
- Give both sides one exact v1 execute and cancel schema with block-7 identity preserved end to end.
- Make readiness, one-request cardinality, input sequencing, cancellation idempotency, EOF, compatibility, and validation boundaries deterministic before transport is wired.
- Reuse block 15's wire rules instead of creating a second stdin-only protocol.

**Non-Goals:**
- Reading stdin, running an asynchronous command loop, launching a worker, emitting/flushing stdout, draining stderr, or choosing exit codes.
- Adding request/cancel acknowledgement event types, ping, shutdown, multiple execute requests, reusable workers, arbitrary job payloads, or job identifiers distinct from `runId`.
- Moving executor-owned eligibility, skipped-ID, configuration, batch, credential, or work-set state into the request.

## Decisions

### Extend the v1 envelope symmetrically for controller input

Controller input uses the block-15 envelope field order and primitive rules: `protocol`, `version`, `direction`, `category`, `type`, `sequence`, `timestampUtc`, `runId`, and `payload`. Identity is `immich-reversegeo.worker` version 1; direction is `controller-to-worker`. The only v1 input pairs are `request/execute` and `control/cancel`.

Alternative: define a smaller unrelated command shape. Rejected because separate framing, identity, canonicalization, and compatibility rules would drift immediately. Alternative: predeclare ping/shutdown discriminator values. Rejected because block 15 closes semantic discriminators; a future feature must add supported semantics deliberately rather than treating currently unimplemented commands as valid.

### Map execute to one exact block-7 request snapshot

An execute envelope has a non-empty canonical `runId` and payload `{ "trigger": "manual" | "scheduled" | "run-once" }`. Parsing constructs one validated immutable block-7 request from those two facts. The same exact run ID and trigger flow to the executor, run-started event, result, terminal event, controller correlation, and any UI alias called “job ID.” Neither payload nor implementation introduces `jobId`, a generated worker identity, or a mode separate from trigger.

All three triggers execute the same eligible-assets processing pipeline. RunOnce is an origin value, not a second request type or an instruction to process a different work set. Serialization snapshots the already accepted request's two immutable fields once. It does not capture settings, schedule text, eligibility, skipped IDs, asset IDs, credentials, connection strings, or mutable UI state; block 11 remains authoritative for its own execution-time snapshots.

Alternative: separate `process-eligible-assets` and `run-once` types. Rejected because that invents execution modes absent from block 7 and risks changing behavior based on transport. Alternative: repeat `runId` inside payload. Rejected because two copies can disagree.

### Bind exactly one execute to readiness and one process

Worker stdout `ready` remains process-scoped with null run ID and sequence 1. The later host must emit and flush it before attempting to consume the initial execute frame; the controller must wait for a valid ready event before sending execute. Receiving bytes early does not create a run: only validation of the execute frame after readiness constructs the request.

The controller input sequence is independent from stdout sequence. Execute must be the first accepted input message at input sequence 1. No second execute is legal before, during, or after the run. Valid cancel commands use each exact next input sequence. A rejected frame commits no sequence or lifecycle state, matching block 15's transactional validator behavior.

Alternative: share one sequence across two unidirectional pipes. Rejected because neither side can atomically order writes on independent streams. Alternative: allow a request before ready. Rejected because launchers need a deterministic signal that worker initialization and protocol version are available.

### Make cancellation correlated and effect-idempotent

Cancel uses `control/cancel`, the active request's exact canonical `runId`, and canonical empty payload `{}`. It has no cancellation reason, token, deadline, or second command ID. Same-version unknown payload properties are tolerated but not retained or emitted.

A valid cancel accepted after execute but before executor invocation latches cancellation so execution begins with cancellation already requested and follows the existing run-started/terminal lifecycle. During execution it requests the same cooperative run cancellation. After execution reaches a terminal state it is a harmless no-op. Multiple correctly sequenced cancel messages for the same run are valid and have the same one-time effect; a byte replay with an already-consumed sequence is invalid, and a cancel before execute or for another/empty run ID is invalid. Parsing a cancel never mutates the immutable request.

Alternative: reject every duplicate cancellation. Rejected because cancellation commands race with process/event observation and cooperative cancellation is naturally idempotent. Alternative: accept any run ID while only one process exists. Rejected because that hides controller correlation bugs.

### Reuse block 15 framing and compatibility without reinterpretation

Input frames use strict UTF-8 without BOM, one compact JSON object on one non-empty line, LF emission, optional single LF/CRLF acceptance by the pure frame parser, no bare CR/literal embedded line break/additional frame data, and the same `MaxMessageBytes = 1,048,576` limit excluding the delimiter. Names/tokens are case-sensitive, GUID/Int64/timestamp forms are canonical, duplicate names are invalid, and size/encoding/framing are checked before JSON interpretation.

Within v1, unknown object properties are ignored at envelope and payload levels after duplicate detection. Missing or invalid known fields and unknown protocol, version, direction, category, or type fail closed. Unknown input never becomes execute, cancel, a diagnostic, or a future generic command. Canonical serialization emits only defined fields; cancel payload is emitted as an empty object.

Alternative: choose a smaller command-line limit. Rejected because one protocol-wide bound is already finalized and avoids inconsistent allocation/security assumptions. Alternative: preserve arbitrary unknown fields. Rejected because additive compatibility permits reading known semantics, not proxying untrusted state.

### Treat EOF as transport finality, not cancellation

EOF before any complete execute frame yields no request and cannot start work. EOF after bytes of an incomplete frame is invalid framing. Once a valid execute has been accepted, clean EOF/half-close means only that no later cancel can arrive; it does not request cancellation, alter the request, or prevent stdout terminal reporting. EOF after cancel or terminal adds no semantic effect.

Block 17 exposes enough pure state to distinguish no request, accepted execute, and malformed partial input, but does not read incrementally, schedule concurrent reads, decide when to stop the loop, or map those states to exit codes. Those mechanics and outcomes remain in blocks 22 and 23.

Alternative: interpret controller half-close as cancellation. Rejected because launchers may intentionally close stdin after delivery and cancellation has an explicit correlated command.

### Add no dedicated acknowledgement messages in v1

There is no execute-accepted or cancel-accepted stdout message. The existing run-started event is the first positive evidence that a valid request reached executor entry; the existing terminal event reports completed/cancelled/failed execution when available. A cancellation received after terminal is intentionally silent. Invalid input produces a safe structured codec/sequence validation failure for the consuming host, not a protocol acknowledgement or fabricated run event.

The pure input codec/validator owns stable, bounded, non-secret error categories and commits no partial typed value or state on failure. Block 22 decides host control flow/logging after such a failure, block 23 chooses the process exit outcome, and blocks 25/30 decide controller-side runtime classification and presentation. None may echo raw input, parser exceptions, stacks, credentials, or arbitrary payload text.

Alternative: add command acknowledgements. Rejected because they expand block 15's closed stdout event vocabulary, introduce ordering/race semantics not needed for one process/one run, and duplicate run-started/terminal evidence.

## Risks / Trade-offs

- [No explicit acknowledgement leaves a short acceptance-observation gap] → Treat run-started as execution evidence and use process/pipe failure handling in later launcher blocks; do not invent an ambiguous intermediate lifecycle event.
- [Clean EOF prevents later graceful cancel] → Controller launchers that need cancellation keep stdin open; half-close is an explicit choice, never implicit cancellation.
- [Repeated cancels consume sequence numbers] → Keep cancellation effect idempotent and sequence validation exact so loss/replay remains detectable.
- [Additive fields can hide producer typos] → Continue strict validation of all known fields and every semantic discriminator, and never retain unknown state.
- [One processing-specific execute type will need evolution for other jobs] → Block 47 introduces deliberate job generalization instead of weakening v1 now.

## Migration Plan

1. Verify applied block-7 and block-15 source contracts and names; stop rather than duplicate or redefine them if prerequisites differ.
2. Extend the isolated Core worker-protocol boundary with immutable controller envelope/payload values and explicit block-7 mapping.
3. Extend the pure frame codec and add a transactional controller-input sequence/lifecycle validator; do not wire console streams.
4. Add deterministic golden, round-trip, negative, correlation, cancellation-order, EOF-state, and boundary tests.
5. Roll back by removing the new controller-input contract/codec/validator/tests; no runtime transport, persisted data, configuration, or deployed behavior changes in this block.
