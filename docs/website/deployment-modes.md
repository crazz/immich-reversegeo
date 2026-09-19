---
icon: material/server-outline
---

# Deployment Modes

Choose **Standard** for a Web interface with built-in scheduling, **Web-only** for an interface with manual control, or **Run-once** when an external scheduler should start each processing attempt. All three use the same Immich ReverseGeo image and separate persistent `/config` and `/data` volumes.

| Mode | Environment value | Web UI | Built-in schedule | Manual and heavy UI actions | Processing |
|---|---|---|---|---|---|
| Standard | Omit the variable, or `standard` | Container port `8080` | Uses your saved schedule | Available | Temporary workers |
| Web-only | `web-only` | Container port `8080` | None | Available | Temporary workers |
| Run-once | `run-once` | None | None | No UI | One attempt in the invoking process, then exit |

In both Web modes, Overview processing, coordinate Lookup, and cache download/export/refresh use temporary workers. Inventory reads, coordinated cache deletion, and database maintenance stay in the Web service. Web-only keeps your saved schedule visible and editable; it becomes active again when you return to Standard.

## Select a mode

Set `IMMICH_REVERSEGEO_MODE` in the container environment. Only the exact lowercase values `standard`, `web-only`, and `run-once` are accepted. **Only an absent variable defaults to Standard.** Empty, whitespace-only, padded, case-varied, and unknown values stop startup with exit `2`.

Mode is read once at startup. It is not saved in `settings.json` or editable through Settings. After changing the Compose environment, recreate the Web service so its next startup sees the new value:

```sh
docker compose up -d immich-reversegeo
```

`docker compose restart` alone keeps the existing container environment. Check Overview's read-only deployment-mode value after recreation.

## Docker and Compose

Use `ghcr.io/crazz/immich-reversegeo:latest` with its normal entrypoint. Follow the complete [Installation example](./installation.md#preferred-setup), which reuses your Immich Compose project, database `.env`, network, and distinct persistent volumes. Choose an image release that includes these modes; see the [changelog](./changelog.md).

- **Standard:** leave the mode variable absent. Publish container port `8080` only to a local or trusted host address.
- **Web-only:** keep the same service, port and mounts, and add `IMMICH_REVERSEGEO_MODE=web-only` to its environment. Manual processing, Lookup and heavy Area caches actions remain available.
- **Run-once:** use the dedicated `immich-reversegeo-run-once` service in the example. It sets `IMMICH_REVERSEGEO_MODE=run-once`, has no published ports, and uses `restart: "no"`. Its optional profile keeps an ordinary `docker compose up` from starting a processing attempt.

Start one disposable attempt from the directory containing that Compose file:

```sh
docker compose run --rm immich-reversegeo-run-once
```

Cron or another scheduler can invoke this same command. Run-once reads the saved processing settings, starts no HTTP listener or child worker, performs one authoritative attempt without a preliminary scheduled-work check, waits for cleanup, and exits. It does not make a second pass or retry internally. Use Web-only when retaining a UI alongside external scheduling, and avoid concurrent maintenance of shared data as described under [multiple containers](./architecture.md#worker-jobs-and-multiple-containers).

### Move from built-in to external scheduling

1. Pause new scheduled work and let any active pass finish. Change the Web service to `web-only`, recreate it, and confirm that Overview reports internal scheduling disabled.
2. Save the desired processing and source settings through that Web UI. Keep the Run-once service on the same image, database environment and config/data volumes.
3. Run the command above manually once. Inspect its exit code and console output before connecting it to automation; one attempt can process many batches. Its output belongs to that job container and does not populate the separate Web service's Logs page.
4. Configure the external scheduler to run from the intended Compose directory, retain output and exit status, and decide its own retry/backoff policy. A Busy result means no pass ran, not successful processing.

Pause that scheduler before cache or database maintenance. Changing the Web mode or stopping the Web service does not stop a separately running Run-once container. Use [Upgrading and Rollback](./upgrading.md) when changing image versions.

## Worker status and recovery

**Contract-verified:** Overview reports these processing-worker states:

| State | Meaning |
|---|---|
| Idle | No active processing worker and no retained worker failure. |
| Starting | A processing request is preparing its worker. |
| Running | The admitted worker is executing the request. |
| Cancelling | Stop was requested; final work and cleanup are still settling. |
| Failed | A failure is retained for inspection; this does not mean a worker is still alive. |

Cancellation requests a cooperative stop first. If the worker does not stop within the bounded grace period, the app escalates to stopping its process tree. Native work can delay cooperation. Already committed location updates, skipped-item records, and published cache replacements remain in place.

A startup failure, crash, communication failure, or forced stop ends that request. The app does not replace its worker, replay the request, or retry it automatically. A future Standard schedule occurrence makes a fresh eligibility check.

Before retrying:

1. Read Overview or the action result and the related Logs entries.
2. Check the reported cause, such as unavailable database access, storage permissions, or insufficient host memory.
3. Verify that the previous worker has ended and cleanup has completed. `Failed` can remain visible after cleanup; a terminal result alone is not proof of completed cleanup.
4. Correct the cause, inspect any saved effects, then explicitly start a new manual or external attempt. Keep other writers stopped during cache deletion or database resets.

See [Using the App](./using-the-app.md) for action-specific behavior and [Troubleshooting](./troubleshooting.md#a-run-ended-unexpectedly) for diagnostics.

## Run-once exit codes

**Contract-verified:** automation should use the process exit code. Progress and failure logs are human-readable; their wording is not an automation interface.

| Code | Meaning |
|---|---|
| `0` | Completed, including no eligible work. |
| `2` | Invalid invocation or deployment mode; startup did not proceed. |
| `3` | Busy: another processing pass holds the database advisory lock. No processing pass ran. |
| `4` | The processing attempt encountered a domain failure. |
| `5` | Required startup, configuration, dependency, data, database, lock, lifecycle, or cleanup infrastructure failed. |
| `130` | Orderly cancellation during shutdown. |

Abrupt operating-system termination can produce a platform-specific status. A cleanup failure can determine the final managed exit even after work has finished. Busy/`3` does not trigger an application retry; any later launch or backoff belongs to your scheduler. Failed or cancelled attempts do not undo committed writes.

## Startup, memory, and disk activity

**Environment-dependent guidance:** starting a worker and preparing its first country cache add latency. Country size, enabled sources, cache state, disk throughput, parallelism, and host load affect both duration and memory use. Measure your own deployment during representative work.

**Contract-verified:** heavy geographic datasets belong to disposable workers in Standard and Web-only. Process exit releases that process's memory; the long-lived Web service does not retain those datasets between jobs. Run-once owns heavy work in its own process and releases it when that process exits.

This ownership model supplies no universal RAM requirement, RSS ceiling, memory-growth threshold, absolute peak, or total process-tree/container/cgroup memory guarantee. A sampled worker maximum is only an observation of that worker; an unavailable sample does not mean zero usage.

## NAS and HDD scheduling

**Environment-dependent guidance:** use the existing enabled/disabled schedule setting and hourly, every-few-minutes, every-few-hours, daily, weekly, or custom-cron choices. Daily, weekly or custom schedules can move work away from backups, disk scrubs, and media scans. Compare actual disk activity and completion time before increasing frequency or parallelism.

**Contract-verified:** each due Standard check asks whether any currently eligible asset exists, using the full current-eligibility `EXISTS` query. An empty result can still require a broad database scan; the check has no fixed cost guarantee. The app has no persisted watermark, incremental tail, separate reconciliation cadence, or NAS-specific control. Disable scheduling for manual use, select Web-only to suppress the internal scheduler, or use external Run-once cadence.

Test representative coordinates in [Lookup](./using-the-app.md#lookup) before bulk processing. If considering optional GADM, remember that it is limited to academic and other non-commercial use; review the [data-source and license guidance](./data-sources.md#optional-gadm-administrative-data) before enabling it.

## What has been verified

**Production-image tested:** a deterministic Docker fixture checks the built production image and unchanged entrypoint, non-root execution, separate writable mounts, Standard HTTP readiness and scheduled workers, Web-only HTTP readiness without scheduled work, Run-once with no listener and one no-work attempt exiting `0`, invalid-mode exit `2`, and owned-resource cleanup.

**Contract-verified** labels describe additional composition, state, cancellation and exit behavior checked through source and targeted tests. **Environment-dependent guidance** describes choices that need measurement on your host. The small Docker fixture and repeated-worker checks do not establish full-library capacity or certify every image previously published under a mutable tag.
