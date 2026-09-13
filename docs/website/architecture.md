---
icon: material/compass-outline
---

# Architecture

<div class="section-intro">
You do not need to understand the internals to use Immich ReverseGeo, but the overall shape is simple: the app reads coordinates from immich, matches them against built-in and downloaded location data, and writes the final place names back.
</div>

For a plain-language explanation of the active geographic data sources, see [Data Sources](./data-sources.md).

Choose how the service runs in [Deployment Modes](./deployment-modes.md). Standard and Web-only keep a Web service available; Run-once performs one processing attempt without a Web listener and exits.

## Main components

<div class="feature-grid">
  <div class="card">
    <h3>Web app</h3>
    <p>The web UI handles settings, manual processing, download management, lookups, and logs.</p>
  </div>
  <div class="card">
    <h3>Built-in data</h3>
    <p>Country matching and airport matching are available right away from data shipped with the app.</p>
  </div>
  <div class="card">
    <h3>Downloaded country data</h3>
    <p>Extra per-country data is downloaded when needed so state and city matching can be more precise.</p>
  </div>
</div>

### Processing pipeline

An admitted processing attempt:

1. reads unprocessed assets from immich
2. resolves bundled country
3. resolves administrative areas from cached Overture divisions and optionally cached GADM country packages
4. optionally queries bundled airport infrastructure
5. writes city/state/country back to immich when a complete result is available

### Worker jobs and multiple containers

In Standard and Web-only, heavy jobs such as asset processing, coordinate Lookup, and cache download/export/refresh run in temporary worker processes. Administrative cache deletion and database maintenance run in the Web process with local coordination. A busy request is rejected immediately and can be tried again after the active operation finishes.

The Web interface does not keep large geographic datasets in memory. Processing, Lookup, and cache downloads load them in worker processes, and that memory is reclaimed when each worker exits. The Data page reads cache summaries without loading those datasets.

Run-once performs one attempt directly in its invoking process and exits after cleanup. It has no Web service, child worker, or internal retry. The process ownership boundary does not guarantee a particular total memory usage; [startup and memory guidance](./deployment-modes.md#startup-memory-and-disk-activity) explains the measurement limits.

This slot is local to each web container. If you run multiple web containers, an operation in one container can overlap processing, Lookup, cache refresh, cache deletion, or database reset in another container. Asset processing also keeps its PostgreSQL advisory lock, which prevents two processing workers from updating Immich at the same time across containers; database resets do not broaden that processing-only lock. Run one interactive web container with no independent writer if you need strict exclusion while changing shared caches, Immich location fields, or the skip list.

### Active data sources

<div class="feature-grid">
  <div class="card">
    <h3>Built in</h3>
    <p>Country matching data and airport matching data are included in the app image.</p>
  </div>
  <div class="card">
    <h3>Downloaded on demand</h3>
    <p>Country-specific Overture and optional GADM data is downloaded locally when needed for better state and city matching.</p>
  </div>
  <div class="card">
    <h3>Optional live lookup help</h3>
    <p>The Lookup page can also show live Overture Places diagnostics when you want to inspect a point more deeply.</p>
  </div>
</div>

### Legacy project

Older geoBoundaries, static-airport, and Foursquare code is preserved in the legacy project but is no longer part of the active runtime path.
