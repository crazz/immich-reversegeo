## 1. Verify prerequisites and schema ownership

- [ ] 1.1 Re-read the applied block-7 request/result and block-8 event/session source contracts; stop rather than recreate or change their identity, accounting, lifecycle, activity, log, or terminal meanings if prerequisites are absent.
- [ ] 1.2 Create the isolated `ImmichReverseGeo.Core.WorkerProtocol` contract boundary with fixed protocol/version, direction/category/type tokens, maximum-message size, canonical field names, and stable validation-failure categories.

## 2. Define immutable event mapping

- [ ] 2.1 Add validated immutable envelope and typed v1 payload values for ready, run started, eligibility determined, progress changed, activity started/ended, log emitted, and completed/cancelled/failed.
- [ ] 2.2 Add explicit block-7/8-to-wire mapping that uses `ProcessingRunRequest.RunId` as the sole job/run identity, maps ready to null run correlation, preserves terminal results, and rejects unsafe or invariant-breaking payloads.

## 3. Implement deterministic single-message codec

- [ ] 3.1 Add canonical compact UTF-8 serialization with fixed property order, camel-case names, kebab-case tokens, canonical GUID/Int64/UTC formats, and only defined v1 fields.
- [ ] 3.2 Add bounded one-frame parsing that checks the 1,048,576-byte limit before JSON parsing, enforces UTF-8/no-BOM/LF-or-CRLF framing, detects duplicate properties, tolerates additive unknown object fields, and fails closed on unknown protocol/version/direction/category/type.
- [ ] 3.3 Return either one fully validated typed event or one safe structured bounded failure without partial values, raw input echo, parser exception, or stack trace.

## 4. Validate stream correlation and lifecycle

- [ ] 4.1 Add a pure stateful validator for exactly one first ready at sequence 1, exact consecutive stream sequence, stable run correlation, legal eligibility/progress/activity/log ordering, activity pairing, and terminal finality.
- [ ] 4.2 Add stream finalization that distinguishes ready-only/no-run from an accepted run missing its required terminal, without mapping EOF/process exit to UI or exit behavior.

## 5. Add deterministic contract tests

- [ ] 5.1 Add byte-for-byte golden serialization and round-trip tests for every v1 type, all trigger/log/terminal variants, canonical timestamps/GUIDs/Int64 boundaries, and exact block-7/8 mapping.
- [ ] 5.2 Add framing/encoding/size tests for exact-limit input, one-byte oversize rejection before JSON parsing, multibyte UTF-8, BOM/invalid UTF-8, LF/CRLF, empty/multiple/bare-CR lines, and escaped versus literal newlines.
- [ ] 5.3 Add compatibility/error tests for additive unknown fields at each object level, missing/wrong/duplicate known fields, malformed JSON, unsupported identifiers/versions/types, category/type mismatches, invalid payload invariants, and safe bounded failures.
- [ ] 5.4 Add sequence/correlation/lifecycle tests for duplicate/gapped/regressing/overflow sequences, ready/start/eligibility/terminal cardinality, pre-count cancellation/failure, completed-without-eligibility rejection, activity pairing/cleanup order, post-terminal rejection, and missing-terminal finalization.
- [ ] 5.5 Prove tests use no ASP.NET host, child process, console stream, stdin reader, launcher, `ProcessingState`, scheduler, or exit-code path; run focused MSTests, `npm run test`, and strict OpenSpec validation.
