## 1. Reconcile released evidence and public surfaces

- [ ] 1.1 Re-read the applied blocks 40–46 and record the landed exact mode parser, missing-only Standard default, mode labels, Web/scheduler composition, worker status labels, Run-once host/log/exit behavior, and public entrypoint; stop instead of documenting a conflicting assumption.
- [ ] 1.2 Re-read the applied blocks 47–56 and inventory which Dashboard, Lookup, cache download/export/refresh, deletion, inventory, and database-maintenance paths are worker-heavy versus lightweight coordinated Web control; use the landed names only in internal review, not public protocol copy.
- [ ] 1.3 Re-read finalized blocks 61–64 and confirm every Standard scheduled check still uses the full current-eligibility `EXISTS` observation, existing schedule presets remain the only controls, and watermark/reconciliation/NAS-specific options remain absent.
- [ ] 1.4 Re-read block 68 memory evidence and the final block-69 canonical Docker harness/CI artifacts; record exactly which image, entrypoint, mode, port, mount, listener, attempt, exit, and cleanup claims are production-image tested versus contract-verified or environment-dependent.
- [ ] 1.5 Inventory current headings and cross-links in `getting-started.md`, `installation.md`, `configuration.md`, `using-the-app.md`, `architecture.md`, `data-sources.md`, and `troubleshooting.md`, plus `mkdocs.yml` and `docker-compose.yml`; identify and remove contradictory single-process copy only in block 70.

## 2. Add the canonical deployment-mode guide

- [ ] 2.1 Create `docs/website/deployment-modes.md` with a decision-first Standard/Web-only/Run-once matrix covering the exact lowercase environment values, missing-only Standard default, strict invalid exit-2 behavior, startup-only/restart requirement, and exclusion from `settings.json` and Web settings.
- [ ] 2.2 Label claims as **Production-image tested**, **Contract-verified**, or **Environment-dependent guidance** and avoid generalizing the deterministic Docker fixture or memory soak into full-library, absolute-peak, cgroup, process-tree, RSS, timing, or capacity guarantees.
- [ ] 2.3 Explain Standard Web+scheduler behavior, Web-only's same UI/manual and heavy worker-backed actions with no scheduler and unchanged saved schedule, and Run-once's no-listener direct same-process single attempt with human-readable logs and stable process exit.
- [ ] 2.4 Explain disposable-worker startup/cache latency, the heavy-memory ownership and process-exit reclamation boundary, the Idle/Starting/Running/Cancelling/Failed states, bounded cooperative/forced cancellation, retained committed effects, crash finality, cleanup-before-retry, and no automatic replacement/replay/retry.
- [ ] 2.5 Publish the managed Run-once exit table: 0 completed/no work, 2 invalid invocation/mode, 3 advisory-lock Busy/no pass, 4 domain failure, 5 startup/config/dependency/data/database/lock/lifecycle/cleanup infrastructure failure, 130 orderly cancellation, and platform-specific abrupt termination; state that automation uses exits rather than parsing human logs.
- [ ] 2.6 Add NAS/HDD guidance using only enabled/disabled, hourly, every-few-minutes, every-few-hours, daily, weekly, custom cron, Web-only, and external Run-once choices; explain full `EXISTS` on every Standard check and recommend measuring around backups/scrubs/media scans without inventing withdrawn controls.
- [ ] 2.7 When mentioning optional GADM, repeat its academic/other non-commercial-use restriction, link the authoritative data-source section, and recommend Lookup before bulk processing.

## 3. Publish exact Docker and Compose examples

- [ ] 3.1 Update the Standard reference to use exactly `ghcr.io/immich-reversegeo/immich-reversegeo:latest`, omit `IMMICH_REVERSEGEO_MODE`, map a local/trusted host address to container port `8080`, preserve the existing database environment/network contract, and mount distinct persistent volumes at `/config` and `/data`.
- [ ] 3.2 Add the Web-only variation with exact `IMMICH_REVERSEGEO_MODE: web-only`, the same image/entrypoint/port/database/network/config/data shape, and no claim that disabling the scheduler disables manual or heavy UI worker jobs.
- [ ] 3.3 Add a dedicated `immich-reversegeo-run-once` service with exact `IMMICH_REVERSEGEO_MODE: run-once`, the same database/network and persistent config/data mounts, no `ports`, `restart: "no"`, no `command` or entrypoint override, and document exactly `docker compose run --rm immich-reversegeo-run-once` for cron/external scheduling.
- [ ] 3.4 Compare all three examples byte-for-meaning against the final canonical block-69 harness evidence; if the supported public-compatible invocation differs, revise the planning artifacts before publication and never substitute a private selector.

## 4. Reconcile existing public pages and navigation

- [ ] 4.1 Add **Deployment Modes** / `deployment-modes.md` to the Setup navigation in `mkdocs.yml` and cross-link it from `getting-started.md` and `installation.md` without expanding README.
- [ ] 4.2 Update `configuration.md` to distinguish immutable environment mode from saved settings, preserve all existing schedule presets, and describe Standard scheduling versus Web-only suppression without a watermark, separate reconciliation cadence, or NAS mode.
- [ ] 4.3 Update `using-the-app.md` with worker-backed Dashboard/Lookup/heavy Data behavior, status meanings, cancellation/failure finality, and the concrete inspect-logs/correct-cause/verify-cleanup/explicit-retry workflow.
- [ ] 4.4 Update `architecture.md` only at the public plain-language level to describe long-lived Web control and disposable heavy workers; add no selector, protocol, stdin/stdout/stderr ownership, process identity, or maintainer debugging detail.
- [ ] 4.5 Preserve `data-sources.md` as the authoritative GADM license page and reconcile/link `troubleshooting.md` for invalid mode, wrong listener expectation, Busy/3, worker startup/crash/cancel, mount permissions, cache latency, and environment-specific memory investigation.
- [ ] 4.6 Search all public pages and `docker-compose.yml` for stale single-process, in-Web heavy-work, scheduler, mode-value, port, mount, retry, memory, and Run-once claims; correct only block-70-owned public guidance and leave release notes/block 72 and maintainer protocol/block 71 untouched.

## 5. Verify documentation and scope

- [ ] 5.1 Run `npm run docs:build` through Zensical and treat unresolved internal-link or missing-target warnings as failures; verify `_out/website/deployment-modes/index.html` and every touched generated route exist.
- [ ] 5.2 Check relative Markdown links across every touched page, the GADM license target, and navigation; verify all exact values, commands, image/port/mount/listener claims, and exit meanings against the recorded prerequisite evidence. No screenshot review is required.
- [ ] 5.3 Run `openspec validate 70-document-deployment-modes-operational-tradeoffs --strict` and `openspec status --change 70-document-deployment-modes-operational-tradeoffs`, confirm strict success and 4/4 complete artifacts, and review the diff/file manifest proving only MASTERPLAN block 70, its four existing artifacts, and later block-70 public documentation surfaces changed—never blocks 71/72 or implementation code.
