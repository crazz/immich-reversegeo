## Context

See [proposal.md](proposal.md) for motivation and [specs/worker-protocol-events/spec.md](specs/worker-protocol-events/spec.md) for normative behavior. Block 7 places validated transport-neutral `ProcessingRunRequest`/`ProcessingRunResult` models in Core; block 8 defines a run-scoped event session whose accepted order is `RunStarted`, optional `EligibilityDetermined`, progress/activity/log events, and `RunFinished`. Block 8 expressly leaves wire versioning, JSON, timestamps/sequences, framing, and redaction to Phase 3.

The first worker message must announce readiness before stdin supplies a request, so it cannot truthfully carry the accepted run identity. Conversely, once a request is accepted, block 7 already supplies the only identity needed. The protocol must represent both facts without inventing a second job/run namespace.

## Goals / Non-Goals

**Goals:**
- Define one deterministic v1 worker-to-controller envelope and typed event schema that consumes rather than mutates block-7/8 meanings.
- Make compatibility, canonical JSON, byte limits, framing, safe parse failures, correlation, sequence, and lifecycle/cardinality mechanically testable without process I/O.
- Reserve stdout for protocol frames and stderr for ordinary logs as explicit constraints for later transports.
- Keep schema/codec ownership dependency-light so both future worker and controller can consume the exact same code.

**Non-Goals:**
- Defining any controller-to-worker request or command type (block 17).
- Writing/flushing stdout or serializing concurrent reporter calls (block 21), reading stdin or owning a worker loop (block 22), or defining exit codes (block 23).
- Starting/draining a child process, incrementally reading its pipes, retaining stderr, or classifying missing terminals and crashes at runtime (blocks 25 and 30).
- Changing `ProcessingState`, the event reporter/session, executor, coordinator, hosting, role selection, scheduling, processing behavior, or UI.

## Decisions

### Use one protocol identity and integer major version

V1 envelopes use `protocol: "immich-reversegeo.worker"` and `version: 1`. Protocol identifier mismatch and unsupported version are distinct structured failures. Version is a JSON integer rather than `"1.0"`: compatibility changes are deliberate whole-schema versions, while additive same-version fields are handled by the unknown-field policy.

Alternative considered: infer protocol solely from event type. Rejected because random stdout JSON or a future unrelated line protocol could be misclassified. Alternative considered: semantic-version strings. Rejected because minor/patch negotiation would imply compatibility behavior not needed by this one-image internal protocol.

### Use the block-7 run ID as the job ID

The wire field is named `runId` and equals `ProcessingRunRequest.RunId`. Documentation and later launcher APIs may call it a job ID, but that is an alias. No `jobId` field or translation table exists. This preserves correlation across coordinator, executor, reporter, protocol, and UI and prevents mismatched identities.

`ready` is process-scoped, occurs before request acceptance, and therefore requires `runId: null`. Every other v1 event requires the same canonical non-empty GUID. The stream validator learns correlation from `run-started`. A rejected invocation never creates a run ID under block 7, and ready by itself does not violate that rule.

Alternative considered: create a launcher job ID and later nest a domain run ID. Rejected because one worker handles one accepted processing run and there is no durable queue/retry identity in this phase. If future retries require an attempt/job distinction, that is a new protocol version or additive explicitly specified concept, not an ambiguous v1 duplicate.

### Separate direction, category, and type

Every v1 event declares `direction: "worker-to-controller"`. Category groups validation/routing while type identifies the exact payload:

| Category | Type | Required payload fields |
|---|---|---|
| `lifecycle` | `ready` | none (empty object) |
| `lifecycle` | `run-started` | `trigger` string, `startedAtUtc` timestamp |
| `lifecycle` | `eligibility-determined` | `eligibleCount` Int64 |
| `progress` | `progress-changed` | `processedCount`, `updatedCount`, `skippedCount`, `failedCount` Int64 |
| `activity` | `activity-started` | `activityId` GUID, `label` string |
| `activity` | `activity-ended` | `activityId` GUID |
| `diagnostic` | `log-emitted` | `level` string, `message` string |
| `terminal` | `completed` / `cancelled` / `failed` | `trigger`, `startedAtUtc`, `endedAtUtc`, all four Int64 counts, `failureMessage` nullable string |

Every listed field is required; `failureMessage` is JSON null for completed/cancelled and a non-blank safe string for failed. Trigger tokens are `manual`, `scheduled`, and `run-once`; log tokens are `trace`, `information`, `warning`, and `error`. The terminal type is the outcome discriminator and must agree with failure-message rules. Terminal repeats trigger and result facts so it is a self-contained serialization of block 7; stream validation confirms it agrees with run-started.

`timestampUtc` is the event occurrence time. For run-started it equals `startedAtUtc`; for terminal it equals `endedAtUtc`. This avoids two conflicting timestamps for those boundaries while leaving eligibility/progress/activity/log/ready timestamps meaningful.

Alternative considered: one `run-finished` type with nested outcome. Rejected because the existing plan and process diagnostics need closed terminal types, while all still map from one block-8 `RunFinished`. Alternative considered: flatten payload properties into the envelope. Rejected because type-specific fields would make the common envelope sparse and weaken discriminator validation.

### Make the JSON representation canonical and hand-controlled at the boundary

Canonical field order is the envelope order from the spec, followed by per-type payload field order from the table. The codec uses strict case-sensitive names, kebab-case tokens, lower-case GUID `D` text, unquoted nonnegative Int64 integers, and seven-fraction-digit UTC `Z` timestamps. It writes compact UTF-8 with escaping controlled by the shared codec and no serializer metadata on block-7/8 domain types.

A boundary-owned writer/parser is preferred over serializing polymorphic domain records directly. It can reject duplicate properties, noncanonical numbers/timestamps, unsupported discriminators, and invalid payload combinations while preventing serializer option drift. `System.Text.Json` primitives are sufficient; no new dependency is required. Domain types remain transport-neutral, and explicit mapping functions produce protocol payload values.

Alternative considered: annotate block-7/8 records. Rejected because those contracts explicitly exclude serializer concerns and because wire evolution must not mutate in-process domain models. Alternative considered: permissive enum/string conversion. Rejected because silent case/number coercion makes golden bytes and compatibility ambiguous.

### Define one bounded NDJSON frame independently from stream I/O

`MaxMessageBytes` is 1,048,576 bytes excluding LF/CRLF. The pure codec accepts a byte span for one possible frame, checks size first, rejects UTF-8 BOM and invalid UTF-8, removes at most one LF or CRLF, rejects other literal line breaks/empty content, and then parses one JSON object. Emitters later append LF. JSON escape sequences such as `\n` remain inside the object and do not frame another message.

One MiB leaves generous room for existing diagnostic labels/messages while bounding allocation and failure input. It is protocol-wide so block 17 can reuse the same framing rather than choose a competing stdin limit. Block 16 expands adversarial compatibility coverage; block 15 still includes deterministic boundary tests needed to implement the codec correctly.

Alternative considered: `TextReader.ReadLineAsync` with an after-the-fact character limit. Rejected as the protocol definition because UTF-16 character counts do not bound incoming UTF-8 bytes and a reader may allocate the oversized line first. Actual bounded incremental pipe reading remains blocks 22/25.

### Ignore additive fields but reject unknown semantics and duplicate names

For protocol/version 1, unknown properties at every object level are skipped after duplicate-name detection. This permits additive metadata without changing existing consumers. Missing known fields, wrong types, invalid known values, and category/type mismatches remain errors. Unknown protocol/version/direction/category/type fail closed; an unknown event is not converted to a log or generic event.

Alternative considered: reject every unknown field. Rejected because any additive field would unnecessarily require a coordinated version bump. Alternative considered: preserve an opaque unknown event. Rejected because downstream UI/lifecycle code cannot safely assign ordering or terminal semantics to it.

### Keep pure decoding separate from stateful stream validation

The codec validates one envelope/payload. A separate `WorkerProtocolEventStreamValidator`-style value/state machine validates accepted-stream context: first ready at sequence 1, exact +1 sequence thereafter, one run ID, legal block-8 lifecycle, activity pairing, terminal finality, and cardinality. It commits state only after full validation, so a rejected event does not consume sequence or corrupt activity tracking.

Incrementally, at most one terminal can be accepted. A finalization check for a stream that accepted run-started requires exactly one terminal; it reports incomplete/missing terminal without inventing a failed event. Blocks 25/30 decide how an actual EOF/process exit turns that signal into launcher/UI failure. A stream with ready only is valid but has no accepted run.

Sequence is stream-wide rather than per-run because v1 has one ready and at most one accepted run per worker process. Sequence starts at 1 and must be exact, not merely increasing, so dropped/corrupt lines are detectable. Int64 max cannot be followed and is therefore terminal for sequencing even if no terminal event appeared.

Alternative considered: restart sequence at one after ready. Rejected because duplicate/missing transition messages become harder to detect. Alternative considered: validate ordering only in block 30. Rejected because order is part of the compatibility contract and must have one shared deterministic implementation; block 30 owns runtime classification, not the rules.

### Return structured safe failures

Parsing returns a discriminated success/failure result. Failure codes cover `message-too-large`, `invalid-encoding`, `invalid-framing`, `malformed-json`, `invalid-envelope`, `unsupported-protocol`, `unsupported-version`, `unsupported-type`, `invalid-payload`, `invalid-sequence`, `invalid-correlation`, and `invalid-lifecycle` (exact .NET member names may follow repository conventions while wire-independent code values remain stable for tests). Diagnostics are bounded summaries built from known field/type names; they never echo payloads, raw input, parser exceptions, or stacks. No error path returns a partially typed event.

Alternative considered: throw parser exceptions to all callers. Rejected because later launcher logic needs stable classification and safe diagnostics without exposing untrusted stdout content.

### Own the schema and codec in Core without owning transports

Place protocol contracts, constants, mapping, codec, validation result, and stream validator under a dedicated `ImmichReverseGeo.Core.WorkerProtocol` namespace/folder. Core already owns block-7/8 dependency-light contracts and can be referenced by both sides. This is schema ownership, not a new composition root. Tests live in the existing MSTest project unless the repository's applied Phase 2 layout supplies a narrower Core test project.

Alternative considered: place protocol beside Web launcher code. Rejected because the worker would depend on the control plane. Alternative considered: create a new project now. Rejected as unnecessary project churn for contracts using only the BCL and Core models.

## Risks / Trade-offs

- [Ready has no run identity] → Require explicit JSON null, first-message sequence 1, and no run creation; bind correlation only at run-started.
- [A one-MiB limit permits unexpectedly large diagnostics] → Keep it a hard upper bound, require safe messages, and allow later application-level message caps without changing framing.
- [Ignoring unknown fields can hide producer mistakes] → Continue rejecting duplicates, wrong known-field types, invalid invariants, and every unknown semantic discriminator.
- [Exact canonical parsing is stricter than generic JSON interoperability] → Prefer deterministic internal one-image behavior and golden tests; compatibility extensions are additive fields or a deliberate version.
- [A valid terminal result can still be absent because the process/pipe failed] → Stream finalization reports incompleteness; block 30 owns runtime crash/protocol-failure presentation.
- [Schema code in Core adds JSON responsibility there] → Isolate it in a dedicated namespace and do not annotate or change transport-neutral domain contracts.

## Migration Plan

1. Verify applied block-7/8 source types and meanings; stop rather than duplicate or redefine them if prerequisites are absent.
2. Add isolated v1 constants, envelope/payload values, explicit domain-event mappings, and validation results in Core.
3. Add the deterministic single-message writer/parser and then the stateful stream validator.
4. Add golden UTF-8 fixtures, round trips, boundary/negative tests, and source-boundary tests proving no host/stream/UI changes.
5. Roll back by removing the new protocol/codec/test files; there is no persisted data, configuration, runtime wiring, or deployed protocol transport in block 15.
