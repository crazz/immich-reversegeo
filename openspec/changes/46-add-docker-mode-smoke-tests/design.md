## Context

See proposal.md and the smoke-test specification. The production image is a framework-dependent .NET 10 chiseled image with WORKDIR /app, ENTRYPOINT dotnet ImmichReverseGeo.Web.dll, port 8080, separate /config and /data roots, APP_UID, and bundled data copied outside dotnet publish. docker-compose.yml is an operator reference, not a test topology. package.json has build/up/down commands but no smoke command, and no active root scripts/ directory exists. .dockerignore excludes tests/, .github/, package.json, and _out/, so the harness and diagnostics remain outside the image.

Blocks 40–45 are planning prerequisites. At apply time, inventory their landed mode, scheduler/status, command-builder, protocol, and outcome APIs before choosing exact log/status markers. Stop rather than inventing a second mode parser, worker protocol, scheduler, or production-only test endpoint when those prerequisites are absent.

## Goals / Non-Goals

**Goals:**
- Exercise one real production image through its unchanged entrypoint in the public mode matrix and a narrowly controlled private-role probe.
- Obtain deterministic image/process/HTTP/exit/UID/GID/mount/bundled-data/child evidence with no live geodata downloads.
- Make one host-side command usable on a developer Linux Docker engine and in the existing Ubuntu CI job.
- Preserve enough labeled evidence to diagnose failures while always cleaning owned resources.

**Non-Goals:**
- Do not duplicate block 45's descriptor/provider matrix or use its hermetic fakes as image evidence.
- Do not change mode behavior, add a smoke-only HTTP endpoint, expose the private worker token, or mount the Docker socket into the application.
- Do not test real Immich migrations/data volume, external geodata, throughput, memory, crash/cancellation races, repeated workers, or multi-architecture output.
- Do not modify block 47. Do not create block 69's dedicated integration/performance/publish orchestration.

## Decisions

### 1. Use one strict host-side Bash harness and one npm alias

Add scripts/docker-mode-smoke.sh as the canonical implementation and test:docker-smoke as its stable local/CI entry. Bash is available on the supported Ubuntu runner and can use Docker CLI, curl, and standard process/file tools without putting test utilities into the chiseled image. The script uses strict shell options, run-unique names/labels, an explicit image tag, and helper functions for deadline polling, assertions, diagnostics, and cleanup.

Alternative: encode the matrix in Compose. Rejected because the reference Compose file is user-facing, fixed-name, and not designed for per-case ports, exit capture, or failure traps. A private smoke Compose fixture may be added only if it materially simplifies PostgreSQL topology; the script remains the public command and passes a run-unique project name.

### 2. Build once, then pin every case to the image ID

Run one docker build for src/ImmichReverseGeo.Web/Dockerfile, resolve its image ID, and use that ID for all runs. Inspect the image before cases: no mode environment entry, /app working directory, unchanged entrypoint, numeric non-zero Config.User, and architecture matching the native host. Verify all bundled files, including both SQLite databases, inside the image and record non-zero size/readability; the actual Standard child/no-download row supplies behavioral visibility of bundled country data.

Alternative: one build per mode. Rejected because it could hide mode-specific build drift and wastes CI time. Alternative: parse Dockerfile text only. Rejected because it cannot prove final metadata or files.

### 3. Use isolated bind mounts prepared for the image UID/GID

Create separate run/case directories beneath _out/docker-mode-smoke/<run-id>/work/, prepare only those directories as writable by the inspected image UID/GID, and bind them to /config and /data. Never pass --user; process identity must come from the image. Assert mount sources differ, inspect every application container for no /var/run/docker.sock, and use host file metadata to verify application-created config/key material and data artifacts have the expected UID/GID. Keep diagnostics under a sibling evidence directory so cleanup can remove work while preserving failures.

The implementation must choose the least-permissive portable preparation supported by Ubuntu CI: host chown when permitted, otherwise a narrowly scoped host-directory permission setup. It must not use a root application run as the assertion path. Named volumes are rejected because an empty volume mounted over /config or /data may be root-owned and obscures deterministic host ownership assertions.

### 4. Use disposable real PostgreSQL, not a fake wire server

Start one pinned PostgreSQL test image per harness run on a run-unique internal bridge network, with no host-published port and run-local credentials. Wait with pg_isready plus a SQL query before applications start. A checked-in SQL fixture creates only columns/indexes required by the landed quoted asset/asset_exif queries and offers reset states:
- empty/no-work for Run-once and basic Web startup;
- one detector-eligible ocean coordinate for Standard, selected so bundled country lookup returns no country and processing records a skip without Overture/GADM cache download.

At apply time, reconcile fixture SQL against the actual Phase 5 detector/executor queries. If observing the worker UID needs a longer window, make the deterministic PostgreSQL fixture delay the worker-side query with a bounded SQL fixture function/view after detector admission; do not add sleeps to production, seed thousands of rows, contact geodata services, or rely on timing luck. The script polls host-side Docker process metadata and records the exact process command and UID/GID while the child is live.

Alternative: fake PostgreSQL. Rejected because implementing enough Npgsql wire behavior is more complex and less representative. Alternative: a full disposable Immich stack. Rejected as block 69/real integration scope. Alternative: omit Standard child launch. Rejected because /app self-launch is a core block-46 boundary.

### 5. Observe HTTP readiness and scheduler policy through public evidence

Publish container 8080 to a Docker-assigned loopback host port and discover the mapping with docker port; do not reserve a port with a racy pre-bind. Poll curl with finite per-request and overall deadlines until the root or Dashboard succeeds, saving the response. Use block 44's rendered mode/scheduling labels as stable positive evidence: Standard reports scheduling available; Web-only reports automatic scheduling disabled while saved values remain. Configure the same due schedule in each case, calculate a deterministic next occurrence, and cross one bounded due window.

For Standard, require finalized worker lifecycle/status/log evidence and a host process snapshot of the actual private child. For Web-only, require both its positive disabled-policy text and absence of scheduler/worker lifecycle markers across the same window. Exact markers must come from landed bounded status/log contracts, not arbitrary exception text. A negative observation alone is insufficient.

Alternative: add a smoke endpoint or environment hook. Rejected because it changes and security-expands production solely for tests. Alternative: fixed sleeps. Rejected because readiness and child timing vary.

### 6. Give non-Web and invalid cases stronger negative network assertions

Run Run-once and private-worker cases without port publication; assert inspect reports no published ports. Hold each process alive only through a bounded deterministic fixture/protocol gate, discover its bridge-network address from the host, and require a finite curl attempt to container port 8080 to fail before releasing the gate. Run-once then uses the empty fixture and a foreground wait helper that captures exact exit 0 and finalized no-work evidence. The private worker is started with controlled open stdin, accepted only after its canonical ready frame is observed, inspected for /app command/UID/GID/no listener, then stopped and reaped by the trap; the harness never sends an execute request unless the landed protocol fixture supplies canonical safe bytes.

Run invalid mode with fresh empty mounts, no database dependency, no port, and a secret-bearing canary. Require exit 2, bounded constant-form stderr, accepted public values, no canary, no child, and no mount mutation. This is representative image evidence; exhaustive invalid parsing/redaction remains in block 40.

### 7. Treat logs, timeouts, and cleanup as first-class harness behavior

Every asynchronous helper takes a named finite deadline. On any failure, capture image inspect/history, each container inspect/log/exit state, Docker process snapshots, port mappings, PostgreSQL logs/readiness and non-secret fixture state, mount trees/ownership, and an assertion manifest. Never print the random database password or invalid-mode canary. Use traps for EXIT/INT/TERM; remove resources by exact run label/project name, then verify none remain. Terminate the Standard parent through Docker and require its finalized shutdown to reap any active child before force-removal fallback; record fallback as a test failure, not success.

Keep successful output concise and remove work state. Preserve failure evidence under _out/, which is already ignored. The script must not use unbounded docker wait, background pipelines without recorded PIDs, or sleep as a success oracle.

### 8. Keep block 46's CI role deliberately smaller than block 69

Replace the existing CI Docker-build-only step with npm run test:docker-smoke, or let the script accept an explicitly supplied prebuilt tag so the CI job still builds exactly once. The check is the bounded PR/main CI gate for this phase.

Do not restructure docker-publish.yml here: independent GitHub workflows cannot safely share the local image, and building/pushing only after a dedicated reusable integration gate is block 69's responsibility after process hardening. Block 69 may reuse this command and fixtures while adding dedicated job topology, final release dependency, wider lifecycle assertions, and optional scheduled performance work. Multi-platform buildx/QEMU and memory/cgroup profiles remain outside block 46.

## Risks / Trade-offs

- **[Prerequisite mode/worker code is not actually landed]** → Apply begins with an inventory and stops if blocks 40–45/Phase 5 contracts are absent; do not plan around the pre-migration source currently visible.
- **[A minute-granularity schedule makes CI slow or flaky]** → Derive the nearest safe due time, use a bounded SQL hold for the child observation window, and key success to lifecycle/process evidence rather than elapsed sleep.
- **[Rendered Blazor content is not present in the first response]** → Use the landed server-rendered status seam or existing UI automation boundary; do not add a test-only endpoint.
- **[Ocean coordinates change bundled-country behavior]** → Choose and document a fixed point verified against the checked-in bundled DB, and fail if a per-country cache/download is attempted.
- **[Chiseled image lacks shell/debug tools]** → Inspect from the host and use the app's real entrypoint/protocol; never install tools into or derive a second image.
- **[Bind-mount permissions vary locally]** → Support Linux Docker as normative, fail early with actionable platform prerequisites, and avoid claiming Docker Desktop/macOS UID fidelity.
- **[Logs leak fixture secrets]** → Use random scoped credentials, redact evidence, and assert canaries are absent before retaining artifacts.
- **[Block 46 and block 69 overlap]** → Keep this as a bounded existing-job smoke command; defer dedicated orchestration, publish gating, multi-platform, soak, and cgroup work to block 69.

## Migration Plan

1. Confirm blocks 40–45 and Phase 5 are applied; map exact public labels, scheduler/child lifecycle markers, protocol ready bytes, database queries, and exit outcomes.
2. Add the run-labeled script, minimal SQL/config fixtures, and npm command; keep all test assets outside the Docker build context.
3. Implement build/inspect, PostgreSQL readiness/reset, per-mode runners, HTTP/process/identity/storage assertions, diagnostics, and cleanup.
4. Run the canonical command locally on native Linux Docker, then invoke it from the existing Ubuntu CI app job without changing the production image or reference Compose behavior.
5. Verify no Docker socket, live download, leaked resource, block-45 duplication, block-47 edit, or block-69 orchestration entered the change; run strict OpenSpec validation/status.

Rollback removes the host-side harness, fixtures, npm alias, and CI invocation. It does not migrate runtime settings, database data, images, or volumes.
