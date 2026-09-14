---
icon: material/rocket-launch-outline
---

# Getting Started

This is the fastest end-user path to a first working setup with Docker.

Use Immich ReverseGeo when you want more accurate or more useful location names in immich than the default built-in reverse-geocoding results are giving you.

What is different here is that Immich ReverseGeo uses better location data and more careful matching than immich's built-in reverse geocoding. That usually gives better results for things like coastlines, islands, airport areas, and other places where the default result feels too broad or inaccurate.

!!! danger "Disable immich's built-in reverse geocoding first"
    Immich ReverseGeo and immich's own reverse geocoding both write location fields.

    If both are enabled at the same time, they can overwrite each other's values and cause inconsistent results.

    Turn off immich's built-in reverse geocoding before you start processing with Immich ReverseGeo.
    See immich's official docs:
    [Reverse Geocoding](https://docs.immich.app/features/reverse-geocoding/)
    and
    [Reverse Geocoding Settings](https://docs.immich.app/administration/system-settings/#reverse-geocoding-settings).

!!! warning "Back up immich before processing or clearing location data"
    Immich ReverseGeo writes `city`, `state`, and `country` values back into immich's database.

    Before you run this against real data, or before you use the Data page to clear existing location fields, create a proper immich database backup first.

    Follow the official guide:
    [Backup and Restore | Immich](https://docs.immich.app/administration/backup-and-restore/)

## 1. Make sure you can reach the immich database

Immich ReverseGeo needs direct access to the same database your immich instance uses.

In Docker setups, that usually means:

- joining the same Docker network as immich
- passing the immich database connection values through to this container

Typical variables:

```env
DB_HOST=database
DB_PORT=5432
DB_USERNAME=...
DB_PASSWORD=...
DB_DATABASE_NAME=immich
```

## 2. Start the container

Follow [Installation](./installation.md) for the complete Compose example and choose an image release that includes the [new deployment modes](./deployment-modes.md). For a controlled first setup, use its **Web-only variation**: the UI and manual actions are available, and a saved schedule cannot start processing while you check the results. Switch to **Standard** later if you want built-in scheduling, or use **Run-once** for an external scheduler with no UI.

Use Docker with persistent mounts for:

- `/config` for settings
- `/data` for downloaded country data

Add the `immich-reversegeo` service to your existing Immich Compose file, then start it:

```bash
docker compose up -d immich-reversegeo
```

## 3. Open the UI

Default container URL:

```text
http://localhost:8080
```

If port `8080` is already in use on your host, change the left side of the compose port mapping and open that port instead.

If the container runs on a remote server, do not expose the UI publicly. Docker-published ports can bypass host firewall rules such as UFW. For a VPS, bind the published port to localhost and use SSH local port forwarding, a VPN, or a trusted reverse proxy with authentication:

```bash
ssh -N -L 8080:localhost:8080 user@host
```

## 4. Test one coordinate

Use the Lookup page to confirm the basics before a full run:

<div class="step-grid">
  <div class="step-card">
    <h3>Country matching</h3>
    <p>Make sure the country is detected correctly for a coordinate you know well.</p>
  </div>
  <div class="step-card">
    <h3>State and city matching</h3>
    <p>Check that the result is reasonably precise and not too broad or generic.</p>
  </div>
  <div class="step-card">
    <h3>Airport areas</h3>
    <p>If you have airport photos, confirm they resolve the way you would expect.</p>
  </div>
</div>

## 5. Review settings and run processing

Check Settings before allowing writes: confirm the database, schedule, batch size, parallelism and source choices. Keep automatic scheduling disabled while validating the first results, or start in Web-only when you need the built-in scheduler absent from startup. Optional GADM data is limited to academic and other non-commercial use; read [its license guidance](./data-sources.md#optional-gadm-administrative-data) before enabling it.

Use `Run Now` from the Dashboard when you are ready to update eligible assets. A conservative batch size and parallelism reduce the amount of concurrent work, but **batch size does not cap the entire pass**. A pass can continue through all eligible assets. For a strictly bounded write trial, use an isolated test library. `Stop` requests cancellation; it does not undo updates already saved.

Inspect the resulting location names in Immich and the completion details in Logs. Wait for the active worker and cleanup to finish before starting another operation. The [architecture overview](./architecture.md) explains why a temporary worker appears during this work.

To replace existing location names, first validate the desired result in Lookup, then use the appropriate [Reset Immich Geo Data action](./using-the-app.md#resetting-immich-location-data) after taking a backup. Resetting selected asset GUIDs clears only those records, but it does not restrict the next processing pass to those GUIDs; other eligible assets can also be processed.

## 6. Choose ongoing scheduling

Use Standard and enable a saved schedule when the app should choose when to run. Keep Web-only for manual control or when an external scheduler launches the separate Run-once job. Start with a schedule that suits your database and disks; [NAS and HDD scheduling](./deployment-modes.md#nas-and-hdd-scheduling) explains why even an empty scheduled check can touch the database.

For more on the UI after setup, see [Using the App](./using-the-app.md).
