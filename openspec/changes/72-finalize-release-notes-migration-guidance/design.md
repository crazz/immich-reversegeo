## Context

See proposal.md. The three release seams currently contain general release text but no worker-migration gate. Finalized blocks 40–56 define strict modes and worker-backed Web behavior; 61–64 preserve full eligibility checks and existing scheduling while rejecting a watermark, reconciliation cadence, and NAS-specific controls; 65–71 bound progress, telemetry, failure, soak, Docker, public-mode, and maintainer-protocol evidence. Apply must re-read landed source, tests, docs, and CI rather than treating planning text as proof.

## Goals / Non-Goals

**Goals:**
- Build one traceable evidence matrix and use it to keep technical notes, public notes, and the maintainer checklist semantically synchronized.
- Give self-hosters exact, audience-appropriate upgrade, mode, operations, license, and rollback guidance.
- Make unsupported or unfinished claims release-blocking rather than soft caveats.

**Non-Goals:**
- Change runtime, image, schema, configuration, tests, CI, or public/maintainer guides owned by earlier blocks.
- Promise zero downtime, lower total/peak memory, universal RSS limits, automatic retries, schema/config-data migration, or compatibility beyond an actually tested image/volume combination.
- Publish, recommend, or expose private worker selectors or protocol controls.
- Revive the rejected block 62 watermark, block 63 reconciliation cadence, or block 64 NAS-specific controls.

## Decisions

### 1. Use a release evidence matrix as the sole claim gate

Before editing release copy, record a row for each claim with: exact wording boundary, owning block, landed source/test/doc link, command or CI run, result, tested image digest/tag, tested previous-image identity where relevant, and reviewer/date. A plan, unchecked task, missing link, stale result, or contradictory source is not evidence. Every enumerated mandatory row must pass; removing required mode, migration, rollback, license, or compatibility guidance does not unblock release. A truly optional ancillary claim may instead be removed from all three release seams and the matrix. This is preferred over drafting aspirational notes and correcting them after publication.

Required rows cover:
- blocks 40–46: absent-only Standard default; exact lowercase `standard`, `web-only`, `run-once`; strict exit-2 rejection; startup-only selection; same neutral image/entrypoint; ports and separate `/config`, `/data` mounts;
- blocks 47–56: Standard/Web-only UI, manual processing, Lookup and supported heavy Data actions through temporary same-image workers; arbitration/cancellation/finality; no heavy Web execution; Run-once's direct same-process boundary;
- blocks 61–64: retained full current-eligibility `EXISTS` checks and existing schedule presets only, with explicit proof that no release statement depends on rejected watermark/reconciliation/NAS controls;
- blocks 65–68 and 71: bounded protocol/progress/failure/finality evidence, process cleanup, and the selected memory soak, including all measurement limitations;
- block 69: required `Docker Mode Integration` CI evidence from `npm run test:docker-smoke`, one build and one immutable production image ID across all mode cases, distinct writable mounts, safe diagnostics, and cleanup;
- block 70: final public deployment guide, exact operator commands/behavior, links, and successful `npm run docs:build`;
- upgrade/rollback: upgraded-image start using preexisting volumes and a stopped-work rollback to the named previous released image with the same tested volumes.

### 2. Synchronize meaning, not prose

Update `CHANGELOG.md` and `docs/website/changelog.md` in one task and cross-link them. The technical changelog records architecture, verified contracts, test/CI evidence, and compatibility boundaries. The public changelog leads with user impact, supported workflow, limits, and links to the block-70 deployment guide and GADM license guidance. The maintainer checklist links every evidence row/test/doc and prevents publication while a row is absent, failed, stale, or contradictory. Exact mode/default, compatibility, retry, and rollback meaning must match in all three; wording may differ by audience.

Keep entries under `Unreleased`. If release tooling needs placeholders, use explicit version/date placeholders only until the actual tag/version and release date are known; never invent or predate either value.

### 3. State the operational contract exactly

Release guidance may state, only after evidence passes:
- absence of `IMMICH_REVERSEGEO_MODE` selects Standard; accepted values are exactly lowercase `standard`, `web-only`, and `run-once`; empty, whitespace, padded, case-varied, and unknown values fail before startup with exit 2; the setting is startup-only, requires restart, and is not persisted;
- one upgraded neutral image and unchanged entrypoint serve all public modes; Standard/Web-only launch private same-image worker processes, but public copy never names, spells, demonstrates, or recommends any private selector;
- persistent `/config` and `/data` remain separate and mounted in every applicable service/job; upgrade introduces no Immich schema change and no migration of Immich schema data or persisted ReverseGeo configuration data;
- Web-only serves the UI and manual processing and retains saved schedule values while running no internal scheduler; after blocks 47–56, Lookup and supported heavy Data operations use workers. It adds no public automation endpoint;
- Run-once has no listener, child worker, detector precheck, internal retry, or automatic restart; one invocation is one direct same-process attempt. Its automation contract is exit 0 completed/no-work, 2 invalid public invocation/mode, 3 advisory-lock busy, 4 domain failure, 5 startup/infrastructure/required dependency or cleanup failure, 130 orderly cancellation; abrupt platform termination may use another raw status. Operators own any retry and must account for retained committed effects;
- optional GADM remains subject to academic/other non-commercial-use restrictions, with the official license/public data-source link.

### 4. Bound temporary-process and memory claims

For Standard/Web-only, say verified heavy geodata jobs run in disposable workers and that the tested lifecycle reached terminal/process/stream cleanup with no surviving child or accumulated owned temporary artifacts. Do not generalize that statement to Run-once, which executes directly in its one-shot process. Do not claim lower total memory, a universal RAM requirement, absolute/OS/container/cgroup/process-tree peak, guaranteed RSS drop, or a numeric threshold unless the exact compatible selected profile produced that evidence. The 1-second child samples and optional profiles retain block-68 caveats.

### 5. Make rollback a tested stop-and-restart operation

The checklist identifies both the upgraded image digest/tag and a real previous released image digest/tag. Test upgrade using preexisting `/config` and `/data`, verify settings/data usability and no Immich schema/config-data migration, stop admissions and active work, stop the upgraded container/job, then start the previous image with the same tested volumes and verify its documented startup and representative behavior. Guidance must warn that newer settings fields, cache formats, partial committed Immich writes, or other forward-created data are compatible only to the extent this exact matrix proves; operators should preserve backups/snapshots and remove unsupported mode configuration when returning to an image that predates it. Never describe concurrent old/new containers, live image swapping, automatic reversal of committed writes, or zero-downtime rollback.

## Risks / Trade-offs

- [A planning artifact is mistaken for release proof] → Require landed links, commands/results, image identities, and reviewer/date in the checklist.
- [Three release seams drift] → Edit and review them together against one matrix; block release on semantic mismatch.
- [Memory isolation becomes a performance promise] → Preserve process/sampler/profile boundaries and remove unsupported numeric or peak language.
- [Rollback corrupts or surprises an installation] → Test one explicit previous/upgraded image and volume matrix, stop active work, retain backups, and publish caveats rather than universal compatibility.
- [Private or rejected controls leak into public guidance] → Add negative review checks for private selectors and blocks 62–64 claims.

## Migration Plan

1. Re-read landed blocks 1–71, source/tests, final block-70/71 docs, the three release seams, and release workflows; create the evidence matrix and mark every unmet row as a release blocker.
2. Run/retain focused mode, Web dependency-boundary, protocol/failure/finality, selected soak, docs-link/build, and block-69 Docker CI evidence using the exact release-candidate image identity.
3. Exercise upgrade and stopped-work rollback with the exact upgraded and previous image identities plus preexisting separate `/config` and `/data` volumes; record compatibility caveats and retained effects.
4. Update both changelogs together under `Unreleased` (or explicit temporary version/date placeholders) and update the maintainer checklist with links, results, image identities, blockers, and final semantic review.
5. Replace placeholders only when the actual version/tag and date are known; rerun docs and final image checks against the release candidate. Publish only with no unresolved blocker.

Rollback of the release procedure is to remove or defer unsupported notes and leave the release unpublished; rollback guidance for operators is the tested stopped-work previous-image procedure above.
