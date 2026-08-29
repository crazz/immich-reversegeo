## Context

See [proposal.md](proposal.md) for motivation and [the deployment-mode specification](specs/deployment-mode-configuration/spec.md) for observable behavior. `AppConfig` currently contains mutable processing and schedule settings saved by `ConfigService` to `<configDir>/settings.json`; database credentials and path overrides are environment-backed. The finalized block 18 contract owns an exact private `--internal-worker` parser, preserves ordinary ASP.NET arguments, and accepts a typed Web or RunOnce public-role candidate. Block 19 resolves immutable composition inputs only after private role selection. The current checkout may not yet contain those applied sources, so implementation must consume their finalized APIs rather than create a parallel role parser.

## Goals / Non-Goals

**Goals:**
- Resolve one typed deployment mode deterministically before any public host or composition side effect.
- Keep operational placement out of mutable/persisted application settings.
- Integrate with block 18 so private worker precedence and safe failures remain authoritative.
- Give later blocks one immutable startup snapshot without prescribing their service graphs.

**Non-Goals:**
- Implement Standard, Web-only, or Run-once composition or lifecycle behavior (blocks 41–43).
- Add a deployment-mode UI, endpoint, reload mechanism, or live transition (block 44 owns UI scope if any).
- Change the private worker grammar, expose it to self-hosters, or alter worker protocol/launch behavior.
- Add Docker mode smoke orchestration owned by block 46.

## Decisions

### Use one strict environment variable

Use `IMMICH_REVERSEGEO_MODE` as the sole public source. Parse with ordinal exact matching to `standard`, `web-only`, and `run-once`. A null/missing value maps to Standard; every non-null value, including empty, whitespace, padded, or case-varied text, must match exactly or fail.

This distinguishes an omitted compatible default from a typo and keeps Docker/Compose configuration portable. Do not trim or accept enum names such as `Standard`, `WebOnly`, or `RunOnce`; those are model names, not public values. Alternatives such as ASP.NET configuration binding, command-line aliases, or case-insensitive enum conversion are rejected because they create extra providers/precedence and can silently accept operator mistakes.

### Keep deployment mode separate from AppConfig and ConfigService

Represent the result with a small immutable deployment-mode value owned near the startup/role-selection boundary. Do not add it to `ImmichReverseGeo.Core.Models.AppConfig`, `ConfigService`, the Settings page, or `settings.json`. Operational topology is selected by the launcher/container and must not be mutable by an in-app save.

The environment accessor is injected or wrapped behind a narrow source for pure tests, but production reads the named variable once. The resulting value is passed through or alongside block 19's immutable startup/composition input; factories and hosted services do not reread it. Alternatives—persisted settings or `IOptionsMonitor`—are rejected because they imply precedence, persistence, and live transitions that later composition cannot safely perform.

### Preserve block 18 as the only private role parser

Startup first applies block 18's existing parser with its Web candidate as a side-effect-free private-role preflight. An InternalWorker result or any block 18 failure returns immediately; consequently deployment configuration is never read and cannot mask the private contract. A Web result proves no reserved private syntax is present. Startup then resolves the public mode once: Standard and Web-only retain the Web role candidate, while Run-once supplies block 18's existing RunOnce candidate/result boundary. If the exact applied API requires final role selection again, invoke the same pure selector with the resolved candidate; do not copy its reserved-token classifier or create a second parser. Ordinary arguments remain byte-for-byte/order-preserved.

This sequence reconciles block 18's requirement that private selection precede application-owned environment resolution with its typed candidate API. A possible alternative is a reviewed lazy-candidate overload on the existing selector, but block 40 must not change its grammar, failure categories, or ownership merely for convenience.

The immutable deployment mode remains distinct even when Standard and Web-only both map to block 18's Web role; blocks 41 and 42 consume that distinction. Run-once only reaches the existing typed boundary here; block 43 owns its host and work lifecycle.

### Fail through a pre-host startup result

Return a typed invalid-mode failure with stable category `invalid-deployment-mode`. The executable writes a fixed diagnostic equivalent to `invalid-deployment-mode: IMMICH_REVERSEGEO_MODE must be one of: standard, web-only, run-once.` to stderr and exits 2. Do not interpolate the raw value or an exception. Perform this before builder creation, DI, logging, path resolution, directories, or settings reads.

Using the same exit-code and bounded-stderr discipline as block 18 provides deterministic container failure without risking secrets. Throwing into host logging or logging the full environment is rejected because no host exists and the input may itself contain sensitive data.

### Keep the image neutral and document the Compose contract here

Do not add a mode-specific image, entrypoint, command, exposed port, volume, or baked Dockerfile `ENV`. The existing image receives `IMMICH_REVERSEGEO_MODE` normally from Docker/Compose. Keep the reference Compose default omitted so it exercises backward-compatible Standard startup; add a concise commented example or nearby comment listing exact alternatives rather than forcing an explicit value.

Block 40 owns updates to the Docker-first installation/configuration docs for the variable, exact syntax, default, restart-only behavior, and settings exclusion. Blocks 41–43 own behavioral descriptions once those modes work, and block 46 owns executable image smoke tests. Public copy uses “Immich ReverseGeo,” “Standard,” “Web-only,” and “Run-once”; lowercase remains limited to exact technical values and slugs.

## Risks / Trade-offs

- [Strict case and whitespace handling surprises an operator] → List exact copyable values and the accepted-values failure message in public docs.
- [A child worker inherits an invalid public value] → Complete authoritative private-role parsing first and prove the environment source was not read.
- [Two mode models drift between deployment and role selection] → Keep deployment mode as the operational value and map it once to block 18's existing typed public-role candidate; do not add another role enum.
- [A future settings save accidentally persists operational state] → Keep the type outside `AppConfig` and add serialization exclusion coverage.
- [Planning references differ from applied Phase 3 names] → Inventory the applied block 18/19 source and tests before implementation, then use exact existing types and seams.

## Migration Plan

1. Add the pure strict deployment-mode resolver and immutable result without changing host composition.
2. Integrate it at the pre-host role boundary with block 18 precedence and unchanged ASP.NET arguments.
3. Add focused unit/startup-boundary tests, including canary-secret redaction and source-read counters.
4. Document the optional container environment variable and keep the Dockerfile/reference Compose default neutral.
5. Deploy with the variable omitted to retain Standard selection. To choose another planned mode after its owning block is implemented, set the exact value and restart the container.

Rollback uses the previous image or removes the variable; no `settings.json`, volume, database, or cache migration is involved.
