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

The reference Compose file omits `IMMICH_REVERSEGEO_MODE`, which selects the compatible Standard default. Standard runs the Web app and schedule, and starts temporary child workers for accepted processing runs. If you need the Web interface and manual runs without the internal scheduler, set `IMMICH_REVERSEGEO_MODE=web-only`. Saved schedule values stay in Settings but remain inactive until you return to Standard mode.

Use one exact lowercase value: `standard`, `web-only`, or `run-once`. This is an environment setting, not a `settings.json` option, so restart the container after changing it.

- Add the service to your existing Immich `docker-compose.yml`.
- Reuse the same `.env` file that already contains your Immich database settings.
- Keep `/config` and `/data` persisted with Docker volumes.
- No extra Docker network configuration is needed when the service lives in the same compose project as Immich.

Reference file:
[docker-compose.yml](https://github.com/immich-reversegeo/immich-reversegeo/blob/master/docker-compose.yml)

Copy/paste snippet:

```yaml title="docker-compose.yml"
--8<-- "https://raw.githubusercontent.com/immich-reversegeo/immich-reversegeo/master/docker-compose.yml"
```

This service expects:

- the same database connection values Immich already uses
- to run in the same compose project and Docker network as Immich
- a persistent `/config` volume for settings
- a persistent `/data` volume for downloaded Overture data and runtime state
- a free host port for the web UI, with `8080` as the default example

Typical variables come from the shared `.env` file:

```env
DB_HOST=database
DB_PORT=5432
DB_USERNAME=postgres
DB_PASSWORD=...
DB_DATABASE_NAME=immich
DATA_DIR=/data
CONFIG_DIR=/config
```

Then start the stack:

```bash
docker compose up -d
```

## Optional Run-once job

Run-once is intended for cron or another external scheduler. It starts no Web server, makes one processing attempt in the current process, waits for cleanup, and exits. Add this optional service beside the persistent service in the same Immich Compose file:

```yaml title="docker-compose.yml"
services:
  immich-reversegeo-run-once:
    image: ghcr.io/immich-reversegeo/immich-reversegeo:latest
    pull_policy: always
    profiles: ["run-once"]
    volumes:
      - reversegeo-config:/config
      - reversegeo-data:/data
    env_file:
      - .env
    environment:
      - IMMICH_REVERSEGEO_MODE=run-once
    stop_grace_period: 40s
    restart: "no"
```

This uses the image's normal entrypoint, the existing Immich database settings and Compose network, and the same separate config and data volumes. It publishes no port. Start one disposable attempt with:

```bash
docker compose run --rm immich-reversegeo-run-once
```

A cron entry can change to the directory containing the Compose file and run that same command. Each launch creates one new attempt. Immich ReverseGeo does not retry, replay, roll back, or start a replacement process. If another deployment owns the database processing lock, the job exits immediately with code `3`; a later invocation is a new decision by your scheduler.

Use the exit code as the automation result:

| Code | Meaning |
| --- | --- |
| `0` | Completed, including when there was no eligible work |
| `2` | Invalid deployment mode or reserved startup syntax |
| `3` | Another Immich ReverseGeo deployment is already processing this database |
| `4` | The processing attempt failed |
| `5` | Required configuration, data, database, lock, lifecycle, or cleanup infrastructure failed |
| `130` | The attempt was cooperatively cancelled during shutdown |

The job writes ordinary readable progress to standard output and warnings or failures to standard error. Log text is for operators; the exit code is the stable automation contract. A failed or cancelled attempt can leave already committed asset updates and skipped-item records in place, so inspect the result before choosing whether to launch another attempt.

## Stopping or updating the service

Keep `stop_grace_period: 40s` on the Immich ReverseGeo service in your Compose file. During shutdown, the app rejects new processing runs, requests cancellation of active work, and waits for process and output cleanup. The setting gives the app time to finish before Docker forces the container to stop.

Use `docker compose stop immich-reversegeo` for a planned stop. For an update, pull the new image and recreate the service with `docker compose up -d immich-reversegeo`. A forced stop or power loss can still interrupt processing; after restarting, check the Dashboard and logs before starting another run.

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
