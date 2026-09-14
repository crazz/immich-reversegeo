## Why

After block 38, Web processing must remain a control-plane route even though Lookup and Data still legitimately keep heavy geodata services registered until block 55. A route-specific regression is needed so manual or eligible scheduled processing cannot silently restore country-index, resolver, Overture/GADM, airport, or in-process executor work to the long-lived Web process.

## What Changes

- Add production-composition tests for accepted manual processing, detector-positive scheduled processing, and detector-empty scheduled processing.
- Prove accepted dispatching routes delegate exactly once to the production child-dispatch boundary contract and never resolve, construct, or call the worker executor, country geometry index, administrative resolver, Overture Places/divisions/cache, GADM divisions/cache, or airport lookup in Web.
- Permit the scheduled detector to use its lightweight repository query before dispatch; a false result remains local and launches no child, while still touching no heavy geodata.
- Add a processing-root service-graph guard plus fail-on-resolution/construction/call sentinels, including instrumentation at the lazy bundled-country-index load transition.
- Keep heavy Web registrations needed by Lookup and Data legal in this transition; block 55 later upgrades the boundary to whole-Web registration exclusion.

## Capabilities

### New Capabilities
- `architecture/web-processing-geodata-boundary`: enforces route-specific Web processing delegation and prevents worker-only geodata or executor activation.

### Modified Capabilities
- None.

## Impact

Depends on applied blocks 19–20 and 38 and consumes the finalized coordinator, scheduled detector, child boundary, role-specific registration root, and block-36 empty-route coverage from blocks 33–37 without editing those changes. Expected implementation is limited to testability instrumentation/composition seams and tests under `tests/ImmichReverseGeo.Tests/`; it does not change processing results, configuration, protocol, Lookup/Data behavior, or deployment modes and performs no real process, database, or geodata work.

## Audit Reconciliation

The test substitutes and proves the finalized child-dispatch boundary contract, not a real child process. Assertions about coordinator/detector/boundary names, registration roots, and available test seams are conditional on their landed forms after prerequisite application; bind to those exact contracts and do not claim process startup, protocol, or real worker execution occurred.

