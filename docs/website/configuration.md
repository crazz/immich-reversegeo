---
icon: material/tune-variant
---

# Configuration

## Deployment mode selection

Immich ReverseGeo reads the optional `IMMICH_REVERSEGEO_MODE` environment variable once when the container starts. Omit it to select the compatible Standard default. The only accepted values are `standard`, `web-only`, and `run-once`; use lowercase exactly as shown. Empty, padded, case-varied, or unknown values stop startup with exit code 2.

The selected value is not saved in `settings.json`. Change it in your Compose environment and recreate the service with `docker compose up -d immich-reversegeo`; a plain container restart does not replace its environment. Standard runs the Web app, manual controls, and internal schedule. Web-only runs the same Web app and keeps manual processing available, but it does not start the internal scheduler. Existing schedule values remain visible and editable in Settings and become active again when you return to Standard mode. See [Deployment Modes](./deployment-modes.md) for the decision table and exact startup rules.

Overview reads this resolved startup mode and shows it as a read-only value. Reloading or reconnecting to the same running Web host shows its current worker status immediately. A host restart resolves the mode again and begins at `Idle`; worker status and retained failures are not saved to configuration or data storage.

Run-once starts no Web server or internal scheduler. It loads the existing settings, makes one globally excluded processing attempt, writes ordinary progress logs, and exits. It does not retry. Use it as a disposable Compose job under cron or another external scheduler; see [Optional Run-once job](./installation.md#optional-run-once-job).

Web-only does not add an automation endpoint. Overview processing, Lookup, and cache download/export/refresh use temporary workers in both Web modes. Cache inventory, coordinated deletion, and database maintenance remain Web control operations. An external scheduler can launch the separate Run-once service.

<div class="section-intro">
The Settings page is intentionally small. Most users only need to check Appearance, the database connection, pick a schedule, and tune how aggressively processing should run. Country-specific city matching lives on its own City matching page in Configure.
</div>

## Saving settings

Settings are saved in three independent groups:

- **Save All Settings** saves the schedule and processing options on Settings.
- **Save City Resolver Settings** saves global and country preferences on City matching.
- **Appearance** saves immediately when you select Light, Dark, or Auto.

Saving one group preserves newer saved values in the other two groups, even from an older open page. If two editors save the same group, the later successful publication to the settings file wins. The order of browser confirmations does not decide which values remain saved; there is no conflict warning between editors of the same group.

If a save fails, Settings and City matching keep your entered values so you can correct them or retry. Appearance keeps its last saved choice. If your browser disconnects before confirming a save, reconnect and reload the relevant page to check its saved values before retrying. A lost confirmation does not undo a completed save.

These guarantees apply to saves through one running Web instance. Coordinate separate writers or manual file edits yourself. Keep the Docker configuration volume and its normal backup workflow described under [Data layout](#data-layout).

## Appearance

Settings includes Appearance with Light, Dark, and Auto. The choice is stored with the other operator settings and applies as soon as it saves. Light and Dark set the console palette directly. Auto follows the browser color scheme and updates live when that scheme changes. Appearance does not require Save All Settings.

For manual runs, coordinate testing, and reset tools, see [Using the App](./using-the-app.md).

## Database settings

Database values are environment-backed and shown as read-only in the UI.

The app reads:

- `DB_HOST`
- `DB_PORT`
- `DB_USERNAME`
- `DB_PASSWORD`
- `DB_DATABASE_NAME`

These values are required because the app reads and updates immich data directly.

Database values are treated literally, including passwords containing semicolons or quotation marks. Preserve those characters when setting your container environment.

<div class="step-grid">
  <div class="step-card">
    <h3>Read-only by design</h3>
    <p>The database values shown in the UI come from the running environment and are there as a sanity check, not for editing.</p>
  </div>
  <div class="step-card">
    <h3>Keep them in Docker or your host environment</h3>
    <p>Set the database values where you launch the app, then recreate the container if you change them.</p>
  </div>
</div>

## Processing settings

The Settings page lets you control:

- whether processing runs automatically
- when it runs, using simple presets or a custom cron expression
- batch size, delay, and parallelism
- whether airport infrastructure can override the city name
- whether GADM administrative areas are enabled as an optional source
- whether GADM should be preferred over cached Overture divisions
- whether split-territory GADM fallback families should be tried
- whether every asset is written to the log

Most users should stay on the preset schedule options. Manual runs from the dashboard still work even when automatic scheduling is disabled. Saved schedules with invalid values stay in the custom editor and are preserved when you save other settings.

Standard supports hourly, every-few-minutes, every-few-hours, daily, weekly, and custom-cron schedules. Each due check asks whether any currently eligible asset exists across the full eligibility range before admitting a worker. It does not count the work or reserve those assets. See [NAS and HDD scheduling](./deployment-modes.md#nas-and-hdd-scheduling) for disk-activity tradeoffs. Web-only ignores the saved schedule while retaining its values.

<div class="feature-grid">
  <div class="card">
    <h3>Schedule</h3>
    <p>Choose when the app should run automatically, from simple presets up to a custom cron schedule.</p>
  </div>
  <div class="card">
    <h3>Batch size</h3>
    <p>Controls how many photos are read in each batch. It does not limit the total number processed by a pass.</p>
  </div>
  <div class="card">
    <h3>Parallelism</h3>
    <p>Controls how much work the app does at once. Higher values can be faster, but they also put more load on the system and database.</p>
  </div>
  <div class="card">
    <h3>Airport matching</h3>
    <p>Leave it on if airport names are useful to you. Turn it off if you prefer commune or city names for photos taken on airport grounds.</p>
  </div>
  <div class="card">
    <h3>Optional GADM source</h3>
    <p>You can enable GADM as an extra administrative boundary source, either as a fallback behind Overture or as the preferred admin source.</p>
  </div>
</div>

### Batch size

**Batch Size must be a positive whole number**, such as the default `50`. Save All Settings rejects zero or negative values, saves none of the submitted edits, and keeps your inputs available for correction.

If an older settings file contains an invalid batch size, Settings shows that value unchanged. Enter a positive replacement, select **Save All Settings**, then reload Settings to confirm it. Appearance and City matching can still save independently while that correction is pending; they preserve the invalid batch size.

A processing pass with eligible assets rejects an invalid saved batch size before fetching or processing any asset batch. Run-once returns configuration-failure exit `5`. A pass with no eligible assets completes successfully without reading processing settings. Each non-empty pass uses one saved settings snapshot, so later saves apply to a subsequent pass.

### GADM administrative areas

When enabled, GADM adds another country-level administrative boundary source for `state` and `city` matching.

The two switches control processing independently:

| Enable GADM | Prefer GADM | Processing order |
|---|---|---|
| Off | Either | Overture only |
| On | Off | Overture first; GADM fills a missing city or state |
| On | On | GADM first; Overture fills a missing city or state |

When the first source supplies both fields, processing skips the second source and its cache preparation. Country detection still uses bundled Overture data. Airport matching runs afterwards. Lookup keeps the source details requested by its own options, even when the preferred source supplies both fields.

GADM is limited to academic and other non-commercial use. Read the [data-source license guidance](./data-sources.md#optional-gadm-administrative-data) before enabling it, and validate a coordinate in Lookup before bulk processing.

Recommended starting point:

- turn `Enable GADM administrative areas` on
- leave `Prefer GADM administrative results` off at first
- use Lookup to compare results before making GADM the primary admin source

What it can help with:

- countries where Overture admin divisions are sparse or pick the wrong city-like name
- places where a more traditional admin-boundary dataset gives better municipality or state names

What it does not fix by itself:

- wrong bundled country detection
- airport names winning when airport matching is still enabled
- missing places that are not present in either source

### Split-territory fallback families

The optional GADM territory fallback setting lets the app try a small related country family when a sovereign or mainland code may be too coarse.

Examples include:

- Denmark, Greenland, and the Faroe Islands
- United Kingdom, Jersey, Guernsey, and the Isle of Man

This is a targeted fallback, not a global scan of every possible territory.

## City matching {#city-resolver}

The City matching page lets you adjust how the app picks a city name when Overture returns several possible matches.

It gives you:

- bundled default rules that ship with the app
- an optional global override
- country-specific overrides
- a searchable country picker
- simple up/down controls to change the order of preferred place types from the official Overture list
- independent tie-break choices: Inherit, Prefer tighter area, or Prefer broader area

This keeps the main Settings page simple while still giving you a way to fix countries where the default city result is not what you want.

### What it actually changes

City matching only affects the `city` value.

It is useful when the app finds the right general area, but chooses the wrong name for your taste. For example:

- it picks a district instead of the wider city
- it picks a very small local area instead of the municipality
- it picks something broader than you want

### What it does not change

City matching does not download better data or invent missing places.

It will not help if:

- the place you want is not in the Lookup results at all
- the airport name is winning and airport matching is still turned on
- the country match is wrong

So before changing anything, use Lookup first and make sure the place you want is actually in the returned data.

### Recommended workflow

1. Open the Lookup page and test a coordinate that gave you a bad city result.
2. Look at `Resolved City`, `Raw Best Area`, and the candidate list.
3. If the place you want is present, decide what to change:
   - if the wrong kind of place won, change the preferred order
   - if two similar places are competing, try broader or tighter matching
   - if an airport name is winning, turn off airport matching first
4. Open City matching and compare the country's **Effective Profile** with the Lookup result before changing preferences. Add a country override only after you know the data supports the result you want.
5. Select **Save City Resolver Settings**, reload City matching to confirm the saved choices, then run Lookup again with the same known coordinate before processing your library.

### About `admin_level`

Lookup shows `admin_level` because it can sometimes help explain why a result was chosen, but it is not a setting you edit directly.

That is because:

- many results do not have a useful `admin_level`
- the place type is usually more important than the number
- `admin_level` alone does not solve every case

So the controls on the City matching page stay simple:

- preferred place type order
- inherited, broader, or tighter matching

### Bundled defaults vs your overrides

The app ships with built-in default rules. Your own settings sit on top of them:

- bundled global default
- bundled country-specific default, if present
- your optional global override
- your optional country override

Each field inherits separately. **Inherit** keeps the tie-break preference from the preceding profiles; **Prefer tighter area** and **Prefer broader area** set an explicit preference, even when you leave the subtype order empty. An empty subtype order inherits the preceding order, so you can change just the tie-break or just the order.

A global subtype-only override leaves each country's bundled tie-break intact. For example, France continues to prefer broader areas unless you explicitly choose a different tie-break globally or for France. An explicit country preference takes priority over an explicit global preference. Select **Inherit** to return a tie-break to the preceding profile; remove all subtype entries to inherit their order again.

The effective profile previews these combined choices. Saving and reloading retains which fields inherit and which are explicit. These preferences select among available administrative data; they cannot create missing geographic coverage.

### Want to improve the bundled defaults for everyone?

If you find a country setting that clearly works better, you can open a pull request.

The bundled defaults currently live in:

- `src/ImmichReverseGeo.Web/bundled-data/defaults/city-resolver-profiles.json`

A good pull request should include:

- the country ISO3 code
- one or more example coordinates
- the old result
- the new expected result
- a short explanation of why the new default is better
- Lookup evidence showing that the desired place is really in the returned data

See the contributor notes in [`CONTRIBUTING.md`](https://github.com/crazz/immich-reversegeo/blob/master/CONTRIBUTING.md#city-resolver-defaults).

## Database connection details

The database section in Settings is read-only and mainly there as a sanity check.

- values are taken from the running process environment
- they are not stored in `settings.json`

## Data layout

Keep separate persistent volumes for configuration and runtime data:

```text
/config/
  settings.json             Saved processing settings and resolver overrides
  dataprotection-keys/       Keys used by the Web interface
/data/
  overture-divisions/        Downloaded Overture country caches: {ISO3}.db
  gadm-divisions/            Optional GADM country caches: {ISO3}.db
  skipped.db                Assets excluded from later processing attempts
```

Country and airport datasets bundled with the image are separate from these downloaded caches. Database credentials come from the environment and are not saved in `settings.json`.

All modes should use the same intended config/data volumes. `/data` contains skipped-asset tracking as well as caches, so preserve it during upgrades. See [Upgrading and Rollback](./upgrading.md) for the stopped-volume backup workflow.

## Operational notes

<div class="feature-grid">
  <div class="card">
    <h3>Turn off immich’s built-in reverse geocoding</h3>
    <p>Only one tool should be updating location names. See immich’s <a href="https://docs.immich.app/administration/system-settings/#reverse-geocoding-settings">Reverse Geocoding Settings</a>.</p>
  </div>
  <div class="card">
    <h3>Geo resets are a real change</h3>
    <p>The Reset locations page under Library can reset reverse geo country, state, and city values for all assets, pasted asset GUIDs, or a selected location value before a rerun, so a database backup is strongly recommended first.</p>
  </div>
  <div class="card">
    <h3>Country downloads take space</h3>
    <p>The app needs internet access when a country is downloaded for the first time, and those downloads can grow over time.</p>
  </div>
</div>

!!! danger "Do not run two reverse-geocoders against the same immich library"
    Immich ReverseGeo should be the only tool updating your immich location fields.

    Turn off immich's built-in reverse geocoding before using this app, otherwise the two systems can step on each other's results.
    See immich's official docs:
    [Reverse Geocoding](https://docs.immich.app/features/reverse-geocoding/)
    and
    [Reverse Geocoding Settings](https://docs.immich.app/administration/system-settings/#reverse-geocoding-settings).

!!! warning "Clearing location data is a real metadata change"
    The Reset locations page does not just reset this app's local state. It can clear existing immich reverse geo `city`, `state`, and `country` fields in the database.

    It does not touch any other immich metadata.

    Take a database backup before using it:
    [Backup and Restore | Immich](https://docs.immich.app/administration/backup-and-restore/)
