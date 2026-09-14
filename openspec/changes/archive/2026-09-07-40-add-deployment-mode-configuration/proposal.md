## Why

Immich ReverseGeo currently has no validated public startup selection for its planned deployment modes. Phase 6 needs one compatible, secret-safe, startup-only contract before later changes compose Standard, Web-only, and Run-once behavior.

## What Changes

- Add the public environment variable `IMMICH_REVERSEGEO_MODE` with exact lowercase values `standard`, `web-only`, and `run-once`.
- Default only a missing variable to Standard; reject empty, whitespace, case-varied, padded, and otherwise unsupported values before host construction.
- Resolve the effective deployment mode once as an immutable startup snapshot; do not persist it in `AppConfig` or support live mode changes.
- Preserve the private exact `--internal-worker` role parser as the higher-precedence contract, including bypassing deployment-mode reads and validation for a valid internal worker invocation.
- Emit a bounded, constant-form, secret-safe startup diagnostic for invalid public mode configuration.
- Define the Docker/Compose and public documentation contract without implementing any mode-specific composition or UI.

## Capabilities

### New Capabilities
- `deployment-mode-configuration`: Defines the public source, accepted values, default, validation, precedence, startup snapshot, persistence boundary, and container-facing contract for deployment mode selection.

### Modified Capabilities
- None.

## Impact

The implementation will affect the executable startup/role-selection boundary and its focused MSTest coverage. Public Docker-first configuration documentation and the reference `docker-compose.yml` will describe the optional environment variable; the Dockerfile keeps one unchanged image and entrypoint with no baked-in mode. No settings UI, mode-specific service composition, Run-once execution, or internal-worker public interface is added by this change.
