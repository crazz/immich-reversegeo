## Why

The v1 worker protocol and launcher are deliberately processing-specific, so reusing them for Lookup and cache work would either duplicate lifecycle machinery or introduce unsafe untyped payloads. Block 47 must establish a backward-compatible typed job foundation while preserving every existing processing behavior and identity invariant.

## What Changes

- Add a v2 worker-job protocol alongside the frozen v1 processing protocol; keep v1 readable and launchable during migration rather than reinterpreting its closed vocabulary.
- Preserve block 18's exact sole private worker argument `--internal-worker`. Select v2 only through the child-process environment entry `IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION=2`, set or removed explicitly by the controller command builder; absence selects legacy v1. The entry is internal transport metadata, never public deployment configuration, AppConfig, or UI.
- Generalize launcher/session/cancellation/failure-classification seams around one job identity, with the existing processing run ID reused as that job ID rather than creating a second identifier.
- Define a closed job-kind discriminator, typed request/result/event variants, common ready/log/activity/terminal/error contracts, and a DI handler registry. No generic object, dictionary, arbitrary JSON, or JSON-element payload is permitted.
- Implement only the `ProcessAssets` adapter/handler path in this change, with parity to v1 processing. Reserve extension points for typed `CoordinateLookup` and `CacheMutation` variants without implementing the jobs owned by blocks 48 and 51.
- Expose immutable job/arbitration metadata for the later admission coordinator without implementing block 50's arbitration policy.
- Preserve the managed worker exit-code taxonomy and classifier finality rules across protocol versions.

## Capabilities

### New Capabilities
- `worker-job-envelope`: A versioned, discriminated, strongly typed worker-job contract and reusable lifecycle/session behavior that preserves v1 processing compatibility.

### Modified Capabilities
- None.

## Impact

Worker protocol codecs and goldens, child-process environment descriptor construction, launcher/session/cancellation/classifier abstractions, processing event bridging, worker composition/DI registration, and worker process fixtures. The private role parser keeps its finalized sole argument unchanged. Blocks 48, 50, and 51 consume the new seams but remain out of scope.
