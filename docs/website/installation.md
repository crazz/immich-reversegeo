---
description: "Install Immich ReverseGeo alongside Immich with separate settings and data volumes and private UI access."
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

## Choose your Compose setup

=== "Existing Immich stack (recommended)"

    Add the service and volume declarations below to your existing Immich Compose file. Reuse its `.env` file and default network. Keep `/config` and `/data` persisted separately.

    ```yaml title="docker-compose.yml"
    --8<-- "docker-compose.yml"
    ```

    Start only Immich ReverseGeo:

    ```bash
    docker compose up -d immich-reversegeo
    ```

=== "Separate Compose stack"

    Save the same service snippet in a separate directory and provide an `.env` file with your Immich database connection values. Join the existing Immich network by adding this section to that Compose file:

    ```yaml
    networks:
      default:
        external: true
        name: immich_default
    ```

    Replace `immich_default` with your actual Immich network name. The database hostname must be reachable on that network. You can inspect available networks with `docker network ls`.

    ```bash
    docker compose up -d immich-reversegeo
    ```

Reference file: [docker-compose.yml](https://github.com/immich-reversegeo/immich-reversegeo/blob/master/docker-compose.yml).

Both setups need the database connection values from Immich:

```env
DB_HOSTNAME=database
DB_PORT=5432
DB_USERNAME=postgres
DB_PASSWORD=...
DB_DATABASE_NAME=immich
```

Use `DB_HOSTNAME` from Immich's `.env` file, including when connecting to an external database. Existing `DB_HOST` settings remain supported and take precedence if both are set. If neither has a nonblank value, the host defaults to `database`.

The example publishes the UI on host port `8080`. Change the left side of the port mapping if it is already in use. Do not expose the UI publicly; see the private-access options below.

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
