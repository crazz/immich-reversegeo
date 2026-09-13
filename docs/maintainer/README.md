# Maintainer documentation

Start with the [architecture overview](ARCHITECTURE.md) for the runtime model, project map and paths to the main flows. Use [CONTRIBUTING](../../CONTRIBUTING.md) for local setup and development commands.

| Task | Read |
|---|---|
| Understand ownership and navigate the source | [Architecture](ARCHITECTURE.md) |
| Change worker requests, cancellation, finality or diagnostics | [Worker architecture and protocol](WORKER_ARCHITECTURE_PROTOCOL.md) |
| Keep heavy dependencies out of the Web host | [Web control-plane boundary](web-control-plane-boundary.md) |
| Change progress delivery or backpressure | [Worker event delivery](worker-event-delivery.md) |
| Measure repeated-worker resource ownership | [Worker memory soak](worker-memory-soak.md) |
| Evaluate a release and its migration evidence | [Release checklist](RELEASE_CHECKLIST.md) |
| Investigate scheduled database checks | [Scheduled existence evidence](scheduled-existence-evidence.md) and [PostgreSQL query plans](postgresql-detector-query-plans.md) |
| Understand the rejected incremental-detection approach | [Immich watermark research](immich-watermark-source-research.md) |
| Work on Overture division data | [Overture divisions](OVERTURE_DIVISIONS.md) |

The release checklist and research documents contain evidence from named revisions and fixtures. Their results are not automatically evidence for a later image or environment.

Operator instructions live on the website: [first setup](../website/getting-started.md), [deployment modes](../website/deployment-modes.md), [daily use](../website/using-the-app.md), [upgrading and rollback](../website/upgrading.md), and [troubleshooting](../website/troubleshooting.md). Keep private worker controls and test-harness details in this maintainer directory.

## Publish the documentation website

The website is configured for [crazz.github.io/immich-reversegeo](https://crazz.github.io/immich-reversegeo/). Before the first deployment, enable GitHub Pages in `crazz/immich-reversegeo` under **Settings → Pages → Build and deployment → Source → GitHub Actions**. Preparing the workflow does not enable or publish the site.

The [Deploy Docs workflow](../../.github/workflows/pages.yml) builds Zensical into `_out/website`, uploads a Pages artifact, and deploys it in the same repository. It runs on relevant changes pushed to `master` or through a manual workflow dispatch. It uses the workflow's GitHub token and the `github-pages` environment; no separate Pages repository or `PAGES_DEPLOY_TOKEN` is needed. See [GitHub's custom Pages workflow documentation](https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages) for repository permissions and environment settings.

Keep `site_url` in `mkdocs.yml` set to the full project URL, including `/immich-reversegeo/`, so canonical URLs and the sitemap point to the correct site. Check a nested page, its navigation, images, and search after deployment. The [Docs Preview workflow](../../.github/workflows/docs-preview.yml) builds pull-request artifacts without deploying them.

## Publish and verify a candidate image

Run [Publish Docker Image](../../.github/workflows/docker-publish.yml) manually for the branch you want to test, with **publish_latest left unchecked**. This publishes a commit-tagged image, pulls it back by the digest returned from the build, checks its source revision, and runs the existing Docker mode smoke tests against that exact image. The tests use an isolated PostgreSQL fixture and temporary storage on the native Linux runner; they do not contact your Immich installation.

Use a candidate only after the complete workflow succeeds. A failed smoke test leaves the candidate tag in the registry for diagnosis; publication alone is not a passing result. Copy the full `ghcr.io/crazz/immich-reversegeo@sha256:…` reference from the workflow summary. The `candidate-image-<run>-<attempt>` artifact retains its identity and smoke evidence for seven days. The smoke runner resolves one local image ID and reuses it without rebuilding, then removes only its own containers, networks and volumes.

This manual candidate path updates neither `edge` nor `latest`. Pushes to `master` keep their existing `edge`/SHA publishing behavior, and a manual run with `publish_latest` checked remains the separate release path. The publisher currently builds for the AMD64 runner; check the target host's architecture before installing a candidate.

For a NAS trial, use a separate container, host port and config/data storage. Start with `IMMICH_REVERSEGEO_MODE=web-only` and validate familiar coordinates through Lookup. Web-only disables scheduling but permits manual writes: use a test database for processing trials. Compare idle, active and post-worker memory over repeated equivalent jobs; the small CI fixture does not establish memory use for a real library. See [Getting Started](../website/getting-started.md) and [Upgrading and Rollback](../website/upgrading.md) before replacing an existing installation.
