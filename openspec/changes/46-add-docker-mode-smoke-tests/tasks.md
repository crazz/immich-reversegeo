## 1. Prerequisite inventory and harness contract

- [ ] 1.1 Confirm blocks 40–45 and Phase 5 are applied; record the landed mode parser, Standard/Web-only status labels, scheduler/child lifecycle evidence, private ready protocol, Run-once no-work/exit mapping, and detector/executor SQL, and stop if any required boundary is absent.
- [ ] 1.2 Define supported native Linux Docker prerequisites, run-unique labels/names, image input/build-once contract, per-assertion deadlines, redaction rules, evidence paths, and the cleanup manifest for scripts/docker-mode-smoke.sh.
- [ ] 1.3 Add npm run test:docker-smoke as the single local/CI entry and concise script help without advertising the private worker token.

## 2. Deterministic dependencies and isolated state

- [ ] 2.1 Add a pinned disposable PostgreSQL fixture on a run-unique internal network with no published database port, active readiness checks, random scoped credentials, and minimal landed asset/asset_exif schema/reset SQL.
- [ ] 2.2 Add empty/no-work and fixed ocean-coordinate scheduled fixture states; verify the latter uses bundled country data, creates no Overture/GADM download cache, and provides a bounded worker-observation hold through test SQL only if required.
- [ ] 2.3 Create separate per-case /config and /data bind directories prepared for the inspected image UID/GID, verify they are distinct and writable through application-created artifacts, and keep work state separate from retained diagnostics.

## 3. Build and packaging assertions

- [ ] 3.1 Build src/ImmichReverseGeo.Web/Dockerfile once under a run-unique tag, or accept an explicitly supplied prebuilt image, resolve one immutable image ID, and require every matrix case to use it.
- [ ] 3.2 Assert the image has no baked mode, retains /app plus dotnet ImmichReverseGeo.Web.dll, configures a numeric non-zero UID/GID, and contains readable non-empty ISO/profile/attribution and both bundled SQLite data files.
- [ ] 3.3 Add common inspect assertions that no application case overrides the image user, publishes an unexpected port, mounts /var/run/docker.sock, or uses a mode-specific entrypoint.

## 4. Web mode smoke cases

- [ ] 4.1 Start absent-mode Standard with a run-unique loopback port and due-schedule fixture; poll HTTP readiness, assert Standard/scheduling public status, and capture all logs and inspect data.
- [ ] 4.2 Observe one scheduler-admitted same-image private child in Standard using finalized lifecycle/status evidence plus host-side process command and UID/GID; prove /app self-launch, no in-Web fallback, expected mount writes, and parent shutdown reaps the child.
- [ ] 4.3 Start exact web-only against the same saved due schedule; assert HTTP and Web-only disabled-scheduling status, cross the bounded due window, and prove no scheduler/detector/child lifecycle evidence or automatic processing mutation.

## 5. Non-Web and failure smoke cases

- [ ] 5.1 Run exact run-once against the reset empty fixture with no published port; hold it only at a bounded deterministic fixture gate, prove a host-side request to its container IP on port 8080 fails, then assert one finalized no-work attempt, exact exit 0 before deadline, and no remaining process.
- [ ] 5.2 Run the exact private worker under controlled stdin only long enough to validate its canonical ready event, /app packaged layout, non-root UID/GID, no published port, and failed host-side request to its container IP on port 8080, then reap it without exposing it as a public mode.
- [ ] 5.3 Run one canary-bearing invalid public mode with fresh mounts and no database dependency; assert exit 2, bounded redacted accepted-values stderr, no HTTP/child, and zero application-created mount files.

## 6. Diagnostics, cleanup, and CI boundary

- [ ] 6.1 Implement named finite polling/wait helpers and EXIT/INT/TERM cleanup that captures image/container/PostgreSQL inspect, logs, exit codes, ports, process snapshots, and mount ownership on failure, then removes every run-labeled container, network, work directory, and child process.
- [ ] 6.2 Add self-checks that fail on forced cleanup, leaked resources, leaked fixture credentials/canaries, live geodata download markers/cache files, or a Docker-socket mount; retain sanitized failure evidence under _out/docker-mode-smoke/.
- [ ] 6.3 Invoke the canonical bounded command from the existing Ubuntu CI app job while preserving one image build; do not alter block 47, add multi-platform/emulation/soak/cgroup work, create a dedicated integration job, or restructure docker-publish.yml ahead of block 69.

## 7. Verification

- [ ] 7.1 Run npm run test:docker-smoke on supported native Linux Docker and verify deterministic assertions, useful intentional-failure diagnostics, interruption cleanup, and zero owned resources afterward.
- [ ] 7.2 Run npm run test to keep block 45's hermetic default suite distinct and passing; confirm the normal test command neither starts Docker nor discovers the smoke harness.
- [ ] 7.3 Run openspec validate 46-add-docker-mode-smoke-tests --strict, inspect openspec status --change 46-add-docker-mode-smoke-tests, and perform a block-46-only scope review covering no production test endpoint, no Docker socket/download, no block-47 edits, and no block-69 orchestration.
