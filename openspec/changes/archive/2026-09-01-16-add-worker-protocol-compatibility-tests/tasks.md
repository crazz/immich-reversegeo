## 1. Verify block-15 prerequisites and place the suite

- [x] 1.1 Re-read the applied block-15 Core protocol source and tests; verify the public serializer/parser, named 1,048,576-byte policy, typed failure result/categories, event mappings, and stream validator exist, and stop for reconciliation rather than recreating missing production behavior.
- [x] 1.2 Select the narrowest existing MSTest project that references the applied Core boundary (currently expected to be `tests/ImmichReverseGeo.Tests` unless block 15 supplied a narrower project) and add the logical `WorkerProtocol/Compatibility` test and `Fixtures/v1` layout without creating or moving a project solely for block 16.
- [x] 1.3 Add a fixture README recording raw UTF-8/no-BOM/no-delimiter canonical conventions, protocol/version provenance, forward/backward retention rules, and the prohibition on blanket snapshot regeneration.

## 2. Establish canonical and retained compatibility fixtures

- [x] 2.1 Hand-review and retain one compact canonical fixture for each of ready, run-started, eligibility-determined, progress-changed, activity-started, activity-ended, log-emitted, completed, cancelled, and failed, with explicit expected typed values independent of production-generated snapshots.
- [x] 2.2 Add tests that parse every canonical fixture, compare the complete typed value, serialize to exact fixture bytes repeatedly, and parse the serialized bytes again.
- [x] 2.3 Add data rows for all trigger and diagnostic-level tokens, all terminal outcomes, escaping, zero/nonzero coherent counts, and exact block-7/8 mappings without multiplying fixtures unnecessarily.
- [x] 2.4 Add retained original-v1 backward fixtures and separate additive-v1 forward fixtures; prove additions at envelope/payload levels decode to the same event and canonical reserialization strips them without changing original fixtures.

## 3. Build compatibility and field-validation matrices

- [x] 3.1 Cover unknown scalar, null, array, and object fields at envelope and payload levels plus duplicate known and unknown properties, including case-varied required names that cannot satisfy the canonical required field.
- [x] 3.2 Cover unsupported, wrong-kind, negative/out-of-range, and case-varied protocol identifiers, versions, directions, categories, and types; include known-type/wrong-category and unknown-type/known-category pairs and prove no generic event coercion.
- [x] 3.3 Mutate canonical bases so every required envelope field and every event-type payload field is covered by missing, duplicate, wrong-kind, forbidden-null/blank, invalid-value, noncanonical-form, or invariant-failure rows as applicable.
- [x] 3.4 Add GUID cases for canonical/non-empty, empty, upper-case, braces, compact/malformed, wrong kind, and ready-versus-run/activity null rules.
- [x] 3.5 Add timestamp cases for supported representable year boundaries, exact seven-fraction UTC Z form, offsets/case/precision/impossible-date failures, and terminal chronology.
- [x] 3.6 Add Int64 cases for zero and maximum where valid, negatives, overflow/underflow, quoted/fractional/exponent forms, nonnegative counts, coherent processed totals, and canonical sequence representation.

## 4. Cover byte framing, encoding, and codec-level purity

- [x] 4.1 Construct valid diagnostic frames at the exact named byte maximum and valid-shaped/malformed frames one byte over; prove UTF-8 byte counting and size classification before JSON parsing without committing megabyte fixtures.
- [x] 4.2 Cover ASCII and multibyte UTF-8 near the boundary, BOM, invalid and truncated UTF-8, empty/whitespace input, no delimiter, one LF, one CRLF, bare CR, repeated delimiters, literal versus escaped CR/LF, truncated JSON tokens, trailing data, and multiple frames in one call.
- [x] 4.3 Add codec-level stdout-purity cases for ordinary logs, prefixes/suffixes, whitespace-only lines, and unsupported semantic JSON; assert no typed/partial event without redirecting console streams or testing stderr, emission, flushing, concurrency, pipes, or processes.

## 5. Cover stream lifecycle and transactional rejection

- [x] 5.1 Add accepted ready-only, completed-empty/nonempty, direct pre-count cancelled/failed, progress/diagnostic, and paired-activity sequences with exact consecutive sequence and stable run correlation.
- [x] 5.2 Add rejected not-ready-first, duplicate ready/start/eligibility/terminal, gap/duplicate/regression, changed/null/empty correlation, illegal pre-eligibility events, completed-without-eligibility, unmatched/duplicate/unfinished activity, terminal disagreement, and post-terminal cases.
- [x] 5.3 After every rejected candidate family, submit the corrected event at the unchanged expected sequence and prove sequence, correlation, lifecycle, activity, and terminal state were not mutated.
- [x] 5.4 Verify finalization distinguishes ready-only, accepted complete, and accepted missing-terminal streams without fabricating a terminal; exercise sequence Int64 overflow only through an ordinary reachable public validator state, never a test-only backdoor.

## 6. Verify safe failures, retention, and scope

- [x] 6.1 For each stable block-15 failure family, assert the machine-readable category, no valid/partial event, bounded diagnostic text, and no raw sentinels, parser types, stacks, credentials, connection strings, or SQL-like content; avoid full prose snapshots unless normative.
- [x] 6.2 Review the fixture diff manually: original/canonical v1 evidence remains unchanged, additive fixtures are separate, and any approved correction is documented rather than generated over existing files.
- [x] 6.3 Prove test references and fixtures contain no controller request/command cases and no ASP.NET/Generic Host, `ProcessingState`, scheduler, console/stdin/stdout/stderr, pipe, child-process, launcher, fixture-executable, exit-code, stderr-tail, crash, or runtime-classification path.
- [x] 6.4 Run focused compatibility MSTests repeatedly, `npm run test` with default exclusions, `openspec validate 16-add-worker-protocol-compatibility-tests --strict`, and `openspec status --change 16-add-worker-protocol-compatibility-tests`.
