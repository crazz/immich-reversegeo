---
icon: material/wrench-outline
---

# Troubleshooting

<div class="section-intro">
Most issues come down to one of three things: the app cannot see the right database values, the app is still downloading country data, or the running process has stale state after a change.
</div>

## The app shows default DB values instead of my real ones

The app reads the container's environment. Check the database variables in your Immich Compose `.env`, then recreate the service with `docker compose up -d immich-reversegeo`. Restarting the existing container alone does not replace its environment.

## Invalid mode or an unexpected Web listener

Use exactly `standard`, `web-only`, or `run-once` for `IMMICH_REVERSEGEO_MODE`. Only absence defaults to Standard; empty, whitespace-only, padded, case-varied and unknown values fail with exit `2`. Correct the Compose environment and recreate the service.

Standard and Web-only serve container port `8080`. Run-once has no HTTP listener and exits after its one attempt, so a browser connection is not a readiness check for it. Use the [dedicated no-port Run-once service](./installation.md#optional-run-once-job) and inspect its exit code. Confirm that you installed a release containing these modes.

## Config or data mounts are not writable

Keep distinct persistent mounts at `/config` and `/data`. Check that both mount paths are correct, writable, have free space, and allow the image's declared non-root user to read and write them. A readable settings file alone does not prove the cache directory is writable. Correct ownership or access for that user on the mounted roots, then recreate the service and inspect Logs. Keep database credentials in the environment rather than `settings.json`.

## Country lookup says no match

Check:

- the bundled `overture-country-divisions.db` file exists in the bundled data directory inside the app image
- the running app was restarted after data or code changes
- the coordinate actually has latitude and longitude in immich

## A country cache download is slow

The first lookup for a new country can take longer because the per-country Overture division cache must be created locally.

Large countries can also produce much larger cache files than small ones.

<div class="feature-grid">
  <div class="card">
    <h3>First run is slower</h3>
    <p>The app has to download and prepare country data the first time you hit a country it has not seen before.</p>
  </div>
  <div class="card">
    <h3>Bigger countries take longer</h3>
    <p>Large countries usually mean larger downloads and more time spent preparing the local data.</p>
  </div>
</div>

## Lookup is busy or its worker is unavailable

Lookup uses an isolated worker in both Standard and Web-only mode. A busy message means processing, Lookup, cache refresh, or coordinated maintenance owns the local work slot. Wait until that operation and its cleanup finish, then try again. Repeated clicks do not queue extra work.

An unavailable or failed message means the isolated worker could not start or did not return a valid final result. Check the container logs, confirm the complete application image was installed, and recreate the service if files were copied or upgraded separately. Lookup does not fall back to loading the geographic resolvers inside the Web service.

If you leave the page or stop the service during a lookup, the app requests cancellation and waits for the owned worker to exit. A completed result only becomes available after the worker output has finished. Lookup remains read-only throughout this cleanup and does not change Immich asset metadata.

## Processing seems slower than expected

Things that affect throughput:

- batch size
- max parallelism
- database latency to the immich PostgreSQL instance
- first-time per-country cache creation

<div class="step-grid">
  <div class="step-card">
    <h3>Start with the easy checks</h3>
    <p>If processing feels slow, first check whether the app is still downloading country data for the first time.</p>
  </div>
  <div class="step-card">
    <h3>Then tune settings</h3>
    <p>Batch size and max parallelism usually make the biggest difference once the needed country data is already present.</p>
  </div>
</div>

## A run ended unexpectedly

Read the message in Logs and check the container's available memory and application installation. A failed or cancelled run keeps location changes that were already saved. Wait until the app shows that the run has finished before starting another run manually.

The Dashboard may retain `Failed` after the worker has released its resources. The short message in Service Status comes from a fixed, safe failure category; it intentionally omits raw worker output, process values, exception details, and environment values. Use Logs for the recorded operator-facing details. A database-stat refresh or an automatic check that finds no work does not clear this status. The next actual worker start clears it, and restarting the Web host returns the status to `Idle`.

If a cleanup or communication warning appears after a completed result, the recorded result stays unchanged. The warning does not undo saved changes. Do not paste raw worker output, database credentials, or connection strings into a support report.

Container logs include lifecycle entries for processing, Lookup, and cache workers. Use the same `job_id` to follow a worker from launch through its final `WorkerJobProcessClassified` entry. That final entry follows process exit and output cleanup, so a terminal result alone does not mean cleanup has finished. A role's stopping entry records why shutdown began; its stopped entry can still report a later cleanup failure.

The reported working-set maximum is a periodic sample of that child process only. It is not total container memory or a guaranteed operating-system peak. An unavailable sample means the app could not measure it, not that the worker used zero memory.

For memory pressure, compare the container host's observations during representative work, check whether a first-country download was active, and review your parallelism and disk activity. There is no universal RAM threshold for every country and host. See [startup, memory, and disk activity](./deployment-modes.md#startup-memory-and-disk-activity).

## The service fails to start after an update

Immich ReverseGeo verifies the files needed to start its processing worker when the service starts. Pull or rebuild the complete application image, then recreate the service. Do not copy only the application DLL into an existing container or volume. Check the container logs for the startup message before trying to process assets.

Standard and Web-only processing runs in temporary workers; Run-once performs its single attempt directly and serves no Web UI. If an upgrade introduces a worker problem, keep the safe failure logs and follow [Upgrading and Rollback](./upgrading.md#5-roll-back-if-needed) for the previous complete image and your saved volumes. See [Deployment Modes](./deployment-modes.md) for supported operation and recovery.

## A run says another run is active or its lock connection was lost

If the message says another run is active, wait for that run to finish before trying again. If it says the lock connection was lost, check the connection to the PostgreSQL database and wait until the affected run has finished.

Run-once exit `3` means another processing pass held the database advisory lock and this attempt did no processing. A lock-connection or cleanup infrastructure failure uses exit `5`. Use the [Run-once exit table](./deployment-modes.md#run-once-exit-codes); your external scheduler owns any later retry or backoff.

Start a manual retry only after the earlier run has ended. There is no automatic retry, and location changes that were already saved remain saved.

## I want to rerun everything from scratch

The Data page can clear existing `city`, `state`, and `country` values in immich.

See [Using the App](./using-the-app.md) for the reset options and what each one does.

Because this is a destructive write operation against immich metadata, back up your database first:

- [Backup and Restore | Immich](https://docs.immich.app/administration/backup-and-restore/)
