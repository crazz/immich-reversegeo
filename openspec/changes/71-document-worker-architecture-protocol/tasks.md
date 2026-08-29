## 1. Inventory finalized evidence

- [ ] 1.1 Re-read applied source and tests for blocks 15–32, 40–56, and 65–69; record exact composition roots, protocol constants/types, job handlers, finality paths, lock/arbitration/cache lifetimes, dependency sentinels, telemetry IDs, and evidence paths without changing those blocks.
- [ ] 1.2 Inventory `docs/maintainer/`, the repository's maintainer discovery surface, `mkdocs.yml`, docs scripts, and block 70's finalized public deployment-guide route; confirm block 70 and block 72 remain out of scope.
- [ ] 1.3 Build an evidence map from every planned guide section to landed source plus focused tests or Docker/soak producers, including PostgreSQL lock integration, process failure matrix, `scripts/docker-mode-smoke.sh`, CI Docker integration, `_out/docker-mode-smoke/`, `_out/docker-mode-integration/`, and `_out/performance/worker-memory-soak/` where those finalized paths exist.

## 2. Write the maintainer reference

- [ ] 2.1 Add `docs/maintainer/WORKER_ARCHITECTURE_PROTOCOL.md` with composition-root and protocol/job matrices covering Standard, Web-only, InternalWorker, and direct Run-once; public modes; sole private `--internal-worker`; protocol `immich-reversegeo.worker`; v1 selector absence; exact private v2 environment selector; v1 ProcessAssets; v2 closed job kinds; one canonical identity; and Web dependency allow/deny categories.
- [ ] 2.2 Document stdin/stdout/stderr ownership and the finalized NDJSON contract: encoding/framing/size, ready, independent sequences, correlation, accepted ordering, queue/flush, EOF, terminal-last/post-terminal, bounded validation, managed exits with exact precedence `6 > 5 > 2 > 3 > 4 > 130 > 0`, raw death, valid Failed-plus-exit-3 ProcessAssets lock contention, and committed-terminal authority.
- [ ] 2.3 Document parent finality and cancellation (single handle, cancel eligibility, shared stop, fixed 10-second grace, whole-tree escalation, drains/disposal/release, no retry), the exact ProcessAssets advisory-lock key/derivation/session lifetime, process-local arbitration and multi-container/direct-writer caveat, and atomic cache candidate validation/publication/cleanup.
- [ ] 2.4 Add the telemetry/evidence-led debugging runbook using canonical identity, job kind/origin, EventIds 5901, 6601–6605, 6610–6612, 6620–6623, 6630, 6640–6641, and 6650; require bounded redaction and prohibit private-selector public use, protocol hand-edit/replay, and raw streams/tails, arguments, environment/configuration, payloads, coordinates, paths, SQL, credentials, connection strings, tokens, exception text, or stacks.
- [ ] 2.5 Add source/test/Docker evidence links and one cross-link to block 70's finalized public guide for supported operation; do not duplicate public mode commands, troubleshooting, or claims.
- [ ] 2.6 Link the new guide from the existing maintainer discovery surface while keeping `docs/maintainer/` out of `mkdocs.yml` public navigation.

## 3. Verify documentation contracts

- [ ] 3.1 Run deterministic source-token drift checks for the protocol identifier/version selectors, private invocation token, job-kind vocabulary, 1,048,576-byte frame limit, managed exits/precedence, lock key/derivation, 10-second grace, telemetry IDs, and dependency allow/deny categories against finalized source/tests; resolve drift from landed evidence rather than editing runtime contracts.
- [ ] 3.2 Run repository Markdown link checks for every source, test, script/workflow, evidence-directory, maintainer-discovery, and block-70 target link; confirm no link points to a planning-only placeholder.
- [ ] 3.3 Run `npm run docs:build` and verify `mkdocs.yml` still uses `docs/website` with no maintainer page in public navigation.
- [ ] 3.4 Run `openspec validate 71-document-worker-architecture-protocol --strict` and final `openspec status --change 71-document-worker-architecture-protocol`; verify proposal, spec, design, and tasks are all complete.
- [ ] 3.5 Review the apply diff for only the new block-71 maintainer guide and its maintainer discovery link, with zero edits to blocks 70/72, public implementation docs, runtime source, tests, Docker assets, or protocol artifacts.
