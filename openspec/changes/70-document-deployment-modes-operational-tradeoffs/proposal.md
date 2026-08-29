## Why

The released deployment modes replace the old single-process operating model, but the public site still describes only one always-on Web container. Self-hosters need one Docker-first, evidence-bounded guide that lets them choose a mode, run it with the supported image and volumes, and recover safely without learning private worker internals.

## What Changes

- Add a navigated public deployment-modes guide and reconcile the existing installation, configuration, app-usage, architecture, data-source, and troubleshooting pages with the finalized blocks 40–56 and 61–69 contracts.
- Document the sole public selector `IMMICH_REVERSEGEO_MODE`: exact lowercase `standard`, `web-only`, and `run-once`; missing-only Standard default; strict invalid-value failure; startup-only/restart semantics; and exclusion from saved settings.
- Publish tested Docker/Compose examples using `ghcr.io/immich-reversegeo/immich-reversegeo:latest`, container port `8080` only for Web-hosted modes, and distinct persistent `/config` and `/data` mounts. Publish a no-port, no-restart Run-once service invoked with `docker compose run --rm`, without a command override or private selector.
- Explain that Standard supplies the Web UI and internal scheduler, Web-only supplies the same UI/manual and heavy worker-backed actions but no scheduler, and Run-once performs one direct same-process attempt with human-readable logs and no HTTP listener.
- Explain temporary worker startup, worker-backed heavy UI actions, bounded cancellation, crash finality, explicit retry ownership, Run-once exit meanings including advisory-lock Busy/3, and memory observations without universal numeric promises.
- Give NAS/HDD guidance through the existing schedule presets and the preserved full-eligibility `EXISTS` check; do not revive withdrawn watermark, reconciliation, or NAS-specific controls.
- Keep the optional GADM non-commercial license constraint visible anywhere operational guidance recommends enabling or downloading GADM data.
- Mark claims as production-image tested, contract-verified, or hardware/environment-dependent guidance so assumptions cannot be mistaken for guarantees.

## Capabilities

### New Capabilities
- `deployment-mode-operations-guide`: Public, Docker-first selection, invocation, operation, and recovery guidance for the released deployment modes.

### Modified Capabilities
- None.

## Impact

Planning targets `docs/website/deployment-modes.md` (new), `docs/website/getting-started.md`, `docs/website/installation.md`, `docs/website/configuration.md`, `docs/website/using-the-app.md`, `docs/website/architecture.md`, `docs/website/data-sources.md`, `docs/website/troubleshooting.md`, `mkdocs.yml`, and the public `docker-compose.yml` example. `README.md` remains product-first, release notes remain block 72, and private protocol/selector documentation remains block 71. There is no runtime, API, database, settings-schema, image, or dependency change.
