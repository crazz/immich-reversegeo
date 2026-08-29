## 1. Reconcile prerequisites and single ownership

- [ ] 1.1 Verify blocks 40–46 and 67 are applied; record the landed mode values, safe lifecycle/terminal observations, canonical `scripts/docker-mode-smoke.sh` interface, and exact `npm run test:docker-smoke` entry without changing their behavior.
- [ ] 1.2 Search `.github/workflows/ci.yml` for every Docker build/smoke invocation and plan the relocation so the required workflow contains exactly one canonical smoke invocation and no separate Docker build.
- [ ] 1.3 Confirm block 46's harness builds `src/ImmichReverseGeo.Web/Dockerfile` once, resolves the immutable image ID, records it, and rejects any case that does not use that identity.

## 2. Deterministic Linux fixture contract

- [ ] 2.1 Verify block 46 pins the disposable PostgreSQL image by full digest and provides a run-unique internal network, container label/prefix, database, and schema with no published database port and fixed local non-secret credentials; stop and return gaps to block 46 rather than patching its harness here.
- [ ] 2.2 Verify block 46's versioned minimal `asset`/`asset_exif` SQL and local data fixtures support one eligible scheduled/Run-once pass without Internet geodata downloads and expose a committed fixture sentinel.
- [ ] 2.3 Verify the canonical harness gates app startup on Docker health, in-container `pg_isready`, and the sentinel query within the 60-second database deadline by polling rather than fixed success sleeps.
- [ ] 2.4 Verify the canonical harness creates distinct run-unique config and data bind roots, prepares them for the image's declared UID/GID, mounts them read-write at `/config` and `/data`, and asserts non-root execution plus successful independent writes.
- [ ] 2.5 Verify the canonical harness denies external egress after image/local-fixture preparation while preserving only app-to-PostgreSQL communication and fails with network evidence if any case attempts a download.

## 3. Canonical mode evidence

- [ ] 3.1 Verify the canonical Standard case omits mode, maps a random loopback port to container port 8080, returns HTTP 200 within 90 seconds, admits exactly one due fixture schedule, observes one scheduler-originated same-image child and one safe terminal outcome within 120 seconds, then returns idle and stays healthy.
- [ ] 3.2 Verify the canonical Web-only case sets exact `web-only`, returns HTTP 200, and proves the same enabled/due settings produce zero scheduler, detector, or child lifecycle observations throughout the bounded window.
- [ ] 3.3 Verify the canonical Run-once case sets exact `run-once`, publishes no port, opens no listener, makes exactly one successful fixture attempt without a child or retry, and exits 0 within 120 seconds.
- [ ] 3.4 Verify the canonical invalid-mode negative uses a unique canary, publishes no port, exits 2, emits bounded `invalid-deployment-mode` and accepted-values text, and reveals neither the canary nor credentials.
- [ ] 3.5 For every case, assert the unchanged entrypoint, declared non-root user, separate RW mounts, unique state, and the single recorded image ID; do not add a second mode matrix or harness.

## 4. Required CI orchestration

- [ ] 4.1 Keep the existing unfiltered `pull_request` and `push`-to-`master` triggers, add `permissions: contents: read`, and add the ref-scoped `ci-${{ github.workflow }}-${{ github.event.pull_request.number || github.ref }}` concurrency group with cancellation only for pull requests.
- [ ] 4.2 Add job id `docker-mode-integration`, name `Docker Mode Integration`, `needs: app`, `runs-on: ubuntu-latest`, and `timeout-minutes: 30`; invoke exactly `npm run test:docker-smoke` once and remove the prior Docker-build-only/duplicate smoke step.
- [ ] 4.3 Keep the Docker job free of cross-run Docker/npm/NuGet caches and scheduled triggers; document that optional repeated-worker/cgroup/RSS soak remains block 68 and outside required PR CI.
- [ ] 4.4 Verify the invoked harness applies its named 60-second database, 90-second Web, 120-second terminal, and 30-second cleanup bounds while preserving its no-fixed-sleep success policy; treat a missing bound as an unmet block-46 prerequisite.

## 5. Failure evidence, cleanup, and verification

- [ ] 5.1 Verify the canonical harness writes bounded per-case stdout/stderr, wait/exit status, allow-list-redacted inspect, health, network, mount, and process snapshots under `_out/docker-mode-integration/<run>/`; scan them for credentials, connection strings, environment dumps, and canaries before CI upload.
- [ ] 5.2 Upload failure evidence with `actions/upload-artifact@v6`, `if: failure()`, a run-unique artifact name, `if-no-files-found: error`, and 7-day retention.
- [ ] 5.3 Add `if: always()` idempotent cleanup for only run-labeled containers/network and run-owned database/fixture/bind roots; preserve the primary failure and forbid broad Docker prune.
- [ ] 5.4 Exercise success and forced-failure paths on Linux, prove one image build/ID, four exact cases, retained redacted evidence, and zero leaked resources; run `openspec validate 69-add-docker-mode-integration-job --strict` and final status.
- [ ] 5.5 Review the diff for block-69-only scope: exactly four planning artifacts plus numbered block 69 now, and during apply only `.github/workflows/ci.yml`; confirm no edits to the block-46 harness, blocks 68 or 70, runtime code, publishing workflows, or soak behavior.
