## Context

See proposal.md. Blocks 40–45 define the observable modes, block 46 owns `scripts/docker-mode-smoke.sh` and `npm run test:docker-smoke`, and block 67 supplies the safe process lifecycle/terminal telemetry used as evidence. Block 46 also initially replaces the existing Docker-build-only CI step; block 69 therefore promotes that single invocation into explicit job orchestration rather than creating another harness. Block 68 is parallel-owned and untouched.

The existing `CI` workflow runs one unbounded `app` job on unfiltered pull requests and pushes to `master`. It has no explicit permissions, concurrency, timeout, cache, service, dependency, or artifact policy.

## Goals / Non-Goals

**Goals:**
- Make the canonical block-46 matrix a required, bounded, isolated Linux job.
- Build the neutral production Dockerfile once and prove every case used its immutable image ID.
- Produce deterministic Standard, Web-only, Run-once, and invalid-mode evidence with disposable PostgreSQL and local fixtures.
- Preserve enough redacted evidence to diagnose failures while always cleaning run-owned resources.

**Non-Goals:**
- Create or fork a smoke script, npm alias, test-only image, entrypoint, or mode implementation.
- Exercise live geodata downloads, QEMU/multi-platform publishing, or restructure `docker-publish.yml`.
- Add repeated-worker, memory, cgroup, or RSS soak behavior; that remains block 68.
- Add a scheduled trigger to required PR CI or use repository/GitHub secrets.

## Decisions

### 1. Promote one canonical invocation into a dedicated job

Keep `app` as the normal restore/build/test job. Replace/relocate its Docker build or smoke step with one job id `docker-mode-integration`, display name `Docker Mode Integration`, `needs: app`, and `runs-on: ubuntu-latest`. The job invokes exactly:

`npm run test:docker-smoke`

The block-46 harness owns the one `docker build -f src/ImmichReverseGeo.Web/Dockerfile ... .`, resolves the image ID, and addresses all containers by that ID. There is no separate workflow build step and no matrix fan-out, so one invocation means one build and one fixture lifecycle.

Alternative: build with Buildx and pass a tag to the harness. Rejected for the required path because block 46 has not fixed the override name and caller/script double-build ambiguity would weaken the gate. A prebuilt override can be added only by reconciling block 46 first and proving the script skips its own build.

### 2. Preserve current triggers and make execution policy explicit

The workflow remains triggered by unfiltered `pull_request` and `push` to `master`; no `schedule` is added. Declare `permissions: contents: read`. Use one workflow concurrency group:

`ci-${{ github.workflow }}-${{ github.event.pull_request.number || github.ref }}`

with `cancel-in-progress: ${{ github.event_name == 'pull_request' }}`, so superseded PR runs stop but main runs are retained. Set `timeout-minutes: 30` on the Docker job. Keep named harness deadlines at 60 seconds for PostgreSQL readiness, 90 seconds per Web readiness/observation, 120 seconds for Standard child or Run-once terminal completion, and 30 seconds for cleanup; these are failure bounds, never success sleeps.

Use no cross-run Docker, npm, or NuGet cache in this Docker job. A clean Dockerfile build is the artifact under test, and the job does not restore host dependencies. Alternative: GitHub Actions BuildKit cache. Rejected until block 46 owns a precise prebuilt-image interface; correctness and one-build auditability take precedence over speed.

### 3. Use one run-scoped fixture environment

The harness generates a collision-safe prefix from GitHub run ID, attempt, job, and a random suffix; applies it as a Docker label; and derives unique container, network, database, schema, bind-root, and artifact names. Start a PostgreSQL image pinned by full digest on an internal Docker network with no host port. Use fixed local credentials generated for the run, not Actions secrets, and never print them.

Readiness is conjunctive: Docker health is healthy, `pg_isready` succeeds inside the database container, and a sentinel query confirms the versioned minimal schema/fixture transaction committed. The fixture creates only the landed minimum `asset` and `asset_exif` shape, one deterministic eligible row, and any no-work/control rows needed by block 46. The run-unique database and schema are passed through the normal `DB_*` contract.

Before app start, create distinct host directories for config and data, set ownership/mode for the image's declared non-root UID/GID, and mount them read-write at `/config` and `/data`. Inspect verifies distinct sources/destinations, RW state, effective UID/GID not zero, and successful writes. Required division/cache inputs are versioned local fixtures copied to the data root before egress denial. After images and fixtures exist, app/PostgreSQL communication stays on the internal network and app containers receive no external egress route. No Docker socket is mounted.

Alternative: GitHub Actions `services.postgres`. Rejected because explicit Docker lifecycle gives the harness unique networks, no published DB port, full inspect evidence, and label-scoped cleanup identical to local use.

### 4. Reuse the block-46 matrix and block-67 safe observations

The dedicated job does not reimplement mode logic. It requires the canonical harness to produce per-case observations under `_out/docker-mode-integration/<run>/`:

- **Standard:** omit `IMMICH_REVERSEGEO_MODE`; publish container port 8080 to a random loopback host port; require HTTP 200; use a due enabled fixture to observe exactly one scheduler-originated private child from the same assembly/image; correlate safe block-67 lifecycle/terminal observations; require the parent to return idle and remain HTTP healthy. Inspect/process evidence proves no second image/container performs work and no in-Web executor path is inferred.
- **Web-only:** set exact `web-only`; use the same due saved settings and fixture; require HTTP 200 during a bounded observation window and zero scheduler, detector, or child lifecycle observations. Manual UI processing is not automated here because block 46's smoke owns only stable container-facing behavior.
- **Run-once:** set exact `run-once`; publish no port; require exactly one attempt, one terminal classification, no private child, no retry, and exit 0 for the deterministic fixture. A listener probe and inspect must show no published port.
- **Invalid:** set a representative unsupported value containing a unique canary; publish no port; require exit 2, stable `invalid-deployment-mode`/accepted-values stderr, and absence of the canary, credentials, connection strings, and environment dumps.

Every case records the image ID before start and verifies it matches the single built ID. Container names and logs use neutral case labels, not secret-bearing inputs.

Alternative: separate CI jobs per mode. Rejected because each job would rebuild or transfer the image and multiply fixture/setup cost; the canonical harness already provides sequential isolation and diagnostics.

### 5. Failure evidence precedes idempotent cleanup

A strict-shell trap captures the primary case/status, then writes bounded stdout/stderr, Docker wait/exit result, redacted inspect JSON, health, network, mounts, and process snapshots. Redaction is allow-list based; never archive full environment arrays, labels containing values, connection strings, arguments containing canaries, or exception dumps. CI uses `actions/upload-artifact@v6` with `if: failure()`, a run-unique name, `if-no-files-found: error`, and a short retention period (7 days).

A final `if: always()` cleanup calls the harness cleanup path and defensively removes only resources bearing the run label, then removes run-owned bind/fixture roots. Cleanup is idempotent and its diagnostics are appended without replacing the original nonzero result; if all cases passed and cleanup alone fails, cleanup becomes the failure. No generic Docker prune is allowed.

## Risks / Trade-offs

- [Block 46 already wires CI] → Relocate its one invocation; search the workflow and fail review if any second Docker build/smoke remains.
- [Fixture drifts from landed Immich query shape] → Keep versioned minimal SQL beside the canonical harness and validate its sentinel before app start.
- [Scheduler/child completes too quickly to inspect] → Correlate bounded block-67 lifecycle/terminal observations and capture process snapshots opportunistically; do not add sleeps or production delay seams.
- [No-egress setup differs across Docker versions] → Test the capability explicitly, fail with network inspection, and never silently allow live downloads.
- [Logs contain secrets] → Use local canaries, allow-list diagnostics, scan artifacts before upload, and treat a redaction miss as test failure.
- [Clean builds increase duration] → Retain the 30-minute bound; add caching only after a separately specified block-46 prebuilt interface preserves one-build identity.

## Migration Plan

1. Confirm blocks 40–46 and 67 are applied and the canonical npm entry, fixture, observations, and cleanup interface have landed.
2. Move the single block-46 invocation out of `app` into `docker-mode-integration`; remove the old Docker-build-only or duplicate smoke step.
3. Add explicit workflow permissions, concurrency, job dependency, timeout, failure-artifact, and always-cleanup steps.
4. Run the exact npm entry on Linux, inspect the one-build/image-ID evidence and all four cases, then run strict OpenSpec validation/status.
5. Roll back by reverting only the job topology; the local block-46 harness remains available and no runtime state or public behavior migrates.
