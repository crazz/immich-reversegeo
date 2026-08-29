## Why

The worker-process and deployment-mode migration changes upgrade, operation, and rollback expectations across one production image. Release communication must not turn finalized plans—or rejected work from blocks 62–64—into claims until the landed image, tests, documentation, and CI evidence prove them.

## What Changes

- Gate release wording on completed evidence from blocks 1–71 and retained links to mode, Docker-image, protocol/failure, memory-soak, documentation, upgrade, and rollback evidence; for rejected blocks 62–64, completion means finalized/validated no-go artifacts, retained full-eligibility evidence, and negative proof that no rejected implementation or release claim exists. Any unmet prerequisite is a release blocker.
- Synchronize the technical `CHANGELOG.md`, user-facing `docs/website/changelog.md`, and maintainer `docs/maintainer/RELEASE_CHECKLIST.md` around one evidence matrix while preserving their distinct audiences.
- Give self-hosters exact migration guidance for the Standard default, strict `IMMICH_REVERSEGEO_MODE` values, one neutral image, separate `/config` and `/data` volumes, Web-only and Run-once behavior, stable Run-once exits, GADM licensing, and supported rollback.
- Bound compatibility and memory wording: no Immich schema/config-data migration is introduced; temporary-process statements apply only where verified; rollback requires stopping active work and reusing a specifically tested previous image/volume combination, with no zero-downtime promise.
- Keep private worker selectors out of public release copy and make no watermark, reconciliation, or NAS-control claim from rejected blocks 62–64.
- Keep release entries under `Unreleased` or explicit version/date placeholders until the actual release identity and date are known.

## Capabilities

### New Capabilities
- `worker-migration-release-guidance`: Evidence-gated, synchronized upgrade, operation, compatibility, and rollback communication for the worker-process release.

### Modified Capabilities
- None.

## Impact

Planning covers only block 72 and its four OpenSpec artifacts. Apply later updates `CHANGELOG.md`, `docs/website/changelog.md`, and `docs/maintainer/RELEASE_CHECKLIST.md`; it consumes, but does not alter, finalized behavior and evidence from blocks 1–71, especially 40–56 and 65–71.
