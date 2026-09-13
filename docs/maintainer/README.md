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
