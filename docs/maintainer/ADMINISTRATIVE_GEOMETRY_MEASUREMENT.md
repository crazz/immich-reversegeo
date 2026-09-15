# Administrative geometry measurement

Use an isolated database seeded from a retained fixture and owned cache copies. Do not run write-path comparisons against a production Immich database. Compare complete published images with the same source settings, cache checksums, asset order, resource limits and database reset. Keep private coordinates, identifiers, database exports and raw logs in ignored `_out/` evidence.

## Timing and memory

Run fresh processes against warm disk caches, with at least three interleaved repetitions per image. Attach no allocation probe or profiler to timing runs. Compare every persisted city/state/country value, including nulls, before interpreting speed. Record image IDs, runtime/GC version, fixture and cache hashes, CPU/RAM/swap limits, start/finish timestamps and exit/OOM status.

Sample process RSS and cgroup CPU, memory peak, swap and OOM counters separately at least once per second. A process sample, a cgroup peak and managed heap size are different scopes. Missing counters are unavailable, never zero. Declare the reference release and numeric gates before comparison: earlier optimization ratios are not an acceptance baseline for later releases. Bounded fixture equivalence must not be described as full-library equivalence.

For adaptive geometry acceptance, the reference is `sha-3a7df81` (digest `sha256:28f7e99a141167a387ccacdb3c71ad34529ceebd09d818127b4f3626e871e090`). The profile is the DXP4800 PLUS NAS, Linux amd64, .NET 10 / NTS 2.6.0, 2 CPU and 4 GiB container memory, with matching GC and settings. Compare three interleaved pairs on the retained 208-row GBR fixture and the existing 50-row USA fixture. GBR median end-to-end time must be at most half the reference and container peak no higher; USA median time and peak must each remain within 10% of this reference. Require zero persisted-result differences and report OOM, swap and memory-pressure observations. Country names identify test fixtures only.

The large-polygon evaluator profile separately requires at most 64 MiB retained managed heap after positive containment and at most 128 MiB after exterior/tolerance queries. Its warm positive median must be at most 25 ms and at least ten times faster than repeated evaluation in the reference's default-budget cache. The exterior/tolerance workload must stay within 10% of the reference median. Retained measurements exclude the same preexisting input/point buffers and keep the geometry reachable through collection; they are not worker or container memory figures. Report cold construction independently. Include synthetic size/ring-count cases, country-label invariance, boundaries and mixed-source cache pressure. Full production-library testing remains separate and user-owned.

The reproducible synthetic matrix uses up to 2.2 million vertices, up to 100,000 holes, and two simultaneously evaluated compact entries from different sources. Select it explicitly:

```sh
npm run agent:test -- --performance \
  --project tests/ImmichReverseGeo.Spatial.Tests/ImmichReverseGeo.Spatial.Tests.csproj \
  --filter 'FullyQualifiedName~CompactGeometryMemoryTests|FullyQualifiedName~CompactConcurrentMemoryTests'
```

It checks full-predicate compatibility and records managed allocation/retention and timing observations. Its unprofiled numbers are diagnostic; the NAS numeric gates require the separate matched reference/candidate measurements above. Ordinary test runs exclude this matrix.

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

The shared owner selects by WKB byte length and budget before loading the blob. Prepared mode is eligible through 32 MiB WKB when its reservation fits an empty budget. Larger shapes, or shapes whose prepared reservation cannot fit, try compact mode. Country/source identifiers and query coordinates never select the representation. Actual occupancy controls admission and eviction, not the size policy.

Costs use checked arithmetic, with WKB length denoted by `B`:

| Representation | Retained geometry | Reserved evaluation workspace | Temporary construction |
|---|---:|---:|---:|
| Prepared | `16B + 2 MiB` | Included in existing retained headroom | `12B + 64 KiB` |
| Compact | `6B + 2 MiB` | `4B + 64 KiB` | `14B + 64 KiB` |

Admission reserves all three columns. Publication releases construction space; compact evaluation workspace stays charged for the entry's lifetime, including retirement while leased. Compact queries serialize per entry through a cancellation-aware gate, so distance materialization cannot multiply that allowance for simultaneous queries of the same shape. Prepared hits retain their concurrency. The fixed floors and factors cover coordinate/ring overhead, validation and possible coordinate-array materialization conservatively; these remain admission estimates, not an RSS ceiling or a proof for every possible shape.

The budget uses one quarter of a finite container memory limit when available, otherwise the runtime's available-memory estimate; it is capped at 1 GiB and falls back to 128 MiB. The container limit takes precedence because the runtime may report a smaller managed-heap allowance under that limit. Country/airport indexes, database buffers, native libraries, filesystem cache and unretained evaluation also consume memory. If neither representation fits even an empty budget, reject admission before eviction and use the compatible unretained path under the existing single build/evaluation permit. Its transient memory is outside the cache budget guarantee.

Statistics preserve `Preparations` as retained constructions across representations; `CompactConstructions` identifies the compact subset. `CompactHits` counts reused compact leases, `IntrinsicRejections` distinguishes impossible admission from ordinary pressure, and `CompactRetainedBytes` / `CompactWorkspaceBytes` include retired entries until the final lease releases. These are local diagnostics, not new worker protocol fields.

File identity includes source, country, full path, size and timestamps. A read transaction keeps candidate metadata and deferred blobs in one snapshot; retirement prevents late publication after an observed replacement. This is not a distributed lock or protection against arbitrary external writers that preserve the same file metadata. Use the application's coordinated cache maintenance.

Eligible valid polygons use prepared Covers or packed-double simple area location with the existing inclusive distance fallback (`<= 0.00015` coordinate units). Compact storage does not create a prepared segment index. Invalid, empty, unsupported and unadmitted shapes keep compatible reference evaluation; ineligible compact candidates are not retained under a compact charge. Geometry is never repaired, simplified or filtered by administrative level for performance. Roll back with the preceding complete image and the same compatible config/data volumes. This optimization introduces no persistent format migration.
