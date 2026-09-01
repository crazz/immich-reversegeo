## Context

See [proposal.md](proposal.md) for motivation and [specs/worker-protocol-compatibility/spec.md](specs/worker-protocol-compatibility/spec.md) for the test contract. Finalized block 15 owns the v1 event schema, canonical compact UTF-8 encoding, one-frame parser, safe failure categories, and stream validator. It already specifies ten event types: ready, run-started, eligibility-determined, progress-changed, activity-started, activity-ended, log-emitted, completed, cancelled, and failed.

Block 15 is planning-complete but is not assumed to be applied when this plan is reviewed. The current general test project references Core, but block 15 permits a narrower Core test project if the applied Phase 2 layout provides one. This change must consume the resulting public codec and validator rather than predict class names or duplicate a test-only parser.

## Goals / Non-Goals

**Goals:**
- Preserve canonical v1 bytes and typed meaning with reviewable fixtures and explicit matrices.
- Exercise additive compatibility, fail-closed semantic incompatibility, adversarial framing/encoding, primitive boundaries, safe failures, and lifecycle state without process I/O.
- Make fixture retention and intentional updates durable across future producer/consumer versions.
- Keep failures diagnostic: a matrix row identifies the violated contract without snapshots obscuring the reason.

**Non-Goals:**
- Redefining or repairing block-15 production contracts, codec behavior, event vocabulary, failure categories, or state-machine rules.
- Testing block-17 controller requests/commands or their payload cases.
- Testing stdout/stderr routing, console ownership, write serialization/flushing, stdin loops, redirected pipes, child processes, launch/drain behavior, stderr tails, exit codes, or crash/protocol-failure presentation owned by blocks 21, 22, 25, 26, and 30.
- Starting ASP.NET, a Generic Host, processing services, `ProcessingState`, scheduler, databases, geodata, or a worker executable.

## Decisions

### Consume the applied block-15 public boundary

At apply time, first verify that block 15 exposes the production serializer/parser, named `MaxMessageBytes` policy, typed validation result, and stream validator. Tests call those same public entry points and assert stable wire-independent failure codes; they do not use reflection, private hooks, `System.Text.Json` as an alternative parser, or a test copy of protocol rules. If the prerequisite is absent or materially differs from its finalized artifacts, stop and reconcile block 15 rather than compensate in tests.

Alternative considered: build fixtures against guessed records while block 15 is unapplied. Rejected because it would freeze invented APIs instead of the wire contract.

### Use a logical suite layout that does not assume a new project

Use the narrowest already-existing MSTest project that references the applied Core protocol boundary. The expected logical layout is:

```text
<protocol-test-project>/WorkerProtocol/Compatibility/
  CanonicalFrameTests.cs
  DecodeCompatibilityTests.cs
  FramingAndEncodingTests.cs
  StreamLifecycleTests.cs
  SafeFailureTests.cs
  Fixtures/v1/
    README.md
    canonical/<event-type>.json
    compatibility/additive-*.json
    compatibility/original-*.json
```

Exact C# filenames may follow the applied repository's conventions. Canonical fixture filenames are type-based; variant rows (all trigger tokens, log levels, terminal outcomes, and boundary values) may be data rows/builders when they do not define a distinct canonical event type. Fixtures are raw UTF-8 bytes, not string literals whose source escaping or newline conversion can hide encoding. Canonical `.json` files contain the compact JSON object without a transport delimiter; framing tests independently append LF or CRLF. The fixture README records protocol/version, byte conventions, provenance, and update policy.

Alternative considered: create `ImmichReverseGeo.Core.Tests` now. Rejected because source prerequisites are not applied and a project move is unrelated churn. Alternative considered: only inline expected strings. Rejected because retained cross-version evidence is harder to inspect and accidental bulk regeneration is easier.

### Give every event type one canonical golden and one typed round trip

Pin one representative canonical object, property order, spelling, escaping, lower-case GUID `D` form, unquoted decimal Int64 form, and seven-fraction UTC `Z` timestamp for each of the ten v1 event types. For each fixture:
1. parse through the production one-frame codec and compare the complete typed value;
2. serialize that value and compare exact fixture bytes;
3. parse the serialized bytes and compare the typed value again; and
4. serialize repeatedly to prove deterministic output.

Use targeted table rows, rather than multiplying golden files, to cover manual/scheduled/run-once triggers, trace/information/warning/error levels, all three terminal outcomes, JSON escaping, zero counts, and nonzero coherent counts. Byte equality is required for canonical output and omission of ignored additions; semantic equality is used for round trips. CRLF acceptance is not allowed to redefine canonical LF emission, which belongs to block 21.

Alternative considered: snapshot every combinatorial value. Rejected because it creates noisy, mechanically regenerated fixtures without increasing discriminator coverage.

### Separate additive compatibility from unknown semantics

For supported protocol/version 1, inject unknown scalar, null, array, and nested-object properties separately at envelope and payload levels. Each must decode to the same known typed event, and reserialization must omit additions and equal the canonical known-field bytes. Exact duplicate names are rejected even when the duplicated name is unknown. A differently cased known name remains an unknown additive property only when the correctly cased required property is also present; by itself it cannot satisfy that requirement.

Unknown or case-varied protocol identifiers, versions, directions, categories, and types fail closed. Include known type under the wrong category, unknown type under a known category, unknown category with a known type token, non-integer/negative/out-of-range versions, and future integer versions. No row may coerce an unknown event into a diagnostic or terminal event.

Alternative considered: one generic “unknown field” and one generic “unknown discriminator” case. Rejected because envelope/payload skipping and category/type routing are independent failure surfaces.

### Use field and primitive matrices, not one omnibus malformed case

Generate one-field mutations from valid canonical bases so each row proves a single outcome: missing, exact duplicate, wrong JSON kind, invalid known value, forbidden null, blank required text, category/type mismatch, and malformed/truncated JSON. Cover all required envelope fields and every payload field at least once across the event vocabulary, with type-specific invariant rows for counts, terminal outcome/failure detail, trigger, level, activity identity/label, and timestamps.

Primitive boundary coverage includes:
- GUID: canonical non-empty values accepted; empty, upper-case, braces, compact/non-hyphenated, malformed, wrong JSON kind, and illegal null rejected for run-scoped/activity identities; ready requires null run correlation.
- Timestamp: valid year boundaries representable by the finalized codec, exact seven fractional digits, and chronological start/end boundaries; offsets, lower-case `z`, missing/extra fractional digits, impossible dates, non-string values, and end-before-start rejected.
- Int64: zero and `Int64.MaxValue` accepted where domain invariants permit; negative counts, overflow/underflow, quotes, fractions, exponents, and inconsistent processed totals rejected. Sequence separately covers positive/canonical form and exact-next state behavior.

Alternative considered: property-based fuzzing as the primary oracle. Rejected because deterministic named cases and stable failures are more reviewable; bounded fuzzing may supplement but cannot replace the matrix.

### Exercise one-frame bytes independently from pipe behavior

Build exact byte arrays and use the named block-15 byte limit. Prove a valid ASCII-padded diagnostic at exactly 1,048,576 JSON bytes is evaluated, while a one-byte-oversized valid-shaped frame and a one-byte-oversized malformed frame both return size failure before JSON classification. Add multibyte UTF-8 values around byte-count boundaries, BOM, invalid UTF-8, empty input, LF-only input, bare CR, one LF, one CRLF, duplicate delimiters, literal LF/CR inside JSON, escaped `\n`/`\r` data, truncated objects/strings/UTF-8 sequences, trailing garbage, and two otherwise-valid frames supplied as one parse call.

“Stdout purity” at this layer means every candidate byte sequence must be exactly one valid protocol frame: plain log text, prefixes/suffixes, whitespace-only lines, and JSON with unsupported semantics are rejected with no typed event. Tests do not redirect `Console.Out`, assert logger routing, emit/flush lines, or read streams; those are transport/process responsibilities.

Alternative considered: `TextReader` or memory-pipe tests. Rejected because block 15 defines a byte-bounded single-frame codec and later blocks own incremental I/O.

### Validate state transitions and rejection atomicity

Drive the public stream validator with explicit event sequences. Accept ready-only finalization as no accepted run; accept completed empty and non-empty runs after eligibility; accept cancelled/failed directly after run-started for pre-count termination; and accept valid progress, logs, paired activities, and one final terminal. Reject not-ready-first, duplicate ready/start/eligibility/terminal, gaps/duplicates/regressions, correlation changes/null/empty run IDs, progress/log/activity before their legal point, unmatched/duplicate activity ends, terminal with unfinished required lifecycle/activity state, completed without eligibility, terminal/result disagreement, and every post-terminal event.

After each rejected candidate, submit the corrected event at the same expected sequence and prove it is accepted; invalid input must not consume sequence, change correlation, close/open activity, or make the stream terminal. Finalization distinguishes ready-only, accepted complete, and accepted run missing terminal without fabricating an event. Int64 maximum is covered at the codec boundary; sequence overflow is asserted through the public validator only if its ordinary public state can reach that boundary without a test-only backdoor.

Alternative considered: assert only final pass/fail. Rejected because transactional rejection is essential for deterministic downstream classification.

### Retain fixtures as compatibility evidence

Committed v1 canonical and original-consumer fixtures are append-only evidence. Do not blanket-regenerate or overwrite them when serializer code changes. An intentional fixture byte change requires a reviewed protocol decision: either restore v1 output, or introduce a new protocol version and a new versioned fixture directory. Keep old canonical fixtures readable for every version still declared supported, and retain explicit unsupported-future-version fixtures to prove fail-closed behavior.

Forward-compatibility fixtures model a v1 consumer reading v1 frames with additive unknown fields; retain them and require canonical reserialization to strip additions. Backward-compatibility fixtures model the current producer/consumer continuing to read the original v1 frames and reproduce their canonical bytes. Additive fields may add new compatibility fixtures but must not rewrite canonical v1 output. Any narrowly approved correction to a mistaken fixture must be documented in the fixture README and reviewed against the finalized specification; a test update cannot silently redefine production wire behavior.

Alternative considered: auto-update snapshots on failure. Rejected because it turns protocol breakage into an unreviewed test-data change.

### Assert safe failures without overfitting prose

For each failure family, assert the stable block-15 machine-readable category, no valid/partial event, bounded diagnostic length, and absence of raw-input sentinels, payload text, parser exception types, stack traces, credentials, connection strings, and SQL-like secrets. Avoid pinning full human diagnostic prose unless block 15 explicitly makes it normative.

Alternative considered: exact exception-message snapshots. Rejected because safe wording may improve without a wire compatibility change.

## Risks / Trade-offs

- [Golden fixtures become brittle to harmless serializer refactors] → Require exact bytes only for canonical wire output; use typed/category assertions elsewhere.
- [A large matrix duplicates block-15 unit tests] → Treat block 15's tests as implementation construction coverage and block 16 as retained cross-version/adversarial evidence; share production builders only, not expected-output generators.
- [Applied project layout differs from today's tree] → Resolve the narrowest existing Core-referencing MSTest project at apply time and preserve the logical folder/fixture structure.
- [Exact-limit fixtures bloat the repository] → Construct padded boundary inputs deterministically in test code from a small canonical base; retain only protocol-significant golden files.
- [Mutation helpers accidentally reproduce production validation] → Helpers only remove/replace raw JSON properties or bytes; expected outcomes remain explicit data rows.
- [Future block-17 cases leak into this suite] → Limit every fixture to `worker-to-controller` event envelopes and reject request/command additions during review.

## Migration Plan

1. Verify block 15 is applied and re-read its public codec, failure categories, stream validator, tests, and selected test-project layout.
2. Add the versioned fixture README and ten canonical v1 event fixtures without generating them from the production serializer as the sole oracle.
3. Add golden/round-trip and additive/backward-compatibility tests, then field/primitive and safe-failure matrices.
4. Add framing/encoding/size and lifecycle/rejection-atomicity suites.
5. Run focused tests repeatedly, the normal default-exclusion suite, strict OpenSpec validation, and a boundary review proving no block-17 or transport/process coverage was introduced.
6. Roll back by removing only block-16 test/fixture files; no production, persisted-data, configuration, or runtime behavior changes.
