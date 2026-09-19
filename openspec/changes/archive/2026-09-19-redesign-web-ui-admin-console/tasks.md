## 1. Grouped console chrome

- [x] 1.1 Present Work, Configure, and Library destinations in the specified order with Overview, Lookup, Logs, Settings, City matching, Area caches, Reset locations, and Skip list, without a Data hub landing item, keeping existing destination URLs while titles change.
- [x] 1.2 Put each destination title and its existing primary action in the page header region, and show one ProcessAssets worker status summary in the top bar for idle, running, and failed states while Overview keeps the full Service Status detail.
- [x] 1.3 Reach City matching from the Configure group only, with no nested City matching launch card on Settings.
- [x] 1.4 Stack the grouped rail above the page at narrow width so every destination stays visible without a menu button, with keyboard order following top bar, rail, page header, body, then footer.

## 2. Skip list destination

- [x] 2.1 Open Skip list at `/data/skip-list` with the existing skip-list count, Clear Skip List action, and busy, result, and reload-error states.
- [x] 2.2 Redirect `/data` to Skip list so the Data hub landing page is not shown and Area caches and Reset locations stay on their existing paths.

## 3. Appearance preference

- [x] 3.1 Store Appearance as `AppConfig.Appearance.Mode` with the other operator settings, defaulting missing values to Auto.
- [x] 3.2 Apply Light, Dark, and Auto on load and immediately on change without Save All Settings, persist on success, keep Auto following the browser scheme live, and resolve Auto to Light when no browser scheme signal exists.
- [x] 3.3 On Appearance save failure, keep the last saved mode or Auto when none was saved, show an error, and do not preview the unsaved choice.

## 4. Console presentation and operator guidance

- [x] 4.1 Apply Light and Dark Google Admin tokens in the shared stylesheet, drop the Syne/Nunito network fonts, keep existing brand artwork, distinguish primary/secondary/destructive/disabled/hover/focus controls in both palettes, keep status text with color, preserve live-region/alert/label/dialog semantics, honor reduced motion, and meet 4.5:1 enabled text contrast, including error, not-found, and reconnect.
- [x] 4.2 Restyle remaining page bodies without changing operational copy, confirmations, or processing/lookup/maintenance behavior.
- [x] 4.3 Update public using-the-app guidance and changelog so published workflow names, Skip list, `/data` redirect, and Appearance match the console operators see.
