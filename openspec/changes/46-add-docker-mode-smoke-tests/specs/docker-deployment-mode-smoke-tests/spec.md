## Purpose

Defines deterministic, bounded evidence that one production Linux image honors deployment-mode process, HTTP, identity, packaging, and mounted-storage contracts without live geodata downloads.

## ADDED Requirements

### Requirement: One neutral production image drives the complete smoke matrix
The smoke harness SHALL build the production Dockerfile exactly once per invocation, assign a run-unique local tag, and reuse that immutable image ID for every case. The image MUST have no baked IMMICH_REVERSEGEO_MODE, MUST retain the production dotnet ImmichReverseGeo.Web.dll entrypoint and /app working layout, and MUST contain the required bundled country, airport, profile, attribution, and ISO data. Standard SHALL be tested once with the mode variable absent, not merely with explicit standard.

#### Scenario: Matrix image is prepared
- **WHEN** the local smoke command starts on a supported Linux Docker host
- **THEN** one neutral image is built and every Standard, Web-only, Run-once, invalid-mode, and private-worker check records the same image ID

#### Scenario: Packaged runtime layout is inspected
- **WHEN** the built image metadata and runtime filesystem are checked
- **THEN** the production entrypoint, /app application assembly, and all bundled-data files are present without a mode-specific command or image

### Requirement: Web-serving and scheduling boundaries are observable
With a ready deterministic database and isolated writable mounts, the absent-mode Standard container and exact web-only container SHALL become reachable through a run-unique host port mapped to container port 8080. Standard evidence SHALL show scheduling is available and SHALL observe one scheduler-admitted same-image private child using the finalized worker lifecycle evidence. Web-only SHALL expose the Web UI while its public mode/schedule status states that automatic scheduling is disabled, and a saved due schedule SHALL produce no scheduler wait, detector, or child-worker lifecycle evidence during the same bounded observation window. HTTP readiness MUST be determined by successful requests, not fixed sleeps.

#### Scenario: Standard default serves and launches its child
- **WHEN** the mode variable is absent and the deterministic due-schedule fixture admits one run
- **THEN** HTTP on container port 8080 is reachable, Standard scheduling is reported available, and logs/status plus host process evidence show one same-image --internal-worker lifecycle without an in-Web fallback

#### Scenario: Web-only crosses the same due schedule
- **WHEN** exact web-only starts with the same saved schedule and the observation window crosses its due time
- **THEN** HTTP is reachable, the UI reports Web-only with automatic scheduling disabled, and no scheduler or child-worker evidence appears

### Requirement: Non-Web invocations have deterministic lifecycle and no HTTP
Exact run-once SHALL use the same entrypoint, execute one attempt against the disposable empty fixture, expose no host port or listening HTTP service, emit the finalized zero-work outcome, and exit 0 within its deadline. A direct exact --internal-worker protocol probe MAY be used only as private smoke evidence: it SHALL reach the finalized ready boundary from /app, expose no host port or listening HTTP service, and be terminated through bounded cleanup without documenting the token as a public mode. While each non-Web process is held alive by a bounded deterministic fixture gate, a host-side request to its container-network address on port 8080 MUST fail.

#### Scenario: Run-once finds no eligible work
- **WHEN** exact run-once starts against the ready empty Immich-shaped database
- **THEN** it exposes no HTTP mapping, reports the finalized no-work success, exits exactly 0 once, and leaves no application process running

#### Scenario: Private worker layout is probed
- **WHEN** the image is invoked with the exact private role token under controlled stdin
- **THEN** it emits valid ready evidence from the packaged application layout, has no HTTP listener, and is reaped without being treated as a public deployment mode

### Requirement: Invalid mode fails before startup side effects
A representative secret-bearing invalid mode SHALL exit exactly 2 within a short deadline, expose no HTTP, start no child, and produce the finalized bounded invalid-deployment-mode stderr diagnostic without echoing the supplied canary. Its fresh /config and /data mounts SHALL remain free of application-created files.

#### Scenario: Invalid value contains a canary
- **WHEN** the image starts with an unsupported canary-bearing IMMICH_REVERSEGEO_MODE
- **THEN** it exits 2, the diagnostic names the setting and accepted public values without the canary, no HTTP or child exists, and neither mounted root is mutated

### Requirement: Runtime identity and mounted storage remain non-root and usable
The image metadata SHALL identify a configured numeric non-zero user, and live process evidence SHALL identify its numeric non-zero UID/GID. Standard, its observed private child, Web-only, Run-once, and the private protocol probe SHALL execute with that same UID/GID; no case MAY override the container user. The harness SHALL provide separate fresh host directories for /config and /data, SHALL verify application-created artifacts in each root are owned by the runtime identity, and SHALL verify the roots are not aliased. No container SHALL mount a Docker socket.

#### Scenario: Standard initializes isolated mounts
- **WHEN** Standard starts with separate prepared /config and /data bind mounts
- **THEN** application-owned configuration/key material and deterministic data state can be created, remain in their respective roots, and are owned by the non-root image UID/GID

#### Scenario: Parent and child identity is observed
- **WHEN** Standard owns a live scheduled private child during the bounded observation gate
- **THEN** host-side process inspection reports the Web parent and worker child under the image's same non-zero UID/GID and the container has no Docker-socket mount

### Requirement: Database and geodata dependencies are deterministic
The harness SHALL start a run-unique disposable PostgreSQL container on an internal run-unique network, wait for database readiness, and initialize only the minimal quoted asset/asset_exif schema and rows needed by the finalized detector/executor queries. The fixture SHALL provide an empty database for Run-once and a bounded ocean-coordinate scheduled row or equivalent landed no-download fixture for Standard child launch. The application containers MUST have no published database port and MUST NOT download Overture or GADM data; assertions SHALL fail if download/cache artifacts or download log markers appear. Bundled files SHALL be read from the image, not copied into the mounts.

#### Scenario: Disposable dependencies are ready
- **WHEN** a mode case begins
- **THEN** PostgreSQL has passed an active readiness query, the selected fixture is reset deterministically, and the application receives only run-local database coordinates and credentials

#### Scenario: Smoke processing needs geographic data
- **WHEN** the Standard child handles the scheduled smoke row
- **THEN** it resolves the no-download path using bundled country data and creates no per-country Overture or GADM download cache

### Requirement: Harness execution is bounded and diagnostic
Every readiness, due-schedule, child-observation, exit, and cleanup wait SHALL have an explicit deadline and diagnostic label. The harness SHALL capture image/container inspect data, process snapshots, HTTP observations, PostgreSQL readiness/fixture output, complete application logs, and exit codes into a run-unique _out/docker-mode-smoke/ directory on failure while redacting fixture credentials and canaries. A trap/finally path SHALL stop and remove every labeled application/database container, network, and temporary mount and SHALL fail if any owned container or child process remains.

#### Scenario: A case times out
- **WHEN** an expected HTTP, scheduler, child, protocol, or exit event misses its deadline
- **THEN** the harness fails that named assertion, captures actionable diagnostics, and performs the same bounded cleanup as a normal run

#### Scenario: Harness completes
- **WHEN** all matrix assertions pass or the command is interrupted
- **THEN** no run-labeled container, network, temporary database, mount directory, or application child remains

### Requirement: Local and CI ownership stays bounded to block 46
The package command npm run test:docker-smoke SHALL be the canonical local command and SHALL fail early with a clear prerequisite message when Docker is unavailable or the host is unsupported. The existing Ubuntu CI path SHALL invoke the same command as a bounded production-image smoke gate. This change MUST NOT add multi-platform emulation, registry publication, cgroup/RSS thresholds, repeated-run soak, a dedicated scheduled/performance job, or the final publish-workflow integration owned by blocks 68–69.

#### Scenario: Developer runs the canonical command
- **WHEN** a developer with a supported Docker engine runs npm run test:docker-smoke
- **THEN** the same script, matrix, assertions, deadlines, and cleanup used by Linux CI execute locally

#### Scenario: Later CI work consumes the harness
- **WHEN** block 69 adds its dedicated Docker mode integration and release orchestration
- **THEN** it can invoke or promote this harness without block 46 preemptively adding block 69's platform, performance, or publication responsibilities
