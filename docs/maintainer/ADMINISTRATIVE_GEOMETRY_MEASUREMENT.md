# Administrative geometry measurement

Use an isolated database seeded from a retained fixture and owned cache copies. Do not run write-path comparisons against a production Immich database. Compare complete published images with the same source settings, cache checksums, asset order, resource limits and database reset. Keep private coordinates, identifiers, database exports and raw logs in ignored `_out/` evidence.

## Timing and memory

Run fresh processes against warm disk caches, with at least three interleaved repetitions per image. Attach no allocation probe or profiler to timing runs. Compare every persisted city/state/country value, including nulls, before interpreting speed. Record image IDs, runtime/GC version, fixture and cache hashes, CPU/RAM/swap limits, start/finish timestamps and exit/OOM status.

Sample process RSS and cgroup CPU, memory peak, swap and OOM counters separately at least once per second. A process sample, a cgroup peak and managed heap size are different scopes. Missing counters are unavailable, never zero. For the fixed 50-USA acceptance fixture, the candidate median must be at most half the reference median; peak memory may not increase beyond the larger of 5% or 64 MiB. The fixed container allowance is 2 CPU and 4 GiB. Replay the full retained corpus through both images with identical pinned caches before claiming geographic equivalence.

## Separate allocation diagnostic

The optional startup hook is outside the solution and application publish graph. Default and Integration test runs never activate it; Performance tests remain excluded by their normal run settings. Build explicitly:

```sh
dotnet build tests/ImmichReverseGeo.AllocationProbe/ImmichReverseGeo.AllocationProbe.csproj \
  -c Release -o _out/allocation-probe
```

For a separate diagnostic run, mount that output read-only at `/measurement` in the disposable test container and set:

```text
DOTNET_STARTUP_HOOKS=/measurement/ImmichReverseGeo.AllocationProbe.dll
```

The hook emits one `SPATIAL_ALLOCATION_MEASUREMENT` JSON record at orderly process exit. Its process-wide managed allocation counter includes application startup, async work and cleanup, from after hook setup before Main to the ProcessExit callback. Hook setup and report serialization are excluded; native allocations are outside this counter. Divide the allocation delta by the independently verified completed asset count. Do not interpret cumulative allocated bytes as simultaneous RAM use. An abruptly terminated process may emit no record; that measurement is unavailable.

GC collection counts and cumulative GC pause seconds cover the diagnostic process interval. Report them separately from elapsed time and sampled memory. Use exactly the same hook binary and scope for both images, and never use these instrumented durations as the unprofiled timing result.

## Accounting and compatibility

The shared owner admits retained geometry plus temporary preparation before loading WKB. Retained charge is `16 * WKB bytes + 2 MiB`; temporary charge is `12 * WKB bytes + 64 KiB`, with checked arithmetic. The fixed floor conservatively covers small-geometry and object/index overhead observed during calibration. These estimates are admission accounting, not an RSS ceiling or a proof for every polygon shape.

The budget uses one quarter of a finite container memory limit when available, otherwise the runtime's available-memory estimate; it is capped at 1 GiB and falls back to 128 MiB. The container limit takes precedence because the runtime may report a smaller managed-heap allowance under that limit. Country/airport indexes, database buffers, native libraries, filesystem cache and unretained evaluation also consume memory. Large geometry can use the original predicate under the single build/evaluation permit after unused entries are evicted.

File identity includes source, country, full path, size and timestamps. A read transaction keeps candidate metadata and deferred blobs in one snapshot; retirement prevents late publication after an observed replacement. This is not a distributed lock or protection against arbitrary external writers that preserve the same file metadata. Use the application's coordinated cache maintenance.

Eligible valid polygons use prepared Covers with the existing distance fallback. Invalid, unsupported and oversized shapes keep compatible reference evaluation; geometry is never repaired, simplified or filtered by administrative level for performance. Roll back with the preceding complete image and the same compatible config/data volumes. This optimization introduces no persistent format migration.
