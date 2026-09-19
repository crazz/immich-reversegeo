## Why

Immich ReverseGeo's Web UI still looks like an indigo lounge template, and its sidebar mixes daily work, configuration, and destructive maintenance so operators cannot tell which destinations are safe, frequent, or advanced. Skip list stays buried on a Data hub that is mostly a directory, City Resolver stays hidden under Settings, and worker status is duplicated in the brand block. Operators also need Light, Dark, and Auto appearance so the console can follow the browser or stay on an explicit palette instead of a single purple theme. A presentation-only restyle would leave the menu problems in place.

## What Changes

- Give the existing Web UI a Google Admin-style console: a compact top bar, a grouped left rail, page title plus primary action, and light and dark palettes instead of the indigo lounge look. This change supersedes the unpublished `redesign-web-ui-presentation` graphite restyle; do not treat that earlier contract as the visual target.
- Add one Appearance setting on Settings: Light, Dark, or Auto. Persist it with the other operator settings. Default Auto. Auto follows the browser color scheme.
- Group destinations by operator job and use these names (old → new):
  - Work: Dashboard → Overview; Lookup stays Lookup; Logs stays Logs
  - Configure: Settings stays Settings; City Resolver → City matching
  - Library: Administrative Areas → Area caches; Reset Immich Geo Data → Reset locations; Skip list (today only on the Data hub) → Skip list as its own destination
- Retire the Data hub as a landing page. Keep `/data` as a bookmark redirect to Skip list, because that is the only unique content that lived on the hub. Reset locations and Area caches already have their own destinations.
- Show one worker status summary in the header. Keep the full Service Status detail on Overview.
- Preserve every existing control, confirmation, busy/disabled rule, validation, result field, licence text, and processing/lookup/maintenance behavior. This change rearranges and restyles the console; it does not add maps, charts, or new geodata/processing actions.
- Update public using-the-app documentation and related UI illustrations so the published workflow matches the new grouping, destination names, and Appearance setting.

## Capabilities

### New Capabilities

- `ui/admin-console`: Operator-facing Web console information architecture, Google Admin-style light/dark presentation, Appearance preference (Light / Dark / Auto), Data hub retirement with `/data` redirect to Skip list, destination renaming mapped above, and content/interaction parity for all existing destinations.

### Modified Capabilities

None. Existing processing, worker status, lookup, cache, reset, skip-list, and configuration requirements remain authoritative. This change specifies how those existing operations are grouped and presented, not new processing or geodata behavior.

## Impact

- Affects the Web layout, navigation, page chrome, shared presentation, Settings persistence for Appearance, and the Data hub route (`/data` redirects to Skip list).
- Does not change resolver logic, Immich database writes, worker protocol, deployment modes, or public APIs.
- Rendering and navigation tests must cover the new grouping and names, `/data` redirect to Skip list, Appearance persistence, and Light/Dark/Auto (including browser-followed Auto).
- Public docs and changelog need the new destination names and Appearance setting. In-app operational copy, confirmations, and licence text stay complete.
