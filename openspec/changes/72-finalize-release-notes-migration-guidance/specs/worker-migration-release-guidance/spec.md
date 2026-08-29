## Purpose

Defines evidence-gated, synchronized release communication so self-hosters can upgrade, operate, and roll back the worker-process release without relying on unsupported compatibility, memory, or migration claims.

## ADDED Requirements

### Requirement: Evidence gates release claims
Release communication SHALL claim a worker-migration behavior only when the maintainer checklist contains a passing evidence-matrix row for that exact behavior. Every mandatory row MUST record the exact bounded wording, owning block, landed source/test/documentation link, command or CI URL and result, reviewer and review date, release-candidate digest/tag, and previous-image identity when rollback or cross-image compatibility is involved. Unfinished authorized work, missing or contradictory evidence, failed checks, and unverified upgrade or rollback combinations MUST remain release blockers; removing mandatory mode, migration, compatibility, license, or rollback guidance MUST NOT unblock release.

#### Scenario: A prerequisite lacks evidence
- **WHEN** any required block 1–71 behavior, documentation, CI result, upgrade test, or rollback test is incomplete, failed, stale, missing, or contradictory
- **THEN** the release remains blocked and the affected behavior is not presented as a released fact

#### Scenario: Rejected prerequisite blocks are complete
- **WHEN** blocks 62–64 are evaluated at the release gate
- **THEN** completion means their no-go artifacts are finalized and strictly validated, retained full current-eligibility behavior is evidenced from its landed owner, and negative inspection proves no watermark, reconciliation cadence, NAS-specific control, or related release claim was implemented

#### Scenario: The evidence gate passes
- **WHEN** mode, UI/dependency, protocol/failure/finality, soak, documentation, Docker CI, upgrade, and rollback evidence is linked and passing for the release candidate
- **THEN** maintainers can trace each published claim to its owning evidence and image identity

### Requirement: Release seams remain synchronized
The technical `CHANGELOG.md`, user-facing `docs/website/changelog.md`, and maintainer `docs/maintainer/RELEASE_CHECKLIST.md` MUST express the same default, accepted modes, compatibility limits, retry semantics, and rollback boundary while retaining audience-appropriate detail and mutual links. Entries SHALL remain `Unreleased` or use explicit version/date placeholders until the actual release version and date are known.

#### Scenario: Maintainer reviews release text
- **WHEN** the three release seams are reviewed before publication
- **THEN** their normative claims agree, their required links resolve, and no fabricated version or date appears

### Requirement: Mode migration guidance is exact
Release guidance SHALL state that only an absent `IMMICH_REVERSEGEO_MODE` defaults to Standard; exact lowercase `standard`, `web-only`, and `run-once` are accepted; all other present values fail before startup with exit 2; selection is startup-only, restart-required, and not persisted. It SHALL describe one neutral image and unchanged entrypoint for all modes, with same-image temporary workers in Standard/Web-only, without exposing or recommending a private worker selector.

#### Scenario: Existing installation upgrades without setting a mode
- **WHEN** a self-hoster starts the upgraded image with `IMMICH_REVERSEGEO_MODE` absent
- **THEN** the guidance identifies Standard as the default and requires no mode-setting migration

#### Scenario: Operator supplies a non-exact value
- **WHEN** the variable is empty, whitespace, padded, case-varied, or unknown
- **THEN** the guidance predicts pre-start exit 2 without presenting normalization or aliases

#### Scenario: Reader follows public examples
- **WHEN** a self-hoster copies any release or migration example
- **THEN** it uses only the public mode variable and never a private worker selector or protocol control

### Requirement: Storage and data compatibility claims are bounded
Release guidance SHALL require separate persistent `/config` and `/data` mounts for applicable services and SHALL state that the release introduces neither an Immich schema change nor migration of Immich schema data or persisted ReverseGeo configuration data. It MUST limit config, cache, and data compatibility claims to behavior verified by the upgrade/rollback matrix.

#### Scenario: Existing volumes are reused for upgrade
- **WHEN** the release-candidate image starts with tested preexisting separate `/config` and `/data` volumes
- **THEN** the checklist records settings/data usability and the release guidance reports only that verified compatibility

### Requirement: Web-only and Run-once operations are actionable
Release guidance SHALL state that Web-only retains the Web UI, manual processing, and saved schedule values while disabling the internal scheduler, and that verified Lookup/supported heavy Data actions run through workers without adding a public automation endpoint. It SHALL state that Run-once exposes no listener, child, detector precheck, automatic restart, or internal retry; performs one direct same-process attempt; can retain committed effects; and maps exit 0 to completed including no work, 2 to invalid public invocation or mode, 3 to advisory-lock busy, 4 to domain failure, 5 to startup/infrastructure/required dependency or cleanup failure, and 130 to orderly cancellation. It SHALL allow abrupt platform termination to produce an unmapped raw status.

#### Scenario: Operator migrates cadence externally
- **WHEN** a self-hoster selects Web-only
- **THEN** the guidance explains that saved schedules are retained but inactive and manual/UI behavior remains available

#### Scenario: Automation invokes Run-once
- **WHEN** an external scheduler invokes the Run-once service once
- **THEN** the guidance maps its stable exits, states that no application retry occurs, and assigns retry policy to the operator

### Requirement: Temporary-process and memory claims retain evidence limits
Release guidance SHALL limit disposable-worker claims to verified Standard/Web-only heavy jobs and cleanup finality. It MUST NOT describe Run-once as child-worker execution or promise lower total memory, a universal RAM/RSS threshold, an absolute or process-tree/container peak, or guaranteed numeric reclamation without an explicitly compatible measured profile.

#### Scenario: Release notes summarize memory behavior
- **WHEN** worker isolation is described
- **THEN** the wording identifies the process boundary and cleanup evidence and preserves the sampler/profile limitations

### Requirement: Optional GADM license remains visible
User-facing release guidance SHALL identify optional GADM data as restricted to academic and other non-commercial use and SHALL link to the public data-source/license guidance.

#### Scenario: User considers GADM after upgrade
- **WHEN** the release summary mentions optional GADM-backed behavior
- **THEN** the user sees the non-commercial restriction and a link to the license guidance before enabling it

### Requirement: Rollback uses a tested stopped-work previous-image path
Rollback guidance SHALL identify the tested upgraded and previous released image identities, require stopping admission and active work before stopping the upgraded instance, and describe starting the previous image with the same tested separate volumes. It MUST disclose compatibility caveats for settings, caches, retained Immich writes, and forward-created data, recommend backups or snapshots, and MUST NOT claim zero downtime, concurrent old/new operation, automatic reversal, or universal backward compatibility.

#### Scenario: Operator rolls back after upgrade
- **WHEN** active work has been stopped and the upgraded instance is shut down
- **THEN** the operator can start the named tested previous image with the tested volumes and validate the documented representative behavior

#### Scenario: Rollback combination is untested
- **WHEN** the previous image and current volume state were not exercised together
- **THEN** release guidance does not call that combination compatible and the release remains blocked or the limitation is made explicit

### Requirement: Rejected scheduling work is not released by implication
Release guidance MUST NOT claim a persisted watermark, incremental detector, periodic reconciliation cadence, or NAS-specific scheduling controls from blocks 62–64. It SHALL describe only the retained full current-eligibility check, existing schedule presets, Standard scheduling, Web-only suppression, and operator-scheduled Run-once where those facts are verified.

#### Scenario: Release text discusses scheduling efficiency
- **WHEN** maintainers review the release seams
- **THEN** no claim depends on withdrawn blocks 62–64 or stale periodic-reconciliation language
