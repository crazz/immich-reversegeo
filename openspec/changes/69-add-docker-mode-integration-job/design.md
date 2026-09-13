## Context

See proposal.md. Blocks 40–45 define the observable modes, block 46 owns `scripts/docker-mode-smoke.sh` and `npm run test:docker-smoke`, and block 67 supplies the safe process lifecycle/terminal telemetry used as evidence. Block 46 also initially replaces the existing Docker-build-only CI step; block 69 therefore promotes that single invocation into explicit job orchestration rather than creating another harness. Block 68 is parallel-owned and untouched.

The landed `CI` workflow already has read-only contents permission and seven jobs, including the canonical Docker smoke invocation inside `app`; triggers include unfiltered pull requests and pushes to `master` and `major-redesign`. It has no workflow concurrency or dedicated Docker artifact/cleanup job policy.

Preflight found substantive gaps between this original plan and the actual block-46 harness: serving cases are dual-homed without enforced egress denial; PostgreSQL readiness has pg_isready/SELECT 1 but no container health plus committed fixture sentinel; each run has a unique database but uses public schema; evidence uses secret replacement rather than an allow-list and explicit size bounds; cleanup exists only as an EXIT trap. This change may harden those boundaries in the existing script and SQL fixture. Blocks 46/67 archived planning and production behavior stay unchanged. This is a narrow prerequisite repair within the same canonical harness, not a fork or a second mode matrix.

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

The landed optional `--image` / `DOCKER_SMOKE_IMAGE` interface already resolves a prebuilt identity and skips the build. Required CI must leave it unset and call the canonical entry once. It may be used only for separate local failure-path validation; those runs are recorded separately from the required one-build success run.

### 2. Preserve current triggers and make execution policy explicit

The workflow preserves unfiltered `pull_request` and `push` to both `master` and `major-redesign`; no `schedule` is added. Declare `permissions: contents: read`. Use one workflow concurrency group:

`ci-${{ github.workflow }}-${{ github.event.pull_request.number || github.ref }}`

with `cancel-in-progress: ${{ github.event_name == 'pull_request' }}`, so superseded PR runs stop but main runs are retained. Set `timeout-minutes: 30` on the Docker job. Retain the landed named failure bounds: 60 seconds for database readiness, 45 seconds for HTTP readiness, 105 seconds for each Standard scheduled phase, 130 seconds to observe two Web-only due opportunities, 30 seconds for the Run-once held count and 45 seconds for its exit, and the existing derived private ready/EOF budgets. Bound the new external cleanup entry by 30 seconds; preserve the primary result when cleanup fails. Polling intervals are not proof of success and no fixed sleep substitutes for a readiness predicate.

Use no cross-run Docker, npm, or NuGet cache in this Docker job. A clean Dockerfile build is the artifact under test, and the job does not restore host dependencies. Alternative: GitHub Actions BuildKit cache. Rejected until block 46 owns a precise prebuilt-image interface; correctness and one-build auditability take precedence over speed.

### 3. Use one run-scoped fixture environment

Retain the harness's collision-safe UTC timestamp/PID/random run prefix and existing Docker label; derive unique container, network, database, schema, bind-root and artifact identities from it. The CI artifact name additionally includes GitHub run ID, attempt and job. Start a PostgreSQL image pinned by full digest on an internal Docker network with no host port. Use fixed local credentials generated for the run, not Actions secrets, and never print them.

Readiness is conjunctive: Docker health is healthy, `pg_isready` succeeds inside the database container, and a sentinel query confirms the versioned minimal schema/fixture transaction committed. The fixture creates only the landed minimum `asset` and `asset_exif` shape, one deterministic eligible row, and any no-work/control rows needed by block 46. The run-unique database and schema are passed through the normal `DB_*` contract.

Before app start, create distinct host directories for config and data, set ownership/mode for the image's declared non-root UID/GID, and mount them read-write at `/config` and `/data`. Inspect verifies distinct sources/destinations, RW state, effective UID/GID not zero, and successful writes. Required division/cache inputs are versioned local fixtures copied to the data root before egress denial. Keep PostgreSQL only on the internal network. Serving containers retain their separate Web publication bridge so loopback HTTP works; before any app starts, install and verify run-owned network controls that deny external egress from both app networks while permitting fixture PostgreSQL and HTTP response traffic. Remove only those owned controls during cleanup. Fail closed when the Linux Docker/firewall capability is unavailable. No Docker socket is mounted.

Alternative: GitHub Actions `services.postgres`. Rejected because explicit Docker lifecycle gives the harness unique networks, no published DB port, full inspect evidence, and label-scoped cleanup identical to local use.

### 4. Reuse the block-46 matrix and block-67 safe observations

The dedicated job does not reimplement mode logic. It requires the canonical harness to produce per-case observations under the landed `_out/docker-mode-smoke/<run>/evidence/`:

- **Standard:** preserve both existing scheduled phases in one absent-mode serving container. S1 processes the deterministic eligible no-country asset, persists its skip, reaps the same-image child and leaves the parent healthy. S2 holds the real admitted worker at the existing PostgreSQL gate, proves its same-image process identity, and proves parent-stop child reaping. Exactly two admissions/production lock acquisitions are expected; do not discard the stronger second phase to satisfy the stale one-pass wording.
- **Web-only:** set exact `web-only`; use the same due saved settings and fixture; require HTTP 200 during a bounded observation window and zero scheduler, detector, or child lifecycle observations. Manual UI processing is not automated here because block 46's smoke owns only stable container-facing behavior.
- **Run-once:** preserve the existing real empty-count gate, one no-work attempt, exact bounded completion output, no private child/retry, no HTTP listener or published port, and exit 0. S1 provides positive asset execution coverage; Run-once provides the independently observable no-work/serving boundary.
- **Invalid:** set a representative unsupported value containing a unique canary; publish no port; require exit 2, stable `invalid-deployment-mode`/accepted-values stderr, and absence of the canary, credentials, connection strings, and environment dumps.

Preserve the fifth **private protocol** ready/controlled-EOF probe, including its real skipped-store initialization, no-listener check, same packaged command, and canonical EOF outcome. Every case records the image ID before start and verifies it matches the single built ID. Container names and logs use neutral case labels, not secret-bearing inputs.

Alternative: separate CI jobs per mode. Rejected because each job would rebuild or transfer the image and multiply fixture/setup cost; the canonical harness already provides sequential isolation and diagnostics.

### 5. Failure evidence precedes idempotent cleanup

A strict-shell trap captures the primary case/status, then writes bounded stdout/stderr, Docker wait/exit result, redacted inspect JSON, health, network, mounts, and process snapshots. Redaction is allow-list based; never archive full environment arrays, labels containing values, connection strings, arguments containing canaries, or exception dumps. CI uses `actions/upload-artifact@v6` with `if: failure()`, a run-unique name, `if-no-files-found: error`, and a short retention period (7 days).

Expose a cleanup-only mode on the same script that accepts validated run identity from its owned output tree, never starts/builds an app, and removes only matching labeled resources, owned firewall rules, and the run-owned work subtree. A final `if: always()` step invokes that idempotent mode after the normal EXIT trap; both paths preserve redacted evidence and remove fixture database/volume/bind state. Do not use a broad script source/eval or a second cleanup helper. Cleanup is idempotent and its diagnostics are appended without replacing the original nonzero result; if all cases passed and cleanup alone fails, cleanup becomes the failure. No generic Docker prune is allowed.

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
4. Run the exact npm entry on Linux, inspect the one-build/image-ID evidence and all five roles/two Standard phases; exercise the existing forced-failure/redaction controls and new idempotent cleanup entry separately, then run strict OpenSpec validation/status.
5. Roll back by reverting only the job topology; the local block-46 harness remains available and no runtime state or public behavior migrates.
