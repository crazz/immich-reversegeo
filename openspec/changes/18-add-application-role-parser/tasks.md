## 1. Pure role selection contracts

- [x] 1.1 Add immutable Web, InternalWorker, and reserved RunOnce role values plus a discriminated success/failure result that cannot expose a partial/default role.
- [x] 1.2 Implement a framework-independent deterministic selector over an argument sequence and typed Web/RunOnce candidate, defaulting the candidate to Web and making a valid private selector override either candidate.
- [x] 1.3 Implement exact ordinal `--internal-worker` grammar with stable duplicate, unexpected-extra, and invalid-syntax categories; reserve case variants and `--internal-worker=...` forms while preserving every unrelated argument unchanged.
- [x] 1.4 Produce bounded constant-form failures that list only the canonical private syntax and never echo arguments, environment values, exceptions, stacks, credentials, or secrets.

## 2. Pre-host startup integration

- [x] 2.1 Invoke role selection as the first application-owned operation in `Program.cs`, before `WebApplication.CreateBuilder(args)`, deployment/environment resolution, DI, filesystem initialization, or application logging.
- [x] 2.2 Pass the original ordered arguments unchanged into the existing Web builder when Web is selected, preserving no-argument and arbitrary ASP.NET argument behavior.
- [x] 2.3 Add explicit InternalWorker and reserved RunOnce non-Web branch boundaries that cannot fall through to `WebApplication.CreateBuilder`, without adding a worker host, run-once execution, or block-19 composition changes.
- [x] 2.4 On role failure, write the safe diagnostic to stderr, set exit code 2, and exit before host/DI side effects; do not add help/version handling.

## 3. Deterministic parser tests

- [x] 3.1 Cover the default Web candidate, explicit typed RunOnce candidate boundary, valid InternalWorker selection, and InternalWorker precedence over both public-role candidates.
- [x] 3.2 Cover exact case sensitivity, case variants, empty/non-empty assignment forms, selector placement, duplicate-selector precedence, every additional-argument position, and stable safe failure categories.
- [x] 3.3 Prove unrelated unknown/duplicate/missing-value ASP.NET arguments and help/version tokens are preserved byte-for-value and in order when no private syntax exists, while private selector plus help/version is rejected.
- [x] 3.4 Prove repeated calls are value-equivalent, inputs are not mutated, failures contain no role/partial value, and diagnostics do not contain injected argument or environment secrets.

## 4. Startup-boundary verification

- [x] 4.1 Add a narrow entry-point/startup seam test proving invalid and InternalWorker selections occur before Web builder, deployment/environment, DI, filesystem, and logging factories can be invoked.
- [x] 4.2 Verify invalid private invocation reports only the stable category/canonical syntax to stderr and exits 2 without constructing a host.
- [x] 4.3 Verify valid InternalWorker and future RunOnce branch selections cannot recurse or fall through to the Web path, while default Web startup remains unchanged.
- [x] 4.4 Run focused MSTests, the normal default-exclusion test suite, and `openspec validate 18-add-application-role-parser --strict`.
