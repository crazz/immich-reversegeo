# Release Checklist

## GitHub

- Push `master`
- Confirm CI passes
- Confirm Pages deploy passes
- Confirm the docs site renders correctly
- Confirm the published container image can be pulled and started

## Runtime

- Start the latest image with persistent `/data` and `/config` mounts
- Confirm the published Web artifact contains `ImmichReverseGeo.Web.dll` with its matching `.runtimeconfig.json` and `.deps.json`; the processing worker runs from this same image and assembly
- The temporary explicit in-process processing selection is a source-level rebuild/revert seam only. Do not document or add a runtime toggle; remove the seam in Change 38.
- Verify the web UI loads
- Verify `/healthz` if a health endpoint is available in the release being tested
- Verify existing settings survive a container update

## Reverse Geocoding

- Verify a lookup in a straightforward inland city
- Suggested coordinate: `47.3769, 8.5417` should resolve to Zurich / Switzerland
- Verify a lookup near a major airport
- Suggested coordinate: `47.460972, 8.553525` should resolve to Zurich Airport infrastructure in Switzerland
- Verify a lookup in a coastal or island-heavy country
- Suggested coordinate: `4.2979, 73.0111` should resolve to Maldives with useful Overture division data
- Verify processing writes city/state/country back to Immich as expected
- Verify a country with on-demand Overture division download still works from a clean `/data` directory

## Docs and Metadata

- Final README updates
- Final changelog updates
- Final installation/configuration docs
- Final privacy URL
- Final support URL
- Final terms URL
