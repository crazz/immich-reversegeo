---
icon: material/update
---

# Upgrading and Rollback

Upgrade the complete Immich ReverseGeo image while keeping the existing `/config` and `/data` volumes. The worker architecture uses the same image for the Web service and its temporary workers. It introduces no Immich schema migration or required conversion of saved ReverseGeo configuration.

Administrative cache search indexes are added automatically when needed, using an offline temporary copy. Allow room for one extra country cache plus its index; no cache reset or Compose change is required. Source data and download dates are preserved. The preceding image can still read the original tables in an indexed cache. See [local cache preparation](./using-the-app.md#administrative-cache-inventory) for fallback behavior when preparation is unavailable.

!!! info "Choose a release that includes these changes"
    The new architecture and deployment modes are currently listed under [Unreleased](./changelog.md#unreleased). Do not assume `latest` already includes them. Use the image reference supplied with the release you intend to install, and keep its version or digest alongside your Compose file.

## 1. Record the current setup

Before changing anything, save the current image version or digest, Compose file, database environment, volume names and saved schedule. Keep credentials private. Record the previous complete image so you can select it again if needed; a mutable tag alone does not identify that previous image.

Confirm which mode you want after the update:

- **Standard:** Web UI with your saved built-in schedule.
- **Web-only:** the same UI and manual controls, with internal scheduling disabled.
- **Run-once:** one processing attempt started by an external scheduler, then exit.

For a controlled first check of the new image, select Web-only. It leaves your saved schedule unchanged and prevents the built-in scheduler from starting processing before you inspect the UI.

## 2. Stop work and back up

1. Pause any external scheduler and stop accepting manual jobs. In Standard, disable automatic processing in Settings if you will keep Standard during validation; record its previous value.
2. Let the active operation finish, or request Stop/Cancel and wait for cleanup. Saved location updates remain saved after cancellation.
3. Stop the Web service from your existing Compose directory:

    ```sh
    docker compose stop immich-reversegeo
    ```

4. Ensure any separately launched Run-once job and other writers have also stopped. Stopping the Web service does not stop a separate job container.
5. Back up `/config` and `/data` using your normal stopped-volume backup or snapshot procedure. Include `skipped.db`; `/data` is not only downloadable geographic data. Follow Immich's [database backup guidance](https://docs.immich.app/administration/backup-and-restore/) for the affected Immich data.

Keep the existing volume names and Compose project identity. Changing the project name or starting from another Compose directory can select new empty named volumes instead of the ones you backed up. Do not remove volumes as part of an image update.

## 3. Replace the image and recreate

Set the chosen image version or digest in your Compose file. Use the same image reference for the Web and optional Run-once services. Preserve the separate mounts, database environment, network and `stop_grace_period: 40s` from [Installation](./installation.md).

If validating with Web-only, set the Web service's environment to exactly `IMMICH_REVERSEGEO_MODE=web-only`. Leave the optional Run-once service at `run-once`, with no published port and `restart: "no"`.

Pull and start only the Web service:

```sh
docker compose pull immich-reversegeo
docker compose up -d immich-reversegeo
docker compose logs --tail 100 immich-reversegeo
```

Use the complete image with its normal entrypoint. Recreating the service applies the new image and environment; restarting an existing container alone does not replace its environment.

## 4. Validate before resuming schedules

1. Open the Dashboard. Confirm the deployment mode, scheduler availability and worker status.
2. Check that Settings still shows the expected processing values and that database access works.
3. Open Administrative Areas and check the expected caches and their storage status.
4. Use Lookup for familiar coordinates, including an airport or optional GADM case if those features matter to your library. Lookup previews results without writing asset metadata; it may prepare a country cache.
5. When ready to allow writes, start a manual pass and inspect its result in Immich. Batch size limits each portion of work, not the entire pass. Use an isolated test library if you need a strictly bounded write trial.
6. Check Logs and wait for the worker and cleanup to finish. Investigate a retained Failed status before retrying.

Resume the saved schedule only after these checks. If you used Web-only for validation, keep it for external scheduling or switch back to Standard and recreate the service. A saved enabled schedule becomes active again in Standard. Before resuming an external scheduler, run one [Run-once attempt](./installation.md#optional-run-once-job) and inspect its [exit code](./deployment-modes.md#run-once-exit-codes).

## 5. Roll back if needed

Pause schedules and stop active work using the same procedure above. Stop the upgraded Web service and any separate jobs before selecting the recorded previous complete image.

Restore the previous image reference for every service that can run processing, together with its compatible Compose configuration. Remove deployment-mode settings or optional services that the older release does not support. An older image may not honor Web-only, so also ensure its saved automatic schedule is disabled before starting it for inspection.

Use the existing volumes only when the previous release supports their contents. Newer settings, cache formats or data created after the update may require compatible backups. Check the release's compatibility notes; the bounded migration checks do not establish compatibility for every image and volume combination.

Start the previous Web image, repeat the UI and Lookup checks, and then restore the intended schedule. Image rollback **does not undo committed Immich location updates**. Restoring those values requires an appropriate database recovery procedure and may affect other Immich changes made since the backup. Keep the backup and post-failure logs until recovery is verified.

For startup failures, missing worker files, Busy responses or memory pressure, use [Troubleshooting](./troubleshooting.md).
