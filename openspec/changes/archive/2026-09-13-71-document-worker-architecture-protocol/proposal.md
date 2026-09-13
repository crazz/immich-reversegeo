## Why

The worker-process migration now spans private process selection, two protocol generations, multiple job kinds, parent finality, cross-process locking, cache publication, and a deliberately lightweight Web control plane. Maintainers need one source-linked reference after blocks 15–56 and 65–69 finalize those contracts, without exposing private worker controls as supported self-hoster interfaces.

## What Changes

- Add `docs/maintainer/WORKER_ARCHITECTURE_PROTOCOL.md` as the evidence-backed reference for composition roots, job/protocol generations and identity, stream ownership, ordering/finality, managed exits, cancellation, locking, arbitration, cache atomicity, dependency policy, telemetry, and safe debugging.
- Add a compact composition/job/protocol matrix and explicit public/private boundary: public deployment modes link to block 70; exact `--internal-worker` and `IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION` remain private and secret-safe.
- Link every architectural claim to finalized source/tests and Docker or soak evidence, and add source-token drift checks for frozen constants and closed vocabularies.
- Add the guide to the repository's maintainer discovery surface while keeping `docs/maintainer/` outside the public `mkdocs.yml` navigation.
- Do not edit block 70, block 72, or public implementation documentation in this change.

## Capabilities

### New Capabilities
- `worker-architecture-maintainer-guide`: A verified maintainer-only reference for the released worker architecture, protocol, finality, safety boundaries, and evidence.

### Modified Capabilities
- None.

## Impact

Planning is limited to numbered block 71 and `openspec/changes/71-document-worker-architecture-protocol/`. Apply work will affect one new maintainer guide and the existing maintainer discovery surface only. It depends on finalized blocks 15–32, 40–56, and 65–69, consumes block 70 only as a public cross-link target, and must pass repository-link, source-token drift, public docs build, strict OpenSpec, and scope checks.