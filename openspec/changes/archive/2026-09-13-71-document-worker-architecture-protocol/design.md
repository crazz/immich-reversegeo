## Context

See `proposal.md` for motivation and `specs/worker-architecture-maintainer-guide/spec.md` for the documentation contract. Blocks 15–32 define v1 protocol, process launch/finality, and the ProcessAssets advisory lock; blocks 40–56 define public modes, private v2 selection, generalized jobs, local arbitration, cache publication, and dependency policy; blocks 65–69 define telemetry and process/Docker/soak evidence. Maintainer content currently has two repository-only files. Because `mkdocs.yml` uses `docs/website` as `docs_dir`, the new guide is not a public site page.

## Goals / Non-Goals

**Goals:**
- Produce one source-linked maintainer guide whose composition and protocol matrices can be checked against landed constants, source, tests, and Docker evidence.
- Make protocol finality, exclusion scope, cache safety, and the Web dependency boundary hard to weaken accidentally.
- Provide a safe diagnostic path based on bounded telemetry and reproducible tests rather than raw protocol or secret-bearing captures.

**Non-Goals:**
- Edit or duplicate block 70's self-hoster mode guidance, edit block 72, or add public docs.
- Add a new maintainer site, publish `docs/maintainer/` in MkDocs, or redesign documentation tooling.
- Change runtime behavior, protocol contracts, telemetry, tests, Docker harnesses, or source constants.
- Invent concrete symbol/path names before the prerequisite implementation lands.

## Decisions

### Use one dedicated repository-only guide

Create `docs/maintainer/WORKER_ARCHITECTURE_PROTOCOL.md` and add one link from the existing maintainer discovery surface chosen from the finalized repository layout. Keep it outside `mkdocs.yml` public navigation. A single guide keeps composition, transport, finality, locking, cache, and dependency rules together where maintainers need to reason across them; splitting by subsystem would duplicate identity and finality rules.

Alternative considered: place the guide in `docs/website/architecture.md`. Rejected because private invocation/protocol detail is maintainer-only and block 70 owns public operations.

### Lead with matrices, then invariants and evidence

The guide will begin with a composition-root matrix and a protocol/job matrix. Follow with sections for private selection, stream state machine, exits/finality, cancellation, exclusion, cache publication, dependency allow/deny, telemetry, safe debugging, and evidence. Each section links to the exact landed source/test or Docker artifact producer; planning block links are not substitutes for applied evidence.

Alternative considered: prose-only architecture narrative. Rejected because it makes role/job/version differences and forbidden dependencies difficult to audit.

### Treat private selection as sensitive implementation detail

State the exact selectors so maintainers can verify launcher behavior, but do not provide copy/paste invocation examples. The guide says the controller owns argument/environment construction and redaction, the sole exact private `--internal-worker` argument remains required for both versions, v1 requires only that `IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION` be absent, and v2 requires that protocol-version environment selector to have exact value `2`. Public operation always links to block 70's finalized route.

Alternative considered: omit private selectors entirely. Rejected because maintainers could not audit recursion prevention or v1/v2 selection drift.

### Separate wire terminal, process evidence, and controller finality

Document the three evidence layers explicitly. Wire terminal is authoritative once accepted; managed exit and raw death describe process completion; the parent may synthesize controller finality only when no valid terminal committed. The exit table calls out the valid Failed-plus-3 ProcessAssets busy case and preserves the defined precedence.

Alternative considered: organize troubleshooting by exit code alone. Rejected because it would misclassify committed terminals and platform-raw deaths.

### Explain exclusion mechanisms by scope and lifetime

Put process-local arbitration and the ProcessAssets PostgreSQL advisory lock in one comparison table. Include lock key/derivation/session lifetime, local owner-handle lifetime, release points, and the multi-container/direct-writer caveat. Cache mutation atomicity is a separate data-publication invariant, not a distributed lock claim.

Alternative considered: describe all three as generic locking. Rejected because their scopes and guarantees differ materially.

### Make debugging evidence-led and redaction-first

Start with canonical identity, kind/origin, stable EventIds, terminal/process classification, then point to the narrowest reproducible test or Docker evidence. Permit only bounded/redacted diagnostic outputs already owned by blocks 66–69. Explicitly prohibit raw stream/tail capture, protocol edits/replay, direct private invocation, and secret-bearing context.

Alternative considered: include manual NDJSON and worker CLI recipes. Rejected because they bypass the controller, risk corrupting framing/finality, and expose private contracts.

### Verify semantic tokens, not just Markdown

At apply time, bind a drift check to landed constants/tests for protocol identifier/version behavior, private selector names, job kinds, frame size, exit codes/precedence, lock key/derivation, 10-second grace, EventIds, and dependency allow/deny categories. Also run repository-link checks and `npm run docs:build`; explicitly assert that maintainer docs remain outside public nav. If a check cannot be automated in the existing docs/test tooling, record a deterministic command and expected source locations rather than adding a broad new framework.

Alternative considered: rely on editorial review. Rejected because these closed tokens are easy to stale and operationally significant.

## Risks / Trade-offs

- [Block 70 lands a different page name] → Resolve its finalized public route at apply time and add only a cross-link; never edit or duplicate that page.
- [Landed symbol or evidence paths differ from planning names] → Re-inventory finalized source/tests and use only verified paths; stop on semantic disagreement.
- [Private details become accidental operator API] → Avoid invocation recipes, mark selectors controller-owned/private, and route public use to block 70.
- [Debug guidance leaks sensitive data] → Restrict capture to bounded closed telemetry and redacted artifacts; list forbidden fields and streams explicitly.
- [Maintainer guide is not discoverable through the public site] → This is intentional; add repository-level maintainer discovery while preserving public-nav isolation.
- [Token checks become brittle to harmless refactors] → Check semantic constants and closed vocabularies through the narrowest stable source/test evidence, not broad source snapshots.

## Migration Plan

1. After blocks 15–56 and 65–69 are applied, inventory exact roots, symbols, constants, tests, scripts, workflows, and evidence directories; resolve block 70's finalized public route.
2. Write the guide and maintainer discovery link only, replacing planning placeholders with landed names.
3. Run source-token drift, repository-link, public-nav isolation, and `npm run docs:build` checks; run strict OpenSpec validation/status.
4. On rollback, remove the guide and its maintainer discovery link. No runtime, protocol, configuration, or data migration is involved.
