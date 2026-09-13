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

The reference below uses **Standard**, with a Web UI and your saved schedule. Compare [Deployment Modes](./deployment-modes.md) before choosing Web-only or the optional Run-once service.

- Add these services to your existing Immich Compose file and reuse its database `.env`.
- Keep both named volumes: `/config` stores settings and `/data` stores geographic caches and runtime state.
- Services in the same Compose project use its existing network. With a separate project, explicitly join the database's network and use its reachable database hostname.
- The Web port is bound to localhost. Use a private access path for a remote host; see [VPS and firewall notes](#vps-and-firewall-notes).

This example uses `ghcr.io/immich-reversegeo/immich-reversegeo:latest` and the image's normal entrypoint. Its source is the reference Compose file from the same documentation revision:

```yaml title="docker-compose.yml"
--8<-- "docker-compose.yml"
```

Your shared `.env` must supply the existing Immich database values, for example `DB_HOST=database`, `DB_PORT=5432`, `DB_USERNAME`, `DB_PASSWORD`, and `DB_DATABASE_NAME`. Use your actual database service name and credentials. Do not store credentials in `settings.json`.

Start the Web service:

```sh
docker compose up -d immich-reversegeo
```

Only an absent `IMMICH_REVERSEGEO_MODE` selects Standard by default. Do not add an empty value to `.env`; empty, whitespace-only, padded, case-varied, or unknown values stop startup with exit `2`.

### Web-only variation

Uncomment this entry in the Web service's `environment` list:

```yaml
- IMMICH_REVERSEGEO_MODE=web-only
```

Keep its image, port `8080`, database environment, network, and separate mounts. Recreate it with `docker compose up -d immich-reversegeo` so the new environment takes effect. The same UI, manual processing, Lookup, and heavy Data actions remain available; the internal scheduler is absent and saved schedule values remain unchanged. Mode is read only at startup and is not a Settings option.

## Optional Run-once job

The reference includes `immich-reversegeo-run-once` beside the Web service. It uses the same image, database environment, network, and persistent config/data volumes, with exact `IMMICH_REVERSEGEO_MODE=run-once`, no `ports`, and `restart: "no"`. No entrypoint or command override is needed. Its `run-once` profile prevents ordinary Compose startup from launching it.

Start one disposable attempt from the Compose directory:

```sh
docker compose run --rm immich-reversegeo-run-once
```

Cron or another external scheduler can run that same command. Run-once has no Web listener or child worker, makes one authoritative attempt in its own process without a scheduled precheck, waits for cleanup, and exits. Configure processing settings through a Web mode before using the job. Web-only is useful when the UI remains available while an external scheduler owns cadence.

Use the [Run-once exit table](./deployment-modes.md#run-once-exit-codes) for automation; logs are for human inspection. Busy exit `3` means no pass ran. There is no internal retry or automatic replacement. Already committed asset updates, skipped records, and published caches remain after cancellation or failure. Avoid running maintenance and an independent writer concurrently; see [multiple-container limits](./architecture.md#worker-jobs-and-multiple-containers).

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
