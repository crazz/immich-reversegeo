## Why

Blocks 40–46 define and locally exercise three production-image deployment modes, but a reusable Docker harness is not sufficient unless required Linux CI invokes it once, bounds it, and preserves evidence. Block 69 promotes that existing harness into a deterministic PR/main gate without duplicating block 46 or pulling block 68's soak into every change.

## What Changes

- Add one required `docker-mode-integration` Linux job to the existing `CI` workflow, dependent on the normal `app` build/test job, for pull requests and pushes to `master`.
- Invoke exactly block 46's `npm run test:docker-smoke` entry once; its production Dockerfile build remains the sole build and its resolved immutable image ID serves every Standard, Web-only, Run-once, and invalid-mode case.
- Make CI orchestration explicit: read-only permissions, PR-aware concurrency cancellation, a 30-minute job timeout, clean-build/no-cross-run-cache policy, bounded service/case deadlines, and no required scheduled trigger.
- Require disposable no-published-port PostgreSQL, run-unique database/schema/network/mount roots, deterministic no-network fixtures, separate writable `/config` and `/data`, unchanged entrypoint, and non-root execution.
- Require mode evidence: serving health plus Standard scheduler/same-image child behavior, Web-only scheduler exclusion, Run-once no-listener/stable completion, and invalid-mode exit-2/redaction behavior.
- Upload redacted logs and inspect/process/health evidence on failure and always clean all labeled resources without masking the primary result.
- Keep full repeated-worker/cgroup/RSS soak optional or scheduled outside required PR CI and wholly owned by block 68.

## Capabilities

### New Capabilities
- `docker-deployment-mode-verification`: Required Linux CI orchestration of the canonical production-image mode harness with deterministic isolation, bounded evidence, and cleanup.

### Modified Capabilities
- None.

## Impact

Planning affects only block 69, `.github/workflows/ci.yml`, and CI consumption of block 46's existing `scripts/docker-mode-smoke.sh` / `npm run test:docker-smoke` interface. It depends on finalized blocks 40–46 and block 67 telemetry contracts; it does not change runtime code, Docker image contents, public mode behavior, publishing workflows, block 68 soak behavior, or block 70 documentation.
