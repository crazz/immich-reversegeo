## Why

Block 15 defines a byte-sensitive event protocol that later worker and controller releases must share, but its implementation-level tests alone do not establish a durable compatibility corpus or fixture-retention policy. A focused golden and adversarial suite is needed before either side depends on the protocol so additive evolution remains compatible and invalid stdout-shaped input fails closed.

## What Changes

- Add a durable v1 compatibility/golden suite for every worker-to-controller event type defined by block 15, with typed round trips and byte-exact canonical output where the wire format is normative.
- Cover same-version additive fields; unsupported protocol identities, versions, directions, categories, and types; missing, duplicate, wrongly typed, noncanonical, malformed, and invariant-breaking fields.
- Cover GUID, UTC timestamp, and Int64 boundaries; stream sequence, correlation, cardinality, activity, and terminal-state transitions; and safe non-advancing rejection.
- Cover strict UTF-8 one-frame behavior for LF, CRLF, BOM, invalid UTF-8, exact-size, oversized, truncated, empty, literal-line-break, and multiple-frame inputs.
- Establish forward/backward fixture retention and intentional-update rules so released v1 evidence is not silently regenerated or overwritten.
- Keep the suite at the pure block-15 codec/state-machine boundary: no controller requests or commands, host, console redirection, pipe, process, launcher, stderr-tail, exit-code, or runtime-fault tests.

## Capabilities

### New Capabilities

- `worker-protocol-compatibility`: Durable golden fixtures, compatibility matrices, adversarial codec/state-machine coverage, and fixture evolution policy for worker protocol events.

### Modified Capabilities

- None.

## Impact

Implementation adds test sources and checked-in textual/binary fixtures to the narrowest existing MSTest project that references the applied block-15 Core protocol boundary. With the current repository layout that is `tests/ImmichReverseGeo.Tests`; if block 15 has introduced a narrower Core test project by apply time, use that project instead and do not create or move a project solely for this change. Production code, wire contracts, transport behavior, block-17 requests/commands, hosting, processing, and UI remain unchanged.
