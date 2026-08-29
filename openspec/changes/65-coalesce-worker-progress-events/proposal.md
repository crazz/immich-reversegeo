## Why

Validated worker streams can outpace Web-state projection and Blazor rendering during bursty processing, Lookup, and cache work. The control plane needs bounded latest-state delivery without weakening protocol validation, losing operator-relevant events, or coupling child-process pipe drainage to UI refresh speed.

## What Changes

- Classify only declared full-state progress payloads as replaceable snapshots; preserve lifecycle, activity, every log level, warning/error, terminal, and capability result events losslessly.
- Add a per-job, sequence-aware bounded coalescing stage after decode and protocol validation, with latest-wins replacement, explicit coalesced-gap evidence, safe backpressure, stale-job rejection, and terminal flush/finality barriers.
- Decouple state mutation from Blazor notification cadence using an injectable clock, a measurement-verified safe default, and immediate final notification at authoritative terminal/failure boundaries.
- Preserve unchanged v1 processing bytes and semantics while applying equivalent behavior to v2 ProcessAssets and descriptor-declared v2 progress snapshots for Lookup and cache jobs.
- Define deterministic burst, real-process, shutdown/disposal, compatibility, and Blazor notification tests, plus an observation seam that block 66 can instrument without changing block 65 semantics.

## Capabilities

### New Capabilities
- `worker-progress-coalescing`: Sequence-aware bounded delivery and rate-controlled UI notification for validated worker-job progress.

### Modified Capabilities
- None.

## Impact

The change is confined to the controller-side accepted-event/bridge/state-notification path and its tests after blocks 15, 16, 21, 27, 44, 47, 49, and 51 are applied. It does not change protocol codecs or bytes, worker emission, processing/job outcomes, log retention limits, public settings, block 64 scheduling, or block 66 telemetry implementation.
