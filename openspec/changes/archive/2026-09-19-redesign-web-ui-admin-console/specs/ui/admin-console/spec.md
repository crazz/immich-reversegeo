## Purpose

Give Immich ReverseGeo a Google Admin-style Web console with job-grouped navigation, Light/Dark/Auto appearance, and full parity for existing operator actions.

## ADDED Requirements

### Requirement: Google Admin-style console presentation

The Web interface SHALL present a Google Admin-style console: compact top bar, grouped left rail, page title with the page's primary action when that action exists, and Light and Dark palettes instead of the indigo lounge look. This presentation SHALL cover Overview, Lookup, Logs, Settings, City matching, Area caches, Reset locations, Skip list, shared header, footer, error, not-found, and reconnect surfaces. Existing brand artwork SHALL remain unchanged. The console MUST NOT add maps, charts, extra themes beyond Light and Dark, an Immich-synced theme, or any product control other than Appearance.

#### Scenario: Operator moves between console pages
GIVEN the Web console is showing a covered destination
WHEN the operator opens another covered destination
THEN headings, controls, surfaces, and statuses use the same console treatment
AND Light or Dark appearance remains the active palette
BUT the indigo lounge look is not used

#### Scenario: Page with a primary action is displayed
GIVEN the operator is on Overview, Lookup, Settings, City matching, Logs, Area caches, Reset locations, or Skip list
WHEN the page is displayed
THEN the page title uses the destination name from the grouped navigation requirement
AND the existing primary action for that page is available in the page header region when that action exists

### Requirement: Grouped navigation and destination names

The console SHALL expose these destinations in a grouped left rail, in this order, using these names:

- Work: Overview, Lookup, Logs
- Configure: Settings, City matching
- Library: Area caches, Reset locations, Skip list

Nav labels and page headings SHALL use those names. The mapping from previous titles is Dashboard → Overview, City Resolver → City matching, Administrative Areas → Area caches, Reset Immich Geo Data → Reset locations, and Skip list promoted from the Data hub. Lookup, Logs, and Settings keep those names. Settings MUST NOT keep a nested City matching launch card; City matching is reached from the Configure group.

#### Scenario: Operator scans the rail on a wide screen
GIVEN the console is shown at a width that keeps the rail beside the page
WHEN the operator reads the navigation
THEN Work, Configure, and Library groups are visible
AND Overview, Lookup, Logs, Settings, City matching, Area caches, Reset locations, and Skip list are available as destinations
AND no Data hub destination is presented as a landing page

#### Scenario: Operator opens City matching
GIVEN the console navigation is available
WHEN the operator opens City matching
THEN the City matching page heading is City matching
AND the existing city-resolver controls remain available

#### Scenario: Settings no longer nests City matching
GIVEN Settings is open
WHEN the operator looks for city-matching configuration
THEN City matching is not launched from a nested Settings card
AND City matching remains available from the Configure group

### Requirement: Data hub retirement and skip-list destination

The Data hub SHALL no longer be a landing page. Skip list SHALL be its own Library destination and SHALL present the skip-list count, Clear Skip List action, and existing busy, result, and reload-error states. Opening `/data` SHALL redirect to Skip list. Area caches and Reset locations SHALL remain their own destinations.

#### Scenario: Operator opens a /data bookmark
GIVEN the operator navigates to `/data`
WHEN the console resolves that location
THEN the operator is taken to Skip list
AND the Data hub landing page is not shown

#### Scenario: Operator manages skipped assets from Skip list
GIVEN Skip list is open and skipped assets exist
WHEN the operator uses Clear Skip List
THEN the existing skip-list confirmation, busy, result, and reload-error behavior remains available
AND the action is not hidden behind a Data hub card

### Requirement: Header worker status summary

The top bar SHALL show one worker status summary for the ProcessAssets worker. Overview SHALL keep the full Service Status detail, including deployment mode, internal scheduling explanation, worker state, failure content, and Open Logs. The header MUST NOT duplicate the full Service Status card.

#### Scenario: Worker is idle
GIVEN the ProcessAssets worker is idle
WHEN the operator views any console page
THEN the header shows a worker status summary of Idle
AND Overview still exposes the full Service Status detail

#### Scenario: Worker is running
GIVEN the ProcessAssets worker is running
WHEN the operator views any console page
THEN the header shows a running worker status summary
AND Overview still exposes the full Service Status detail

#### Scenario: Worker failed
GIVEN the ProcessAssets worker is failed
WHEN the operator views Overview
THEN the header shows a failed worker status summary
AND Overview still shows the existing failure content and Open Logs path

### Requirement: Appearance preference

Settings SHALL include Appearance with values Light, Dark, and Auto. Appearance SHALL be stored with the other operator settings. Appearance SHALL default to Auto when no saved value exists. Changing Appearance SHALL apply and persist immediately, independent of Save All Settings. Light SHALL use the light palette. Dark SHALL use the dark palette. Auto SHALL follow the browser color scheme and SHALL update the palette when that scheme changes during the session, without requiring reload. If Appearance cannot be saved, the console SHALL keep the last saved value, or Auto when none was ever saved, MUST NOT preview the unsaved choice, and SHALL show an error.

#### Scenario: First visit uses Auto
GIVEN no Appearance value is saved
WHEN the operator opens the console
THEN Appearance is Auto
AND the palette matches the current browser color scheme

#### Scenario: Operator selects Dark
GIVEN Settings is open and Appearance can be saved
WHEN the operator selects Dark
THEN the dark palette applies immediately
AND Dark remains the saved Appearance after reload
AND that value is stored with the other operator settings
AND Save All Settings is not required for that persistence

#### Scenario: Auto follows a later browser scheme change
GIVEN Appearance is Auto and the browser scheme is light
WHEN the browser scheme changes to dark during the session
THEN the console switches to the dark palette without reload

#### Scenario: Appearance cannot be saved
GIVEN Appearance is Light and saving a new Appearance value fails
WHEN the operator selects Dark
THEN the light palette remains
AND an error is shown
BUT the unsaved Dark choice is not previewed

#### Scenario: First-visit Appearance cannot be saved
GIVEN no Appearance value is saved and saving a new Appearance value fails
WHEN the operator selects Dark
THEN Appearance remains Auto
AND the palette still matches the current browser color scheme
AND an error is shown
BUT the unsaved Dark choice is not previewed

### Requirement: Content and interaction parity

For the same application state, the console SHALL preserve the existing control inventory except for the added Appearance setting, plus existing help text, confirmations, field labels, alerts, result fields, numeric and date formats, validation, busy/disabled/visible conditions, and licence text. Nav labels and page headings MAY use the new destination names. Other in-app copy MUST NOT be rewritten, removed, replaced by an icon alone, or placed behind a new interaction. Presentation MUST NOT change processing, lookup, cache, reset, skip-list, or log filter/download behavior.

#### Scenario: Existing action is activated
GIVEN an existing console action is available
WHEN the operator activates it after the redesign
THEN it invokes the same operation with the same inputs and safeguards
AND it exposes the same pending and final outcomes as before

#### Scenario: Existing confirmation is displayed
GIVEN Reset All or a cache deletion reaches its existing confirmation state
WHEN that confirmation is shown
THEN the same prompt and confirmation/cancellation controls remain visible and operable
AND no confirmation step is added or bypassed

#### Scenario: Licence and help text need multiple lines
GIVEN existing explanatory text, attribution, or GADM licence text needs multiple lines
WHEN the page is displayed
THEN the text remains complete and readable
BUT it is not shortened, clipped, hidden behind a new disclosure, or removed

### Requirement: Narrow stacked navigation

At a narrow viewport the grouped rail SHALL stack above the page content. Every destination in Work, Configure, and Library SHALL remain directly visible and operable. The console MUST NOT add a menu button or other extra navigation interaction to hide those destinations.

#### Scenario: Phone-width console is used
GIVEN the console is viewed at 390 by 844 CSS pixels
WHEN the operator reads the page
THEN Work, Configure, and Library destinations remain visible without opening a menu
AND labels, controls, messages, and footer remain reachable
AND multi-column groups reflow rather than overflowing the page

### Requirement: Accessible console states

Primary, secondary, destructive, disabled, hovered, and keyboard-focused controls SHALL remain distinguishable in both Light and Dark. Normal enabled text SHALL have at least 4.5:1 contrast against its background. Meaningful status SHALL retain text alongside color. Keyboard focus order SHALL follow the visible reading order. Existing live-region, alert, label, and dialog semantics SHALL be preserved. Reduced-motion preferences SHALL suppress nonessential animation.

#### Scenario: Keyboard operator traverses Overview
GIVEN Overview is displayed
WHEN the operator moves through interactive elements with the keyboard
THEN focus is visible
AND focus follows the displayed reading order
AND every available action is reachable

#### Scenario: Worker or maintenance status changes
GIVEN an existing status transition or failure is rendered
WHEN that state is shown
THEN status text remains available in addition to color
AND existing live-region or alert semantics remain
