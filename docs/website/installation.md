---
icon: material/docker
---

# Installation

## Docker

If you already run immich with Docker Compose, the simplest setup is to add one service to that existing compose file.

!!! warning "Database backup strongly recommended"
    This service updates immich metadata in the database.

    Before first use, and especially before using any bulk-clear action from the Data page, create a proper immich database backup.

    Official guide:
    [Backup and Restore | Immich](https://docs.immich.app/administration/backup-and-restore/)

## Preferred setup

- Add the service to your existing Immich `docker-compose.yml`.
- Reuse the same `.env` file that already contains your Immich database settings.
- Keep `/config` and `/data` persisted with Docker volumes.
- No extra Docker network configuration is needed when the service lives in the same compose project as Immich.

Reference file:
[docker-compose.yml](https://github.com/immich-reversegeo/immich-reversegeo/blob/master/docker-compose.yml)

Copy/paste snippet:

```yaml title="docker-compose.yml"
--8<-- "docker-compose.yml"
```

This service expects:

- the same database connection values Immich already uses
- to run in the same compose project and Docker network as Immich
- a persistent `/config` volume for settings
- a persistent `/data` volume for downloaded Overture data and runtime state
- a free host port for the web UI, with `8080` as the default example
- about 2 GB of memory, set by `mem_limit` in the snippet above

!!! note "Why the memory limit is there"
    The bundled country boundary data is held in memory while processing runs and uses several hundred
    megabytes. `mem_limit` sets a total container memory budget that the .NET runtime can take
    into account. Large country caches and high parallelism can increase peak memory use.

    Do not lower it much below `2g`. A limit under the working set makes the container fail
    immediately instead of running. Raising it is fine.

Typical variables come from the shared `.env` file:

```env
DB_HOSTNAME=database
DB_PORT=5432
DB_USERNAME=postgres
DB_PASSWORD=...
DB_DATABASE_NAME=immich
DATA_DIR=/data
CONFIG_DIR=/config
```

Use `DB_HOSTNAME` from Immich's `.env` file, including when connecting to an external database. Existing `DB_HOST` settings remain supported and take precedence if both are set. If neither has a nonblank value, the host defaults to `database`.

Then start the stack:

```bash
docker compose up -d
```

## Image versions

`latest` normally follows GitHub's designated latest stable release after its image is successfully published. Maintainers can explicitly promote another validated build using the manual publishing option. Use a published version tag to control upgrades, for example `ghcr.io/immich-reversegeo/immich-reversegeo:1.2.3`. This is an example; choose an available version from [GitHub Releases](https://github.com/immich-reversegeo/immich-reversegeo/releases).

- Releases tagged `v1.2.3` publish both `1.2.3` and `v1.2.3` image tags.
- Other stable releases retain their version tags without changing `latest`. Rerunning an older release does not automatically move `latest` backwards.
- Prereleases publish their full version tags, such as `1.2.3-rc.1`, without updating `latest`.
- `edge` follows development builds from `master`.
- `sha-...` tags identify builds by commit.

After changing the image version in your Compose file, run `docker compose pull immich-reversegeo` and `docker compose up -d immich-reversegeo`. Keep your existing `/config` and `/data` volumes.

## VPS and firewall notes

Do not expose the web UI publicly. Docker-published ports can bypass host firewall rules such as UFW, so do not rely on the host firewall alone to hide a broadly published `8080:8080` mapping on an internet-facing server.

If you run Immich ReverseGeo on a VPS and only need local or SSH-forwarded access, bind the published port to localhost:

```yaml
ports:
  - "127.0.0.1:8080:8080"
  # - "[::1]:8080:8080"
```

Then use a private path such as SSH local port forwarding, a VPN, or a trusted reverse proxy with authentication:

```bash
ssh -N -L 8080:localhost:8080 user@host
```

Then open:

```text
http://localhost:8080
```

## Runtime notes

- Built-in data covers country matching and airport matching.
- More detailed country data is downloaded only when needed.
- The app needs internet access the first time it downloads data for a new country.
- Large countries can use hundreds of megabytes each under `/data`.

## After install

- Use [Configuration](./configuration.md) for database and processing settings.
- Use [Using the App](./using-the-app.md) for manual runs, coordinate testing, and reset tools.
