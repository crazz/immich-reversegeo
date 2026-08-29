## Why

An Overture division cache export writes through a Microsoft.Data.Sqlite connection to a GUID-specific temporary file. The connection currently uses the provider's default pooling and only clears its path-specific pool on success, so a fault after `Open()` can return the native connection to a pool that is never reused and retain native resources until process exit.

## What Changes

- Disable Microsoft.Data.Sqlite pooling only for the one-shot temporary output connection in `ExportOvertureDivisions`.
- Add deterministic regression coverage at the temporary-output boundary: verify the provider-supported `Pooling` setting for repeated unique paths, force a post-open failure, and prove temporary-file cleanup.
- Prove a later controlled export for the same country can validate and publish its cache after the transient fault.
- Keep DuckDB use, cache schema/content, release lookup, successful publication, and in-flight download coordination unchanged.

## Capabilities

### New Capabilities
- `overture/temporary-export-resource-cleanup`: Safe cleanup and recovery for failed temporary Overture division exports.

### Modified Capabilities
- None.

## Impact

- `src/ImmichReverseGeo.Overture/Services/OvertureDivisionCacheService.cs`: the GUID `*.tmp` SQLite output connection and a narrow internal test seam; public constructors and DI behavior remain unchanged.
- `tests/ImmichReverseGeo.Tests/OvertureDivisionCacheServiceTests.cs`: repeated post-open failure, cleanup, configuration, and repaired-retry coverage.
- No dependency, schema, public API, configuration, or data migration changes.
