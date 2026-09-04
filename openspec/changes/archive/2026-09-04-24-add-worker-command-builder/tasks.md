## 1. Prerequisite reconciliation

- [x] 1.1 Re-read the applied block 18 role selector and blocks 19–20 composition/worker-host source and tests; consume their exact known-Web identity, `--internal-worker` token, and registration seams without redefining role parsing or worker lifecycle.
- [x] 1.2 Inventory checkout truth for the supported production layouts (local framework-dependent Web output, Docker entrypoint, and any same-application apphost). Apply only the layouts the checkout actually supports; record a stop/reconcile outcome rather than treating planning baselines as landed facts.

## 2. Pure descriptor and runtime seams

- [x] 2.1 Add immutable descriptor, success/failure, and stable bounded failure-category values for executable, discrete ordered arguments, working directory, environment-inheritance policy, redirect stdin/stdout/stderr, `UseShellExecute=false`, and `CreateNoWindow=true`.
- [x] 2.2 Add an injected immutable runtime-facts adapter and deterministic OS/path/filesystem seams for current process path, current and known-Web assembly identities/locations, working directory, path comparison/extension rules, and target existence.
- [x] 2.3 Ensure descriptor/failure default string and diagnostic representations cannot enumerate environment, reconstruct the command line, echo raw invalid paths, or expose arguments, configuration, credential canaries, exceptions, or stacks.

## 3. Validated entrypoint resolution

- [x] 3.1 Implement pure framework-dependent resolution that accepts only the current absolute existing platform `dotnet` host plus a matching absolute existing Web entry assembly and emits exactly `<assembly-path>`, `--internal-worker` through discrete argument values.
- [x] 3.2 Implement pure same-application apphost resolution that requires matching platform-normalized executable and Web entry-assembly identities and emits exactly `--internal-worker`; do not infer mode from sibling files or scan directories.
- [x] 3.3 Preserve the validated absolute current working directory and complete unchanged process-environment inheritance, including `CONFIG_DIR`, `DATA_DIR`, ASP.NET/.NET settings, and all `DB_*` settings, without materializing those values in arguments or descriptor logs.
- [x] 3.4 Fail closed with no partial descriptor for unavailable, empty, relative, missing, mismatched, unsupported, ambiguous, or test-host-derived executables, assemblies, and working directories; never search PATH, use `AppContext.BaseDirectory`, select the first nearby target, or silently switch launch shape.
- [x] 3.5 Register only the descriptor/runtime-facts builder needed by block 25. Do not instantiate `ProcessStartInfo`, start a child, open/drain streams, write requests, wait, cancel/kill, classify exit, retry, expose PID/session state, or dispose process resources.

## 4. Deterministic verification

- [x] 4.1 Add pure tests for local framework-dependent and Docker snapshots, asserting exact executable, absolute assembly argument, exact final `--internal-worker`, argument order, working directory, environment inheritance, all redirect flags, `UseShellExecute=false`, and `CreateNoWindow=true`.
- [x] 4.2 Add Windows and Unix apphost/path tests covering `.exe`, case rules, separators, roots, spaces, and platform-significant characters while proving values remain single unquoted `ArgumentList` items.
- [x] 4.3 Add negative tests for null/empty/relative/missing executable, assembly, and working-directory facts; mismatched entry/application identity; ambiguous/unsupported layouts; and no fallback between apphost and dotnet modes.
- [x] 4.4 Add an explicit Microsoft.Testing.Platform snapshot proving that the test executable/entry assembly and a copied nearby `ImmichReverseGeo.Web.dll` are rejected and never returned or executed.
- [x] 4.5 Add repeated-resolution and hostile-canary tests proving value equivalence, no ambient runtime/filesystem/environment reads beyond injected seams, no process creation, no shell quoting, no command-line credentials, and redacted diagnostics/representations.
- [x] 4.6 Run focused MSTests, `npm run test`, `openspec validate 24-add-worker-command-builder --strict`, and `openspec status --change 24-add-worker-command-builder`.
