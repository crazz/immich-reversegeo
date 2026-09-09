---
icon: material/application-outline
---

# Using the App

This page covers the day-to-day UI for Immich ReverseGeo after setup is complete.

For install-time settings such as database values, schedules, and processing limits, see [Configuration](./configuration.md).
For a source-by-source explanation of the geographic data behind the app, see [Data Sources](./data-sources.md).

## Dashboard

Use `Run Now` on the Dashboard to start a manual processing pass immediately.

- it works even if automatic scheduling is turned off
- it uses your current Settings values for batch size, delay, parallelism, and airport matching
- the Dashboard shows live progress, recent activity, and the last completed run
- `Stop` requests cancellation of the current run; `Stopping…` remains visible while it finishes and releases resources

Wait for the run to finish before starting another pass. Work that does not observe cancellation, such as a synchronous native operation, can take longer to stop. Stopping does not undo location updates already written.

Each processing run uses a temporary worker started from the same Immich ReverseGeo application image. The Dashboard and Logs continue to show the run while that worker is active.

The Service Status card stays visible while database statistics load or when the database is unavailable. It shows:

- **Deployment mode:** `Standard` or `Web-only`
- **Internal scheduling:** whether this Web host permits the built-in scheduler; your saved schedule still controls whether Standard actually runs it
- **ProcessAssets worker:** `Idle`, `Starting`, `Running`, `Cancelling`, or `Failed`

Web-only disables the built-in scheduler without changing your saved schedule, and manual Dashboard runs remain available. Run-once starts no Web UI, so it has no Service Status card.

`Failed` remains visible so an unexpected worker failure does not immediately look idle. Open Logs for the recorded processing details. The next worker start clears the retained failure; restarting the Web host creates a fresh `Idle` status.

## Lookup

Use the Lookup page when you want to test a coordinate before running a full processing pass.

- paste or type coordinates to see how the resolver behaves for that point
- it shows the country match, cached administrative area match, optional airport match, and final values that would be written
- `Include bundled airport infrastructure lookup` lets you test with or without airport matching for that lookup
- `Include live Overture Places lookup` adds an extra live place search for debugging, but it is slower and not needed for normal use
- `Prefer cached GADM administrative areas` switches the administrative area part of the lookup to an experimental on-demand GADM cache for that country; country detection still starts with the bundled Overture country data

Lookup starts a temporary isolated worker in both Standard and Web-only mode. While it is checking availability, starting, or running, the coordinate and source options stay locked. The page shows the current lookup step and any active cache preparation. `Cancel` requests a stop and remains in `Cancelling…` until the worker has exited and its output has finished draining.

If another lookup already owns the temporary worker slot, the page reports that it is busy and starts no second worker. If the worker cannot start or stops without a valid result, Lookup shows a short safe failure message. It does not switch to an in-process resolver. You can retry after the existing job has finished or after correcting the worker installation problem.

Lookup is always a preview. It may download or read geographic caches, but it does not update an Immich asset or write the displayed city, state, or country to `asset_exif`.

## Data tools

The Data area contains maintenance tools that change downloaded caches or Immich reverse geo values.

| Page or action | What it does |
|---|---|
| `Clear Skip List` | Removes permanently skipped assets so they can be retried on the next run. |
| `Reset All Data` | Clears Immich reverse geo `city`, `state`, and `country` values for all matching assets and clears the skip list. |
| `Reset Single Item(s)` | Clears reverse geo values only for the pasted asset GUIDs and removes those assets from the skip list. |
| `Reset Specific Locations` | Finds assets by an existing city, state, or country value and clears all three reverse geo fields for those matching assets. |
| `Delete Overture cache` | Removes one downloaded Overture country cache file. |
| `Re-download Overture cache` | Replaces one downloaded Overture country cache with a fresh copy. |
| `Delete All Overture Divisions` | Removes every downloaded country cache so they will be fetched again on demand later. |
| `Delete GADM cache` | Removes one downloaded GADM country cache file. |
| `Re-download GADM cache` | Replaces one downloaded GADM country cache with a fresh copy. |
| `Delete All GADM Caches` | Removes every downloaded GADM cache so they will be fetched again on demand later. |

### Re-downloading an administrative cache

`Re-download` starts a temporary worker that builds and validates a fresh cache before replacing the current one. The page shows named steps such as downloading, exporting, validating, and publishing when they apply. It also shows one `Cancel` action while the refresh can be cancelled. Cache controls stay disabled until the worker has finished and cleanup is complete.

Cache refreshes share one heavy-work slot with processing and Lookup in each running Immich ReverseGeo Web process. If the page reports `Busy`, wait for the active operation to finish, then select `Re-download` again. If it reports `Unavailable`, resolve the displayed worker availability problem and retry. Refresh requests are not queued or replayed automatically.

If download, export, validation, or publication preparation fails, the existing valid cache remains available. Cancelling before publication also keeps that cache. If cancellation arrives just after a validated replacement was published, the refresh can still end as cancelled while the new valid cache appears after the page reloads its actual status. A cancelled attempt is never reported as successful.

The Reset Immich Geo Data page only clears reverse geo values in `asset_exif`. It does not touch any other Immich metadata.

## Logs

Use the Logs page when you want to inspect recent activity outside the Dashboard summary.

- filter the in-app log view to all messages, warnings, or errors
- download the current filtered view as `immich-reversegeo.log`
