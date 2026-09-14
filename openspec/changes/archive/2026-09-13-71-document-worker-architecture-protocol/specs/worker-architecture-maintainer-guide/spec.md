## Purpose

Preserves an evidence-backed maintainer reference for the released worker architecture while keeping private worker controls out of supported public operation.

## ADDED Requirements

### Requirement: Architecture and composition reference
The maintainer guide SHALL identify the finalized Standard/Web-only Web control plane, private InternalWorker, and direct Run-once composition roots in a compact matrix. It MUST identify the dependency categories allowed in each root, the heavy geodata categories forbidden from the long-lived Web root, and the source/tests that enforce those boundaries.

#### Scenario: Maintainer changes host composition
- **WHEN** a maintainer evaluates a registration or dependency change
- **THEN** the guide identifies the applicable root, its allowed and forbidden dependency categories, and the enforcing composition/dependency evidence

### Requirement: Public and private selection boundaries
The guide SHALL distinguish public deployment modes from the sole exact private `--internal-worker` invocation. It SHALL document that private protocol v2 is selected only by `IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION=2`, absence selects frozen v1, and any other present value fails before readiness. It MUST state that private selectors are not public configuration, negotiation, persisted settings, logging fields, or supported operator commands.

#### Scenario: Maintainer investigates worker selection
- **WHEN** a maintainer needs to determine why a child selected v1, v2, or failed before readiness
- **THEN** the guide identifies the controller-owned selector/absence rules and redirects supported operator usage to the public deployment guide without publishing a private invocation recipe

### Requirement: Versioned job and identity contract
The guide SHALL identify protocol `immich-reversegeo.worker`, describe v1 as frozen ProcessAssets, and describe v2 as the closed ProcessAssets, CoordinateLookup, and CacheMutation job family. It MUST state that each job has one canonical identity, ProcessAssets reuses its processing RunId, and no attempt, lease, cancellation, or telemetry identity is introduced.

#### Scenario: Maintainer correlates a worker job
- **WHEN** a maintainer follows one job across controller, protocol, telemetry, and terminal evidence
- **THEN** the guide uses only the canonical runId or jobId appropriate to the protocol generation and explains their ProcessAssets identity equivalence

### Requirement: Stream, ordering, terminal, and exit contract
The guide SHALL document controller/worker ownership of stdin, stdout, and stderr; strict bounded NDJSON framing; readiness, independent sequence, correlation, accepted-event ordering, flushing, EOF, terminal-last, and post-terminal rules; safe validation failures; and the managed exit-code table and precedence. It MUST identify exit 3 plus a valid Failed terminal as an expected ProcessAssets advisory-lock-busy result, distinguish raw platform death, and state that an accepted valid terminal remains authoritative over later process evidence.

#### Scenario: Maintainer classifies process completion
- **WHEN** terminal, exit, or stream evidence appears inconsistent
- **THEN** the guide provides the precedence and finality rules needed to classify the job without rewriting a committed terminal or inferring protocol meaning from an exit alone

### Requirement: Parent finality, locking, arbitration, and cache safety
The guide SHALL document parent-owned exactly-once finality, correlated cooperative cancellation, the fixed 10-second grace, whole-tree escalation, complete stream drain, disposal/release ordering, and no automatic retry. It SHALL document the ProcessAssets-only PostgreSQL advisory-lock key, derivation, dedicated-session lifetime, explicit release, and same-database scope; distinguish it from process-local first-wins heavy-job arbitration; state the multi-container/direct-writer caveat; and describe atomic cache candidate validation/publication and old-cache preservation.

#### Scenario: Maintainer diagnoses contention or cancellation
- **WHEN** a job is busy, cancelled, killed, or interrupted during cache publication
- **THEN** the guide identifies which exclusion mechanism applies, its lifetime and scope, the expected finality, and the cache state that must remain valid

### Requirement: Safe evidence-led debugging
The guide SHALL provide a redaction-first workflow using canonical job identity, closed job kind/origin, stable EventIds 5901, 6601–6605, 6610–6612, 6620–6623, 6630, 6640–6641, and 6650, followed by links to protocol/process tests, dependency sentinels, PostgreSQL lock tests, Docker smoke/integration evidence, and memory-soak evidence. It MUST prohibit hand-editing or replaying protocol frames, direct public use of private selectors, and capture of raw protocol streams/tails, arguments, environment/configuration, payloads, coordinates, paths, SQL, credentials, connection strings, tokens, arbitrary exception text, or stacks.

#### Scenario: Maintainer investigates a failed child safely
- **WHEN** a maintainer follows the documented debugging workflow
- **THEN** diagnostics remain bounded and redacted, supported public troubleshooting is reached through the block-70 cross-link, and every deeper claim has a source or evidence link

### Requirement: Documentation isolation and drift verification
The guide SHALL be discoverable from the existing maintainer documentation surface but MUST remain outside public MkDocs navigation. Verification MUST resolve repository and block-70 links, build the public site, and compare documented frozen tokens and closed vocabularies against finalized source/tests, including protocol/version selectors, job kinds, frame limit, exit table/precedence, lock key/derivation, cancellation grace, telemetry IDs, and dependency allow/deny categories.

#### Scenario: Documentation contract drifts
- **WHEN** a source-token, test, Docker evidence, public cross-link, or docs-build check disagrees with the guide
- **THEN** verification fails and the guide is corrected from landed evidence rather than preserving an aspirational or duplicated claim
