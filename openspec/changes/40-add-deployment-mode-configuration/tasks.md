## 1. Reconcile prerequisite startup boundaries

- [ ] 1.1 Re-read the applied block 18 role parser and block 19 startup/composition input source and tests; record their exact type names and consume them without duplicating the reserved-role parser or changing its grammar.
- [ ] 1.2 Add an immutable deployment-mode value and a pure resolver for the sole source `IMMICH_REVERSEGEO_MODE`, with exact ordinal values `standard`, `web-only`, and `run-once`; default null only and reject empty, whitespace, padded, case-varied, and unknown values.
- [ ] 1.3 Keep the deployment-mode model outside persisted `AppConfig`/`ConfigService` and expose the resolved value only through the immutable startup/composition snapshot used by later mode blocks.

## 2. Integrate pre-host selection

- [ ] 2.1 Wire block 18's authoritative private-role selection ahead of deployment environment access so valid InternalWorker and all reserved-syntax failures bypass mode reads and retain their existing results, diagnostics, and exit behavior.
- [ ] 2.2 For invocations without reserved private syntax, read `IMMICH_REVERSEGEO_MODE` exactly once, map Standard and Web-only to the existing Web public-role candidate and Run-once to the existing RunOnce candidate, and preserve all ordinary host arguments unchanged.
- [ ] 2.3 On invalid public configuration, write the bounded constant-form `invalid-deployment-mode` diagnostic with all accepted values to stderr and exit 2 before builder/host construction, DI, application logging, path resolution, filesystem access, or settings reads; never include the raw value or other environment data.
- [ ] 2.4 Leave every selected deployment mode at the existing typed composition handoff only; do not implement Standard/Web-only/Run-once service graphs, hosts, execution, live switching, or UI.

## 3. Add focused verification

- [ ] 3.1 Add pure MSTest cases for all three exact values, missing-variable Standard default, empty/whitespace/padded/case-varied/unknown rejection, deterministic results, and one-read immutable snapshot behavior.
- [ ] 3.2 Add startup-boundary tests proving valid InternalWorker bypasses even invalid/canary mode configuration, malformed/duplicate/augmented private syntax wins without a mode read, ordinary ASP.NET arguments remain unchanged, and Standard/Web-only/Run-once map to the finalized block 18 candidates.
- [ ] 3.3 Add failure tests asserting exit code 2, stable category and accepted-values text, canary-secret redaction, and zero builder/DI/logging/path/filesystem/settings side effects.
- [ ] 3.4 Add persistence coverage proving saving `AppConfig` while the environment variable is set writes no deployment-mode property or value to `settings.json`.

## 4. Document the container contract

- [ ] 4.1 Keep the production Dockerfile on one unchanged entrypoint and without a baked deployment-mode `ENV`; add a static contract assertion only if the existing Dockerfile test conventions support it, leaving runtime image smoke tests to block 46.
- [ ] 4.2 Update the reference `docker-compose.yml` with an optional commented `IMMICH_REVERSEGEO_MODE` example/list while keeping omission as the Standard default; do not expose the private `--internal-worker` token.
- [ ] 4.3 Update Docker-first public installation/configuration docs using the product name “Immich ReverseGeo” and mode names “Standard,” “Web-only,” and “Run-once”; document exact lowercase values, missing-only default, invalid startup failure, restart requirement, and exclusion from `settings.json`, while deferring mode behavior to blocks 41–43.

## 5. Validate scope

- [ ] 5.1 Run focused deployment-mode and configuration-persistence MSTest coverage, then `npm run test`.
- [ ] 5.2 Run strict OpenSpec validation and status for change 40 and review the diff for planning/implementation scope that excludes blocks 41–44 and leaves block 46 smoke ownership intact.
