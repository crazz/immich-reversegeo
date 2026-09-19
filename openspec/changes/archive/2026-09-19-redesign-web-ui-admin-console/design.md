## Context

See `proposal.md` for motivation and `specs/ui/admin-console/spec.md` for the behavior contract. A design artifact is required because layout, navigation, presentation, and Appearance persistence cross every Web page.

The Web console today is an indigo-night lounge: `MainLayout.razor` supplies a sidebar plus footer, `NavMenu.razor` mixes nested destinations with a worker summary in the brand block, and `wwwroot/app.css` owns all presentation. `AppConfig` persists schedule and processing through `ConfigService` / `settings.json` and has no Appearance field. `/data` is a hub that links to reset and administrative caches and also hosts skip-list maintenance. City matching is a dedicated page plus a nested Settings launch card.

This change supersedes unpublished `redesign-web-ui-presentation`. Do not treat that graphite restyle as the visual target.

## Goals / Non-Goals

**Goals:**

- Implement the selected approach: tokenized Light/Dark CSS, existing layout/config, no new UI library.
- Separate chrome (top bar, grouped rail, document theme) from existing page handlers and worker/status services.
- Persist Appearance with the other operator settings and apply it immediately on successful save.
- Keep existing destination URLs working; add Skip list at `/data/skip-list` and redirect `/data` there.

**Non-Goals:**

- New processing, lookup, cache, reset, or worker behavior.
- A component library, second stylesheet file, second settings store, theme switch beyond Light/Dark/Auto, or Immich-synced theme.
- Rewriting operational copy, confirmations, or licence text.
- Implementing the unpublished graphite presentation contract.

## Decisions

### 1. Tokenized CSS and existing layout, not swapped files or a component library

Keep all presentation in `wwwroot/app.css` using Light/Dark tokens. Resolve the palette onto `html` as `data-theme=light|dark`. Auto is a saved mode, not a third palette: while Auto is selected, a `matchMedia` listener updates `data-theme` from the browser scheme without writing `settings.json`. If no browser scheme signal exists, resolve Auto to Light so the document always has a concrete palette.

Retain existing brand artwork (logo mark and favicon). Drop the Syne/Nunito network font request and use a system sans-serif stack. Do not add font or icon downloads. Seed colors from Google Admin (light canvas, white surfaces, blue actions; dark charcoal with the same blue role), then measure normal enabled text at 4.5:1 against its surface. Seed values are not an exemption from that bar. If any nonessential motion is added, `prefers-reduced-motion` suppresses it.

Rejected: swapping `light.css` / `dark.css` (duplicated rules, load races). Rejected: MudBlazor or similar (new UI framework, out of proposal scope).

### 2. Console chrome in existing layout components

```
Browser
  html data-theme = light | dark     (resolved palette)
  matchMedia listener when Appearance is Auto

MainLayout
  top bar: brand + worker summary
  grouped rail: Work / Configure / Library
  page header region: destination title + that page's existing primary action when it exists
  page body
  licence footer

Reading and keyboard order follows that chrome: top bar, rail, page header region, remaining page body, footer.

Appearance
  AppConfig.Appearance.Mode = light | dark | auto
  stored in the existing settings.json via ConfigService
  applied to the document from layout as soon as it loads or changes

Pages
  existing Razor pages stay
  Data hub becomes a /data → Skip list redirect
  Skip list gets its own route/page
```

| Unit | Responsibility |
|---|---|
| MainLayout | Top bar, rail slot, page header region (title + primary action), body, footer |
| NavMenu | Grouped destinations only (no worker summary) |
| Header worker summary | Same worker snapshot the sidebar uses today |
| Settings Appearance field | Light / Dark / Auto; save immediately through ConfigService |
| AppConfig.Appearance.Mode | Persisted with schedule/processing |
| Appearance applier | Resolve Auto vs explicit mode onto html data-theme; subscribe to matchMedia while Auto |
| Data.razor | Redirect to Skip list |
| Skip list page | Existing skip-list UI moved off the hub |
| app.css | Light/Dark tokens and console treatments |

Do not introduce a general theme framework, component library, or second settings store. Worker summary reads the existing worker-status snapshot. Overview keeps the full Service Status card. Settings MUST NOT keep the nested City matching launch card.

Visible titles change; URLs stay: `/`, `/lookup`, `/logs`, `/settings`, `/city-resolver`, `/data/geoboundaries`, `/data/reset-geo-data`. Skip list is `/data/skip-list`. `/data` redirects there.

At narrow widths, stack the grouped rail above the page. Do not add a menu button.

### 3. Appearance flows through ConfigService, with a document-level snapshot for live apply

```
load settings.json
        │
        ▼
AppConfig.Appearance.Mode  (missing → Auto)
        │
        ▼
resolve palette: Light/Dark explicit, or browser scheme if Auto
        │
        ▼
set html data-theme

operator changes Appearance
        │
        ▼
SaveConfigAsync (Appearance only path, not Save All Settings)
        │
   ┌────┴────┐
   ▼         ▼
 success    failure
 apply new   keep last saved mode
 mode        show Settings error
             do not preview

browser scheme changes while Auto
        │
        ▼
re-resolve data-theme only (do not write settings.json)
```

ConfigService stays the persisted source of truth. The appearance applier holds the in-Web snapshot that sets `data-theme` so header and pages update without a full reload. Skip-list, cache, reset, lookup, and processing data flows stay as they are; only routes and chrome change. Appearance is stored as a sibling of Schedule and Processing on `AppConfig`, not inside Processing.

Reconnect, error, and not-found inherit the document palette. Do not add a second theme path. Theme resolve and `matchMedia` are document-local and must not touch workers or geodata. Appearance is not a secret; no new auth.

### 4. Failure handling stays local to Appearance and navigation chrome

- Appearance save fails: keep last saved mode (Auto if none), show the existing Settings error treatment, do not apply the unsaved choice.
- Auto with no browser scheme signal: resolve to Light.
- `/data` redirect uses the existing Blazor navigation path to Skip list; a failed navigation still must not render the old Data hub.
- Processing, lookup, cache, reset, and skip-list failures stay on their existing page controllers; this change does not add retries or alter those messages.

### 5. Testing extends existing Web rendering tests and adds Appearance cases

Keep MSTest and the agent test runner. No new test framework and no CSS screenshot snapshot framework.

- Extend existing Web rendering/navigation tests for grouped destinations, new titles, Settings without a nested City matching card, `/data` → Skip list, and Skip list actions.
- Add focused tests for Appearance: default Auto, persist with AppConfig, apply without Save All Settings, revert on save failure, Auto follows a scheme change, missing value → Auto, first-visit save failure keeps Auto.
- Reuse existing Dashboard/Lookup/Settings/cache/reset/log behavior tests; retarget helpers that find controls by old titles or DOM position.
- Use browser checks for Light/Dark contrast and the 390×844 stacked rail.
- Do not run Reset, cache deletion, or processing against production Immich for visual checks.

### 6. Rollout is additive settings plus a bookmark redirect

- Missing Appearance in existing `settings.json` means Auto. No rewrite of other settings.
- Old `/data` bookmarks land on Skip list.
- No Immich database or cache migration.
- Rollback is the previous application image. An older image ignores the new Appearance field.
- Do not ship both this contract and unpublished `redesign-web-ui-presentation`.
- Public using-the-app docs and changelog update with the new names and Appearance in the same implementation change.
- Implementation stays on a dedicated apply pass; unrelated local work stays out of the PR.

## Risks / Trade-offs

- Shared CSS and layout affect every page, including reconnect and error → verify all main routes in Light and Dark, plus stacked narrow nav.
- Immediate Appearance save is a second persistence path beside Save All Settings → keep it Appearance-only so schedule/processing dirty state is unchanged; test save failure does not preview.
- Auto plus `matchMedia` can fight an explicit Light/Dark choice if the listener is not gated → subscribe only while saved mode is Auto.
- Moving skip-list markup to a new page can break rendering tests that assume `/data` content → retarget those tests to `/data/skip-list` and assert `/data` redirects.
- Removing the Settings City matching card is a control-inventory change → required by the spec so City matching is rail-only; update Settings rendering tests.
- Token contrast can fail after a palette tweak → measure Light and Dark enabled text at 4.5:1; do not ship unmeasured seed colors.
- Header worker summary plus Overview Service Status can look duplicated if the header grows too detailed → keep the header as a short summary only.
