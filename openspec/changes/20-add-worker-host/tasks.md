## 1. Prerequisite and ownership reconciliation

- [ ] 1.1 Re-read the applied Phase 2 executor/reporter source and blocks 15, 17, 18, and 19 source/tests; record exact request, role, composition, alias, initializer, result, and terminal-event owners, and stop rather than invent missing prerequisites.
- [ ] 1.2 Record a block-20 boundary matrix for Generic Host construction, service scope, readiness, request acquisition, cancellation, terminal coordination, standard streams, exit outcome, and disposal, explicitly assigning NDJSON emission to 21, stdin mechanics to 22, and exit codes to 23.

## 2. Generic Host construction

- [ ] 2.1 Wire the exact InternalWorker role branch to a `HostApplicationBuilder`/`IHost` path using block 19's shared and internal-worker roots, without forwarding the consumed private selector or constructing `WebApplicationBuilder`/`WebApplication`.
- [ ] 2.2 Configure InternalWorker host/framework/application console logging to stderr before host build and leave stdout without a writer until block 21 supplies the sole protocol emitter.
- [ ] 2.3 Register one worker lifecycle hosted service and verify no Kestrel/listener/URL, endpoint/static/Razor/Blazor/antiforgery/Data Protection, scheduler/coordinator, or UI-state service enters the worker host.
- [ ] 2.4 Preserve the unchanged no-argument Web builder, composition, middleware, endpoints, arguments, and startup behavior.

## 3. Lifecycle seams and startup boundary

- [ ] 3.1 Add narrow transport-neutral abstractions for required worker startup, readiness publication, one initial execute-request lease, pre-request EOF/failure, and accepted terminal/finality coordination; add no console reader, NDJSON serializer/writer, or exit-code policy.
- [ ] 3.2 Have the hosted lifecycle await Generic Host startup, create one `AsyncServiceScope`, resolve scoped lifecycle collaborators, and complete the finalized required asynchronous initializers before readiness publication.
- [ ] 3.3 Preserve lazy country-index, DuckDB, download, and processing database behavior, and test that readiness alone performs none of that heavy work.
- [ ] 3.4 Await readiness publication successfully before invoking the initial request-acquisition seam; route startup/readiness failure to pre-request failure coordination and one-shot shutdown.

## 4. One accepted request and executor invocation

- [ ] 4.1 Model initial acquisition as exactly one of accepted execute lease, clean pre-request EOF, or safe pre-request failure, preserving block 17's exact immutable request and EOF distinctions.
- [ ] 4.2 On acceptance, create one linked token from the lease cancellation token and Generic Host stopping token and invoke the finalized executor exactly once with the exact request/reporter/token.
- [ ] 4.3 Preserve cancellation already requested before executor entry, cooperative cancellation during execution, unrelated-cancellation failure taxonomy, and clean post-acceptance stdin EOF as non-cancelling.
- [ ] 4.4 Never generate a second run ID, alter the trigger, precompute eligibility/settings/work sets, start scheduling, return to initial request acquisition, or invoke a second executor job.

## 5. Terminal coordination and structured disposal

- [ ] 5.1 Keep the executor/reporter as the sole accepted-run domain terminal producer; await one host finality/flush/outcome hook for a returned result or accepted infrastructure failure without synthesizing or duplicating a terminal event.
- [ ] 5.2 For clean EOF or failure before request acceptance, await only the pre-request hook, start no executor/session/run terminal, and leave exit-code selection to block 23.
- [ ] 5.3 In unconditional cleanup, settle the request/control lease, dispose the linked CTS and lease, asynchronously dispose the one execution scope, request application stop once, and dispose the host/provider so singleton/native resources are released.
- [ ] 5.4 Use Generic Host lifetime for SIGTERM/SIGINT and explicit stop; add no custom signal handler, port, server, or shutdown scheduler.

## 6. Deterministic host verification

- [ ] 6.1 Add in-memory gated startup/readiness/request/executor/terminal collaborators proving host-start-before-init, init-before-ready, ready-before-request wait, exact identity, exactly one executor call, one terminal hook, and one-shot stop without sleeps.
- [ ] 6.2 Cover clean pre-request EOF, partial/invalid/pre-request failure, readiness failure, executor result, active cancellation, unrelated cancellation-like failure, executor/reporter fault, terminal-hook fault, and host stop while waiting or executing.
- [ ] 6.3 Add disposable sentinels proving accepted and pre-request cleanup paths release lease, async scope, hosted service, shared data source/provider, and host resources in the required order even after failure.
- [ ] 6.4 Add descriptor/host tests proving no Web/server/scheduler/UI facilities, ports, live PostgreSQL/SQLite, country-index/DuckDB/download initialization, real stdin/stdout, child process, or exit mapper is used; verify worker host logs follow stderr policy and stdout remains unclaimed.
- [ ] 6.5 Run focused MSTests, `npm run test`, `openspec validate 20-add-worker-host --strict`, final OpenSpec status, and a scope diff proving block 21 and all other numbered changes were not modified.
