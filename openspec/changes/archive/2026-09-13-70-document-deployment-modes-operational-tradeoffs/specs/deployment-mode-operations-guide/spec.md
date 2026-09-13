## Purpose

Gives self-hosters evidence-bounded, Docker-first instructions for selecting, invoking, operating, and safely recovering each released Immich ReverseGeo deployment mode.

## ADDED Requirements

### Requirement: Exact public deployment-mode selection
Public documentation SHALL identify `IMMICH_REVERSEGEO_MODE` as the sole public deployment-mode input and SHALL list only the exact ordinal lowercase values `standard`, `web-only`, and `run-once`. It SHALL state that an absent variable alone defaults to Standard; empty, whitespace-only, padded, case-varied, and unknown values are invalid and exit 2 before startup. It SHALL state that mode is read at startup, requires a restart to change, and is not saved in `settings.json` or editable in the Web UI. It MUST NOT expose a private role selector, protocol selector, command override, or undocumented argument.

#### Scenario: Operator relies on the compatible default
- **WHEN** a self-hoster uses the documented Standard Compose service without declaring `IMMICH_REVERSEGEO_MODE`
- **THEN** the guide identifies the result as Standard and does not instruct the operator to add an empty value

#### Scenario: Operator enters a non-exact value
- **WHEN** a self-hoster configures an empty, padded, case-varied, whitespace-only, or unknown mode value
- **THEN** the guide predicts bounded invalid-mode failure with exit 2 and directs the operator to use one exact lowercase value and recreate or restart the container

### Requirement: Decision-first mode behavior
The mode comparison SHALL explain that Standard runs the Web UI, internal scheduler, manual processing, and temporary worker jobs; Web-only runs the same Web UI, manual processing, Lookup, and heavy Data actions through temporary workers but registers no internal scheduler and preserves saved schedule values; and Run-once starts no Web listener or UI, runs exactly one authoritative pass in the invoking process, writes human-readable operator logs, and exits. The guide SHALL distinguish worker-backed heavy geodata/asset work from lightweight coordinated Web control and inventory operations without implying that heavy work executes inside the long-lived Web process.

#### Scenario: Self-hoster wants built-in scheduling
- **WHEN** a self-hoster wants an always-available UI and Immich ReverseGeo to own cadence
- **THEN** the guide recommends Standard and explains that due checks launch work only when the full eligibility check finds current work

#### Scenario: External system owns cadence but UI remains available
- **WHEN** a self-hoster wants the UI and manual/heavy UI actions but no automatic work from that Web container
- **THEN** the guide recommends Web-only, states that no scheduler runs regardless of saved schedule values, and states that those values remain editable and unchanged

#### Scenario: External scheduler owns each attempt
- **WHEN** a self-hoster wants cron, Compose, or another orchestrator to start one attempt at a time
- **THEN** the guide recommends Run-once and states that the process has no HTTP listener, child worker, detector precheck, automatic retry, replay, or second pass

### Requirement: Tested Docker and Compose instructions
The installation guidance SHALL use the neutral image `ghcr.io/immich-reversegeo/immich-reversegeo:latest`, the unchanged image entrypoint, separate writable persistent mounts at `/config` and `/data`, and the existing database environment/network contract. Standard and Web-only examples SHALL map a host address to container port `8080`; Run-once SHALL publish no port and use no automatic restart. The ephemeral Run-once example SHALL set exact `IMMICH_REVERSEGEO_MODE=run-once` on a dedicated service and invoke exactly `docker compose run --rm immich-reversegeo-run-once` without a command override or private selector.

#### Scenario: Standard Compose deployment
- **WHEN** an operator copies the documented Standard service
- **THEN** it omits the mode variable, uses the exact published image, maps container port 8080, and mounts distinct config and data volumes

#### Scenario: Web-only Compose deployment
- **WHEN** an operator copies the documented Web-only service variation
- **THEN** it differs from Standard by exact `IMMICH_REVERSEGEO_MODE=web-only` while retaining the Web port and independent config/data mounts

#### Scenario: Ephemeral Run-once deployment
- **WHEN** an operator follows the documented Run-once Compose invocation
- **THEN** Compose creates one removable process from the neutral image with shared persistent config/data, no published port, no restart policy, no command override, and one process exit result

### Requirement: Operational finality, cancellation, and retry guidance
The guide SHALL describe the Web worker states Idle, Starting, Running, Cancelling, and Failed in user terms; explain that cancellation first requests cooperative stop and may escalate to bounded process-tree termination; and state that already committed database or cache effects are not rolled back. A worker crash, startup/protocol failure, forced stop, or missing terminal SHALL be final for that request, release owned admission/resources after cleanup, and SHALL NOT cause an automatic replacement or retry. Troubleshooting SHALL direct operators to inspect safe UI status and logs, verify the process has ended, correct the cause, and make an explicit new manual or external attempt only when safe.

#### Scenario: Worker is cancelled or crashes
- **WHEN** a temporary worker is cancelled, forcibly stopped, or exits unexpectedly
- **THEN** the guide does not promise rollback or automatic recovery and gives concrete status/log/cleanup checks before an operator-owned retry

#### Scenario: Run-once process finishes
- **WHEN** Run-once reaches managed finality
- **THEN** the guide lists 0 for completed or no work, 2 for invalid invocation/mode, 3 for advisory-lock Busy, 4 for processing/domain failure, 5 for startup/configuration/dependency/infrastructure failure, and 130 for orderly cancellation, while stating that abrupt platform termination may use a platform status

#### Scenario: Advisory lock is busy
- **WHEN** Run-once exits 3 because another authoritative processing pass owns the global advisory lock
- **THEN** the guide explains that no pass ran, no application retry occurs, and any later retry is an explicit operator or orchestrator decision

### Requirement: Evidence-bounded startup and memory expectations
The guide SHALL explain that worker process startup and first-country cache preparation can add latency, that heavy geodata memory is owned by disposable workers rather than accumulated in Standard/Web-only Web hosts, and that process exit is the structural reclamation boundary. It MUST NOT publish a universal RAM requirement, RSS ceiling, slope, absolute peak, process-tree, or cgroup guarantee. Numeric or capacity advice SHALL be labeled as environment-specific measurement, and hardware-dependent NAS/HDD recommendations SHALL be separated from production-image-tested behavior.

#### Scenario: NAS operator evaluates resource needs
- **WHEN** a self-hoster asks how much memory or startup time a worker needs
- **THEN** the guide describes the verified ownership/cleanup model, identifies dataset, country, cache, storage, and host variability, and recommends observing their own deployment without asserting a universal number

### Requirement: Correct NAS scheduling and data-source guidance
NAS/HDD guidance SHALL use the existing enabled/disabled, hourly, every-few-minutes, every-few-hours, daily, weekly, and custom-cron schedule choices in Standard, or Web-only/Run-once for external cadence. It SHALL state that every Standard scheduled check uses the preserved full current-eligibility `EXISTS` observation and MUST NOT describe a watermark, incremental tail, separate reconciliation cadence, NAS mode, or withdrawn control. Guidance that mentions optional GADM downloads or processing SHALL repeat or directly link the non-commercial-use license restriction and recommend Lookup validation before bulk use.

#### Scenario: NAS owner reduces disk contention
- **WHEN** a NAS owner wants processing away from backup, scrub, or media-scan windows
- **THEN** the guide recommends an existing daily, weekly, interval, or custom schedule, or explicit external Run-once cadence, without inventing a NAS-specific control or separate reconciliation pass

#### Scenario: Operator considers GADM
- **WHEN** operational guidance suggests enabling GADM for a difficult location
- **THEN** it warns that GADM is for academic and other non-commercial use, links the data-source license guidance, and recommends testing Lookup before full-library processing

### Requirement: Verifiable public documentation surface
The canonical guide SHALL live at `docs/website/deployment-modes.md`, be present in `mkdocs.yml` navigation, and be cross-linked from setup, installation, configuration, app usage, public architecture, and troubleshooting pages. Claims SHALL be visibly categorized as production-image tested, contract-verified, or hardware/environment-dependent guidance. Documentation verification SHALL build the Zensical site, report no unresolved internal links, and verify the generated directory-style deployment-mode route; no screenshot approval is required.

#### Scenario: Documentation is verified
- **WHEN** the block 70 documentation changes are complete
- **THEN** `npm run docs:build` succeeds, internal links and the generated `/deployment-modes/` route resolve, and the commands have been checked against the canonical block 69 Docker evidence
