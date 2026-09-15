# FORK for my experiments and bugfixing. AI assistance is used. If you dont know what you do - use original repo!
## Architecture and performance compared with upstream

This fork builds on [Immich ReverseGeo upstream](https://github.com/immich-reversegeo/immich-reversegeo). It separates the Web UI from geographic processing: temporary Worker processes handle processing, coordinate lookups and cache refreshes, then exit to release their process memory. Web and Workers run inside the same Docker container.

| Area | Upstream | This fork |
|---|---|---|
| Default process model | Web UI and geographic processing share one persistent process | Persistent Web UI with a temporary Worker for each heavy job |
| Memory after a job | Managed within the persistent Web process; allocations can remain after processing | The operating system reclaims the Worker's process memory when it exits |
| Administrative polygon lookups | Candidate polygons are decoded again for each lookup | Geometry is reused within a memory budget; disk indexes narrow polygon searches |
| Processing with GADM enabled | Queries both Overture and GADM | Queries the preferred source first; the secondary source fills missing city or state fields |

GADM remains optional and uses non-commercial data; users control source priority. Large polygons can use compact storage, selected by size and memory cost across all countries.

### Measured on a UGREEN DXP4800 PLUS NAS

| Metric | Upstream | This fork | Observed change |
|---|---:|---:|---:|
| Process and write 800 assets across 8 countries | 930.30 s | 39.29 s | **23.7× faster** |
| Process and write 208 GBR assets | 708.12 s | 7.99 s | **88.6× faster** |
| Process and write 50 USA assets | 170.99 s | 4.68 s | **36.5× faster** |
| Process memory after the GBR run | 2,933 MiB | 148 MiB | **95.0% lower** |
| Process memory after the USA run | 3,107 MiB | 139 MiB | **95.5% lower** |

Location results matched in all **1,058 row comparisons** across these samples.

Memory figures are process RSS measured 20–30 seconds after completion. Docker's displayed usage also includes filesystem cache, which can remain after a Worker exits.

<details>
<summary>Benchmark conditions and limitations</summary>

- Tested on 2026-09-15 using the published Linux amd64 images: upstream revision [5186001](https://github.com/immich-reversegeo/immich-reversegeo/commit/51860018406fda9b323db06f336509fd7e9b8cfa) and this fork's [10a09ee](https://github.com/crazz/immich-reversegeo/commit/10a09eea733bc4cefad421147c1072bb65d3f5c6) (`ghcr.io/crazz/immich-reversegeo:sha-10a09ee`).
- Each app had 2 CPU, 4 GiB RAM and 8 GiB combined RAM+swap. Both used the same coordinates, copies of the same downloaded administrative caches and an isolated test PostgreSQL instance. No production data was modified.
- Settings: GADM enabled, Overture preferred, airport matching enabled, batch size 50, batch delay 100 ms and parallelism 4.
- Times run from the scheduled processing start to the last database write, including Worker startup. Web startup, schedule waiting and cleanup after the last write are excluded. Identical database triggers recorded write timestamps.
- One processing pass per image and sample, with fresh application containers. The fork's first mixed run includes local index preparation. No downloads were timed; the NAS filesystem cache was not globally cleared.
- Bundled country data differs: upstream uses Overture March 2026 data; this fork uses August 2026 data. Airport data is identical. These numbers compare the published versions on these samples; they are not a universal speedup guarantee. Samples can overlap.
- Upstream completed all 800 mixed-sample writes, then stopped because the test's annual cron exceeded its scheduler's timer range. Its write timing is retained; post-run memory for that sample is omitted. Subsequent runs used a daily schedule.

</details>

See the [architecture guide](https://github.com/crazz/immich-reversegeo/blob/master/docs/website/architecture.md) for the process model and deployment options.

# ORIGINAL description
# Immich ReverseGeo

Immich ReverseGeo is a self-hosted companion service for [immich](https://immich.app) that improves the accuracy and usefulness of reverse-geocoded location names for photo assets.

The new architecture separates the long-lived Web UI from memory-heavy work. Web handles controls, scheduling and progress; temporary Worker processes load geographic data for processing, lookups and cache refreshes inside the same container. Each Worker exits after its job, allowing the operating system to reclaim its process memory and keeping those datasets out of Web between jobs. Peak container memory still depends on the workload and filesystem cache. See the [architecture guide](./docs/website/architecture.md) for details.

It is built for people who already have GPS coordinates on their assets and want a local, repeatable way to write better `city`, `state`, and `country` values back into immich than the built-in basic reverse-geocoding flow typically provides.

Immich ReverseGeo is an independent project and is not affiliated with immich. immich is a great product, and this project is built to work alongside it.

## What It Does

- Reads unprocessed immich assets that already have latitude and longitude
- Resolves better country, state, and city names
- Uses built-in airport data to improve results around airports and terminals
- Writes improved location names back into immich
- Includes a local web UI for setup, lookups, downloads, and operations

## Important Notes

- Immich ReverseGeo needs direct access to the same PostgreSQL database your immich instance uses.
- Disable immich's own reverse geocoding before using Immich ReverseGeo, otherwise both systems can overwrite the same location fields and fight each other. immich docs:
  [Reverse Geocoding](https://docs.immich.app/features/reverse-geocoding/) and
  [Reverse Geocoding Settings](https://docs.immich.app/administration/system-settings/#reverse-geocoding-settings).
- It writes location fields back into immich, so you should take a database backup first.
- The Reset Geo Data page under Data can reset reverse geo `city`, `state`, and `country` values for all assets, specific asset GUIDs, or a selected city, state, or country before reprocessing. It only clears those reverse geo fields.
- The app needs internet access the first time it downloads extra location data for a country.
- Large downloaded country data can take a lot of disk space. Bigger countries can approach `~500 MB` each, and multiple countries can grow into many gigabytes on disk.
- If you rebuild or switch containers and see antiforgery or key-ring errors in the browser, restart with the same persisted `/config` volume and reload the page. You may need to clear old browser cookies once after changing setups.
- Do not expose the web UI publicly. On a VPS or other internet-facing host, bind the published port to localhost or use SSH forwarding, a VPN, or a trusted reverse proxy with authentication.

## Documentation

- Documentation website: [crazz.github.io/immich-reversegeo](https://crazz.github.io/immich-reversegeo/)
- This revision: [Getting Started](./docs/website/getting-started.md), [Deployment Modes](./docs/website/deployment-modes.md), [Using the App](./docs/website/using-the-app.md), and [Upgrading and Rollback](./docs/website/upgrading.md).
- [Architecture overview](./docs/website/architecture.md): how the Web service, temporary workers and persistent storage fit together.
- Contributors: [local setup](./CONTRIBUTING.md) and [maintainer documentation](./docs/maintainer/README.md).

The worker architecture and new deployment modes are currently [Unreleased](./docs/website/changelog.md#unreleased). Documentation in this revision can describe features not yet present in a published image.

## Docker

The app expects:

- database connection values from environment variables
- persistent app data under `/data`
- persistent settings under `/config`

End users should be able to start the published image with:

```bash
docker compose up -d
```

using the provided `docker-compose.yml` as a service block inside their existing Immich compose file and the environment values already used for their Immich setup.

See the [installation guide](https://crazz.github.io/immich-reversegeo/installation/).

## Contributing

Contributor and local-development workflows live in [CONTRIBUTING.md](./CONTRIBUTING.md).

## Changelog

Technical release notes live in [CHANGELOG.md](./CHANGELOG.md).

## License

Immich ReverseGeo is licensed under the GNU Affero General Public License v3.0.

See [LICENSE](./LICENSE).
