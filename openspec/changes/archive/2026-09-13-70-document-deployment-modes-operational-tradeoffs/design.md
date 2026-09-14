## Context

See proposal.md and `specs/deployment-mode-operations-guide/spec.md`. The Zensical site uses `docs/website/` through the existing `mkdocs.yml` compatibility path and emits directory routes beneath `_out/website/`. Current setup, configuration, app, architecture, and troubleshooting copy assumes one always-on Web process. Blocks 40–46 define strict modes and process outcomes; blocks 47–56 move heavy Web-initiated geodata work behind temporary jobs and leave lightweight control/inventory work in Web; blocks 61–64 preserve one full-eligibility scheduled check and reject new NAS-specific controls; blocks 68–69 bound memory and production-image claims.

## Goals / Non-Goals

**Goals:**
- Provide one canonical mode decision page with exact, copyable Docker/Compose instructions.
- Reconcile every existing public page whose single-process wording would contradict the released compositions.
- Keep tested facts, contract facts, and hardware-dependent advice visibly distinct.
- Give concrete recovery guidance without exposing private worker mechanics.

**Non-Goals:**
- Change runtime behavior, configuration, images, ports, volumes, schedules, retries, or licenses.
- Document stdin/NDJSON, private selectors, protocol versions, process identities, or maintainer debugging; block 71 owns those topics.
- Add release notes or migration announcements; block 72 owns them.
- Add screenshots, a second docs generator, a committed link-check dependency, or numeric capacity promises.

## Decisions

### 1. Add one canonical page and keep existing pages task-focused

Create `docs/website/deployment-modes.md` and add **Deployment Modes** to the Setup section of `mkdocs.yml`, producing `/deployment-modes/`. The page owns the decision table, evidence labels, mode environment contract, Compose examples, Run-once exits, process/memory model, NAS advice, and recovery flow. Existing pages receive concise contextual updates and links rather than duplicating the whole guide:

- `getting-started.md`: choose a mode before starting; preserve backup, disable-Immich, and Lookup-first workflow.
- `installation.md`: exact image, Standard/Web-only/Run-once Compose forms, port 8080 rules, separate mounts, and supported Run-once command.
- `configuration.md`: startup-only mode versus saved settings; Standard/Web-only schedule policy; existing presets and full-check semantics.
- `using-the-app.md`: temporary workers for Dashboard processing, Lookup, and heavy Data work; five status labels; cancellation and explicit retry expectations.
- `architecture.md`: plain-language Web control-plane/disposable-worker model without protocol internals.
- `data-sources.md`: retain the authoritative GADM license wording and link it from operational advice.
- `troubleshooting.md`: invalid mode/exit 2, unexpected listener/no listener, worker startup/crash/cancel, Busy/3, permissions, caches, and environment-specific memory checks.
- `docker-compose.yml`: keep Standard as the omission-default reference and add/cross-reference optional exact Web-only and Run-once service forms.

Alternative: distribute mode details only across existing pages. Rejected because commands, tradeoffs, and recovery meanings would drift. Alternative: put all details in README. Rejected by the product-first README convention.

### 2. Use one exact public Compose contract

The examples use `ghcr.io/immich-reversegeo/immich-reversegeo:latest` and never override the image entrypoint. Standard keeps service name `immich-reversegeo`, omits `IMMICH_REVERSEGEO_MODE`, maps a local/trusted host address to container `8080`, mounts independent named volumes at `/config` and `/data`, and uses the existing Immich database environment/network contract. Web-only is the same service shape plus exact `IMMICH_REVERSEGEO_MODE: web-only`.

Run-once uses a dedicated service name `immich-reversegeo-run-once`, exact `IMMICH_REVERSEGEO_MODE: run-once`, the same database environment/network and persistent config/data volumes, no `ports`, and `restart: "no"`. The supported ephemeral command is exactly:

`docker compose run --rm immich-reversegeo-run-once`

There is no trailing argument, `command`, entrypoint override, or private selector. This matches block 69's tested properties: neutral image/entrypoint, exact environment selection, separate writable mounts, no published port/listener, one attempt, no child/retry, and stable exit. Apply must reconcile the final landed block-69 harness before publishing; if its public-compatible invocation differs, stop and revise this plan rather than improvise.

Alternative: `docker compose exec` into Standard. Rejected because it would not select the one-shot root. Alternative: advertise the internal worker argument. Rejected because it is private and protocol-driven.

### 3. Present behavior as a user decision matrix

The canonical table has rows Standard, Web-only, and Run-once and columns public value/default, Web UI/port, internal scheduler, manual/heavy UI actions, execution process, and intended operator. It states:

- Standard: Web and internal schedule; eligible scheduled and manual/heavy UI work use disposable workers.
- Web-only: same UI and heavy worker-backed actions, but structurally no scheduler/detector/waits; saved schedule remains unchanged.
- Run-once: no UI/listener/port; exactly one direct same-process authoritative pass; no detector precheck, child, retry, or second pass.

“Every heavy UI action uses a worker” means asset processing, Lookup geodata resolution, and cache download/export/refresh paths that require heavy geodata. It does not falsely classify lightweight inventory, coordinated deletion, or database-maintenance control work as geodata execution. This wording preserves blocks 47–56's final control-plane boundary.

### 4. Use an explicit evidence taxonomy

Use short admonitions/labels consistently:

- **Production-image tested:** block 69 evidence for missing-mode Standard HTTP/port 8080/scheduled child, exact Web-only HTTP with no scheduler activity, exact Run-once no published port/listener and exit 0 for a deterministic pass, invalid mode exit 2/redaction, unchanged entrypoint, non-root identity, and separate writable mounts.
- **Contract-verified:** strict accepted/rejected values, Web-only saved-schedule behavior, worker-backed heavy UI paths, cancellation/finality/no retry, and complete managed Run-once exit meanings.
- **Environment-dependent guidance:** startup duration, disk throughput, cache download duration/size, and memory usage on a particular NAS/container host.

Do not call an unmeasured recommendation “tested.” Do not generalize block 69's deterministic no-work/small fixture into full-library capacity evidence. Block 68's structural process cleanup supports the disposable ownership statement but explicitly supplies no universal RSS, slope, or peak threshold.

Alternative: label everything “verified.” Rejected because tests cover different boundaries and hardware advice is not portable.

### 5. Describe finality and retry from the operator's perspective

For Web modes, explain Idle, Starting, Running, Cancelling, and Failed; local schedule checks that find no work do not fabricate a worker. Cancel requests cooperative stop, then the owner may force-stop the process tree after its bounded grace. Committed asset/cache effects remain committed. Crashes, startup/protocol failures, missing terminal, and forced-stop failures are final for that request; cleanup and admission release complete before a later user action. No mode automatically replaces, replays, or retries a failed attempt.

Run-once documentation lists 0 completed/no work; 2 invalid syntax/mode; 3 global advisory-lock Busy; 4 domain processing failure; 5 startup/config/dependency/data/database/lock/lifecycle/cleanup infrastructure failure; and 130 orderly cancellation. Abrupt platform termination can remain platform-specific. Logs are for humans; automation branches on process exit. Busy/3 means another processing pass held the global exclusion and this attempt performed no processing; any orchestrator retry/backoff is explicitly operator-owned.

### 6. Preserve existing schedules and correctness semantics for NAS guidance

Recommend existing hourly, every-few-minutes, every-few-hours, daily, weekly, custom-cron, disabled/manual, Web-only, or external Run-once choices. Explain that each Standard scheduled occurrence first runs the full current-eligibility `EXISTS` observation; no persisted watermark, incremental tail, or separate reconciliation cadence exists. Recommend moving daily/weekly/custom work away from backups, scrubs, Immich maintenance, and heavy media scans and measuring the actual host. Do not invent a NAS mode, resource threshold, or withdrawn setting.

When GADM may improve a location, tell users to use Lookup first and link `data-sources.md`; repeat that GADM is limited to academic and other non-commercial use and is not a clean fit for commercial use.

### 7. Verify build, routes, links, and command provenance without screenshots

Run `npm run docs:build` using Zensical and the existing `mkdocs.yml`; treat build warnings about missing/unresolved internal targets as failures. Inspect relative Markdown links in every touched page and verify generated `_out/website/deployment-modes/index.html` plus the cross-linked generated pages exist. Compare every mode value, image/entrypoint/port/mount property, service command, listener statement, and exit claim to blocks 40–56 and the final canonical block-69 harness/evidence. No visual screenshot review is required.

## Risks / Trade-offs

- [Block 69 implementation differs from finalized planning] → Re-read the landed harness and CI evidence at apply start; stop and reconcile rather than publishing an assumed invocation.
- [A canonical page leaves stale contradictory copy elsewhere] → Inventory and update every listed public page, then search the site for single-process, scheduler, port, and in-process geodata claims.
- [Compose examples drift independently] → Keep Standard in `docker-compose.yml`, derive Web-only and Run-once from the same image/env/network/volume contract, and cross-link the canonical guide.
- [Users treat memory guidance as a guarantee] → Pair structural worker ownership with explicit sampler/fixture limits and no universal numeric threshold.
- [NAS advice revives rejected controls] → Name only landed presets/modes and explicitly state full `EXISTS` per scheduled check.
- [GADM advice hides license limits] → Require the non-commercial warning and authoritative data-source link at each operational recommendation.
- [Internal details leak into public docs] → Describe observable workers, states, logs, and exits only; leave selectors/protocol/stream ownership to block 71.

## Migration Plan

1. Reconcile the final applied blocks 40–56, 61–69, canonical Docker harness, public compose contract, and current docs inventory.
2. Add the canonical page/nav route and update installation/reference Compose first so every later cross-link has a stable target.
3. Reconcile configuration, using, architecture, data-source, troubleshooting, and getting-started copy without changing README or release notes.
4. Run docs build, internal-link/generated-route checks, command/evidence review, strict OpenSpec validation, and block-70-only scope review.
5. Roll back by reverting only block 70's public documentation/nav/reference-compose edits; no settings, data, database, or image migration is involved.
