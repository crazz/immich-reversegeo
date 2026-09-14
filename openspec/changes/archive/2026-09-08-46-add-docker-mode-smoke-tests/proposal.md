## Why

Blocks 40–45 define and hermetically compare deployment-mode composition, but they cannot prove that the production Linux image preserves the real entrypoint, port, process, UID/GID, bundled-data, and mounted-storage boundaries. A bounded production-image smoke harness is needed before later CI integration and soak work can build on trustworthy container evidence.

## What Changes

- Add one local, CI-runnable command that builds one neutral production image once and exercises the Standard default, Web-only, Run-once, invalid public mode, and the private worker boundary.
- Use a disposable PostgreSQL fixture with only the minimal Immich-shaped schema/data required for deterministic startup, zero-work, scheduling, and child-launch observations; prohibit live geodata downloads.
- Assert non-root runtime identity, the framework-dependent /app entrypoint and same-image child launch, mode-specific HTTP and scheduler behavior, Run-once exit semantics, separate writable /config and /data mounts, and bundled-data visibility.
- Bound every wait, retain per-case inspect/log/exit diagnostics, and reap containers, networks, volumes, temporary state, and child processes on success, failure, and interruption.
- Add the bounded harness to the existing Linux Docker CI path without adding block 69's later dedicated integration/performance orchestration or changing the published image.

## Capabilities

### New Capabilities
- docker-deployment-mode-smoke-tests: Defines deterministic production-image evidence for deployment-mode lifecycle, networking, identity, storage, packaging, and cleanup boundaries.

### Modified Capabilities
- None.

## Impact

Planning targets a host-side smoke script under scripts/, a local package.json command, the existing .github/workflows/ci.yml Docker step, and fixture files outside the Docker build context. The production Dockerfile and reference Compose file are inputs to verify and SHOULD remain neutral unless implementation discovers a production-image defect. This change consumes blocks 40–45 and the Phase 5 private-worker protocol; it does not touch block 47 or replace block 69's later dedicated Docker integration/release job.
