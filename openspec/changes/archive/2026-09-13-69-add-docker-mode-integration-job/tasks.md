## 1. Reconcile prerequisites and single ownership

- [x] 1.1 Verify blocks 40–46 and 67 are applied; record the landed mode values, safe lifecycle/terminal observations, canonical `scripts/docker-mode-smoke.sh` interface, and exact `npm run test:docker-smoke` entry without changing their behavior.
- [x] 1.2 Search `.github/workflows/ci.yml` for every Docker build/smoke invocation and plan the relocation so the required workflow contains exactly one canonical smoke invocation and no separate Docker build.
- [x] 1.3 Confirm block 46's harness builds `src/ImmichReverseGeo.Web/Dockerfile` once, resolves the immutable image ID, records it, and rejects any case that does not use that identity.

## 2. Deterministic Linux fixture contract

- [x] 2.1 Preserve the existing full-digest image and strengthen the same script/SQL fixture so it pins the disposable PostgreSQL image by full digest and provides a run-unique internal network, container label/prefix, database, and schema with no published database port and fixed local non-secret credentials; close only the explicitly approved health/schema/egress/evidence/cleanup prerequisite gaps in the existing harness and SQL fixture.
- [x] 2.2 Preserve the versioned minimal `asset`/`asset_exif` SQL, positive Standard S1 fixture, held Standard S2 gate and empty Run-once case; add a committed fixture-version sentinel and unique schema without live geodata access.
- [x] 2.3 Add and verify conjunctive readiness in the canonical harness so it gates app startup on Docker health, in-container `pg_isready`, and the sentinel query within the 60-second database deadline by polling rather than fixed success sleeps.
- [x] 2.4 Verify the canonical harness creates distinct run-unique config and data bind roots, prepares them for the image's declared UID/GID, mounts them read-write at `/config` and `/data`, and asserts non-root execution plus successful independent writes.
- [x] 2.5 Add and verify run-owned Linux network controls so the canonical harness denies external egress after image/local-fixture preparation while preserving only app-to-PostgreSQL communication and fails with network evidence if any case attempts a download.

## 3. Canonical mode evidence

- [x] 3.1 Preserve Standard absent-mode/random-loopback HTTP readiness within 45 seconds and both existing 105-second scheduled phases: S1 positive no-country terminal/skip/child reap with healthy parent, then S2 held real worker identity and parent-stop child reaping; assert exactly two admissions and lock acquisitions.
- [x] 3.2 Verify the canonical Web-only case sets exact `web-only`, returns HTTP 200, and proves the same enabled/due settings produce zero scheduler, detector, or child lifecycle observations throughout the bounded window.
- [x] 3.3 Preserve Run-once exact mode, held real empty-count observation within 30 seconds, no listener/published port, one no-work attempt without child/retry, bounded terminal output and exit 0 within 45 seconds after release.
- [x] 3.4 Verify the canonical invalid-mode negative uses a unique canary, publishes no port, exits 2, emits bounded `invalid-deployment-mode` and accepted-values text, and reveals neither the canary nor credentials.
- [x] 3.5 Retain the existing fifth private ready/EOF/no-listener case and its derived deadlines. For every case, assert the unchanged entrypoint, declared non-root user, separate RW mounts, unique state, and the single recorded image ID; do not add a second mode matrix or harness.

## 4. Required CI orchestration

- [x] 4.1 Preserve unfiltered `pull_request`, pushes to both `master` and `major-redesign`, and existing `permissions: contents: read`, and add the ref-scoped `ci-${{ github.workflow }}-${{ github.event.pull_request.number || github.ref }}` concurrency group with cancellation only for pull requests.
- [x] 4.2 Add job id `docker-mode-integration`, name `Docker Mode Integration`, `needs: app`, `runs-on: ubuntu-latest`, and `timeout-minutes: 30`; invoke exactly `npm run test:docker-smoke` once and remove the prior Docker-build-only/duplicate smoke step.
- [x] 4.3 Keep the Docker job free of cross-run Docker/npm/NuGet caches and scheduled triggers; document that optional repeated-worker/cgroup/RSS soak remains block 68 and outside required PR CI.
- [x] 4.4 Verify named 60-second database, 45-second HTTP, 105-second Standard phase, 130-second Web-only two-due observation, 30/45-second Run-once and existing private ready/EOF bounds. Add a 30-second external cleanup bound and preserve predicate-driven success.

## 5. Failure evidence, cleanup, and verification

- [x] 5.1 Harden and verify the canonical harness writes bounded per-case stdout/stderr, wait/exit status, allow-list-redacted inspect, health, network, mount, and process snapshots under the existing `_out/docker-mode-smoke/<run>/evidence/`; retain only bounded allow-listed fields and safe diagnostics, scan them for credentials, connection strings, environment dumps, and canaries before CI upload.
- [x] 5.2 Upload failure evidence with `actions/upload-artifact@v6`, `if: failure()`, a run-unique artifact name, `if-no-files-found: error`, and 7-day retention.
- [x] 5.3 Expose and verify idempotent cleanup-only on the same canonical script; wire it through `if: always()` for only run-labeled containers/network/volumes, owned firewall controls and database/fixture/bind roots. Preserve evidence/primary failure and forbid broad Docker prune.
- [x] 5.4 Exercise success and forced-failure paths on Linux, prove one image build/ID, five existing role cases and both Standard phases, retained redacted evidence, and zero leaked resources; run `openspec validate 69-add-docker-mode-integration-job --strict` and final status.
- [x] 5.5 Review the diff for block-69-only scope: exactly four planning artifacts plus numbered block 69 now, and during apply only `.github/workflows/ci.yml`, `scripts/docker-mode-smoke.sh`, and `tests/docker-mode-smoke/fixture.sql`; confirm no second harness, edits to archived block-46/67 plans, blocks68/70, runtime code, publishing workflows, or soak behavior.
