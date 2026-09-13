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
