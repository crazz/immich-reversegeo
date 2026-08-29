## Why

The worker contracts now span process launch, two protocol versions, three heavy job kinds, cancellation escalation, PostgreSQL exclusion, cache publication, arbitration, and structured lifecycle telemetry. Focused tests do not yet prove that those contracts compose at the real OS-process boundary without deadlock, orphaned children, leaked locks or temporary files, terminal rewrites, or unsafe retries.

## What Changes

- Extend the established hermetic worker-process fixture into a table-driven failure matrix for v1 `ProcessAssets` and applicable v2 `CoordinateLookup` and `CacheMutation` jobs.
- Cover pre-host invalid mode/protocol selection, unusable worker configuration/dependency, invalid request/payload/selector, and Busy/Unavailable refusal; spawn and readiness failure; database unavailability; pre/post-ready crashes and mapped/unmapped exits; parent shutdown; cooperative cancellation and forced whole-tree termination at the 10-second `TimeProvider` boundary; ProcessAssets lock contention; malformed/truncated/oversized/out-of-order/unknown NDJSON; simultaneous stdout/stderr pipe pressure; cache failure/retry/temp cleanup; and orphan detection.
- Require each row to assert exact exit/terminal authority, block-66 event identity and safe classification, child/process-tree state, stream finality, coordinator release, and applicable lock/cache artifacts.
- Keep deterministic no-download fixtures in the normal default suite. Gate only rows that require external PostgreSQL as `Integration`, and report explicit capability-based platform skips.
- Add tests and fixture modes only. Do not change production protocol, launcher, cancellation, arbitration, cache, deployment-mode, telemetry, or retry behavior.

## Capabilities

### New Capabilities

- `worker-process-failure-recovery`: Deterministic process-boundary verification of failure classification, finality, resource cleanup, and explicit retry across supported worker jobs.

### Modified Capabilities

- None.

## Impact

Planning affects the existing worker-process fixture and process-focused tests under `tests/ImmichReverseGeo.Tests/`, plus controlled cache/protocol/dependency fixture inputs. It consumes the fixed protocol and exit contracts from blocks 15–32, mode/Docker fixture boundaries from 40–46, v2 jobs and arbitration/cache contracts from 47–54, and the structured event catalog from block 66. No live Overture or GADM endpoint, new production dependency, CI workflow, Docker image contract, public setting, or project runtime code is in scope.
