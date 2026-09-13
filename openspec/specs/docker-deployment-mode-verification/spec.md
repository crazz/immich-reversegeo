# Docker Deployment Mode Verification Specification

## Purpose

Defines the required Linux CI evidence that one neutral production image correctly supports every released deployment mode under isolated, secret-safe conditions.

## Requirements

### Requirement: One canonical production-image verification
The required Linux CI path SHALL invoke the canonical `npm run test:docker-smoke` entry exactly once. That invocation MUST build the production Dockerfile exactly once, resolve the resulting immutable image identity, and use only that identity for every mode case, including the existing private ready/EOF probe; CI MUST NOT perform a second image build or maintain a second smoke harness or npm alias.

#### Scenario: One image supplies every case
- **WHEN** the Docker mode integration job runs for a pull request or a push to `master` or `major-redesign`
- **THEN** exactly one neutral production image is built and its immutable identity is used for Standard, Web-only, Run-once, and invalid-mode verification

### Requirement: Isolated deterministic dependencies
The verification SHALL use a disposable PostgreSQL service on a run-unique internal network with no published database port, a run-unique database and schema, and a versioned minimal fixture. It SHALL mount separate run-unique writable roots at `/config` and `/data`, execute the unchanged image entrypoint as the declared non-root user, and SHALL NOT require Internet geodata downloads or repository/GitHub secrets.

#### Scenario: Dependencies become ready without fixed sleeps
- **WHEN** the fixture service starts
- **THEN** verification waits within a named deadline for container health, PostgreSQL readiness, and a fixture sentinel query before starting an application case

#### Scenario: Container state is isolated
- **WHEN** any mode case executes
- **THEN** it uses unique database, schema, network, config root, and data root state and can write independently to both mounted roots as a non-root process

#### Scenario: External data access is unavailable
- **WHEN** the mode matrix executes after fixture preparation
- **THEN** all required behavior completes from the image and deterministic local fixtures without external network downloads

### Requirement: Serving-mode behavior is verified
Standard and Web-only SHALL each expose the production HTTP service only through a random loopback host port and SHALL return HTTP 200 within a named readiness deadline. Standard verification MUST preserve two complementary scheduled phases: an eligible no-country asset reaches terminal skip and child reaping while the parent stays healthy, then a held real admitted worker proves same-image identity and child reaping when its parent stops. Exactly two admissions are expected. Web-only verification MUST prove that the same enabled and due saved schedule causes no scheduler, detector, or child activity while HTTP service remains healthy.

#### Scenario: Standard serves and proves both scheduled phases
- **WHEN** the image starts with the mode variable absent and the eligible scheduled fixture is due
- **THEN** HTTP readiness succeeds, S1 reaches terminal skip and child reaping with a healthy parent, then S2 proves a real held same-image worker and parent-stop reaping, with exactly two admissions and no in-Web execution

#### Scenario: Web-only serves without automatic work
- **WHEN** the same image and due saved schedule start with exact mode `web-only`
- **THEN** HTTP readiness succeeds and the observation window contains no scheduler, detector, or child launch activity

### Requirement: Non-serving and invalid modes have stable results
Run-once verification SHALL publish no port, open no HTTP listener, perform exactly one deterministic no-work attempt, and exit 0. The existing private ready/controlled-EOF case SHALL also remain listener-free and use the same packaged image identity. Invalid-mode verification SHALL publish no port, exit 2, emit the bounded `invalid-deployment-mode` diagnostic, and redact the supplied canary value and all credentials.

#### Scenario: Run-once completes without a listener
- **WHEN** the image starts with exact mode `run-once` against the held empty-count fixture
- **THEN** no port or listener is exposed, exactly one attempt reaches one terminal result, and the container exits 0 without a child process or retry

#### Scenario: Invalid mode fails before hosting
- **WHEN** the image starts with a canary-bearing unsupported public mode
- **THEN** it exposes no listener, exits 2, reports the stable invalid-mode category and accepted values, and does not reveal the supplied value or credentials

### Requirement: CI execution is bounded and required
The existing `CI` workflow SHALL run one `docker-mode-integration` job named `Docker Mode Integration` on `ubuntu-latest` after the normal application job for unfiltered pull requests and pushes to `master` and `major-redesign`. The workflow SHALL declare read-only contents permission, cancel superseded pull-request runs through one ref-scoped concurrency group, retain main-branch runs, apply a 30-minute job timeout, and use no cross-run Docker or dependency cache in this clean-image gate. It SHALL add no required scheduled soak trigger.

#### Scenario: Pull request integration gate
- **WHEN** a pull request creates or supersedes a CI run
- **THEN** the newest ref-scoped run contains one required Docker mode job and any superseded pull-request run is cancelled

#### Scenario: Main integration gate
- **WHEN** a commit is pushed to `master` or `major-redesign`
- **THEN** its Docker mode job is retained and bounded by the job and harness deadlines

### Requirement: Failure evidence and cleanup are final
On any case or cleanup failure, verification SHALL preserve bounded, redacted per-case stdout/stderr, wait/exit status, container inspection, health, network, mount, and process evidence as a CI artifact. The same canonical harness SHALL expose an idempotent cleanup-only entry that does not build or launch applications. Cleanup SHALL run regardless of prior outcome, remove only run-labeled containers, networks, volumes, owned egress controls, database/fixture state and work roots, retain evidence under the existing output tree, and SHALL NOT replace the primary failure classification. No captured or rendered evidence may expose credentials, connection strings, environment dumps, or canary values.

#### Scenario: Case fails
- **WHEN** a mode assertion, deadline, or service check fails
- **THEN** redacted evidence is uploaded and all run-owned resources are removed while the original case failure remains the job result

#### Scenario: Verification succeeds
- **WHEN** every mode assertion passes
- **THEN** cleanup still removes all run-owned resources and no failure-only artifact is required

### Requirement: Full soak remains outside required PR verification
The required Docker mode job SHALL NOT execute repeated-worker memory, cgroup/RSS threshold, or full soak behavior. Such behavior MAY run only through a separately invoked or scheduled block-68 performance path and SHALL NOT become a dependency of the required pull-request mode gate.

#### Scenario: Required pull-request run
- **WHEN** the Docker mode integration job runs for a pull request
- **THEN** it completes the bounded mode matrix without invoking the block-68 soak
