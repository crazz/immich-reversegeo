## 1. Verify the removal gate and landed ownership

- [ ] 1.1 Re-read the applied blocks 11, 13, 19–20, and 25–37 source/tests; record the exact selector, keyed backend, coordinator scope, child adapter, executor, detector, composition-root, alias, and fixture names/lifetimes, and stop rather than create parallel abstractions.
- [ ] 1.2 Require passing block-37 rollout evidence for production-package startup, manual and detector-positive scheduled outcomes, process-local rejection with no process/exit, advisory Busy as Failed plus exit 3, Completed/Cancelled/Failed terminals, protocol/crash and forced-kill raw evidence, cancellation, full stream/process/scope/activity/handle cleanup, safe retrigger, and zero in-process resolution.
- [ ] 1.3 Inventory the transitional heavy-registration helper and map every executor-only descriptor separately from every resolver/cache/repository descriptor still consumed by Lookup or Data; explicitly hand retained Web-heavy services to block 55.

## 2. Collapse dispatch to one child backend

- [ ] 2.1 Add or retain one internal non-keyed child-processing backend contract at the coordinator's run-scope boundary, owned by Web child-client composition and preserving the finalized child adapter behavior.
- [ ] 2.2 Change the singleton coordinator to resolve exactly one unkeyed child backend per dispatched run after admission/reporter arming and, for scheduled work, only after a positive detector result; preserve terminal/finality cleanup before matching-handle release.
- [ ] 2.3 Preserve manual and scheduled request/run/reporter/token identity, Stop and shutdown ownership, event projection, cancellation escalation, classifier precedence, state lifecycle, scope disposal, and no fallback/replacement/replay/resubmission/retry.
- [ ] 2.4 Prove local contention, detector false, and detector cancellation/failure before dispatch create no backend scope and resolve no child backend, executor, protocol session, or heavy processing collaborator.

## 3. Remove production in-process selection and execution

- [ ] 3.1 Delete the temporary backend enum, immutable selection singleton, selection default/overload, selection switches, invalid-enum validation, keys, keyed registrations/resolution, and transition-only comments or factories using their exact landed equivalents.
- [ ] 3.2 Delete the production in-process backend adapter and every Web DI alias, factory, constructor dependency, service-locator lookup, delegate, or fallback that can resolve or invoke the authoritative executor.
- [ ] 3.3 Remove newly unreferenced production interfaces, helpers, imports, and selection/in-process tests rather than leaving dead code; retain only test-local fakes that implement the new child control boundary.
- [ ] 3.4 Confirm no AppConfig field, settings JSON migration, configuration-provider binding, environment variable, CLI argument, endpoint, UI control, obsolete alias, or hidden compatibility path is added.

## 4. Make executor composition worker-only

- [ ] 4.1 Split the transitional heavy registration helper as needed so internal-worker composition registers the authoritative executor and executor-only aliases, while production Web composition cannot resolve them.
- [ ] 4.2 Preserve executor direct construction only in the internal-worker execution path and focused test fixtures/worker test hosts; preserve its exact singleton/seam identity inside a worker host.
- [ ] 4.3 Retain without duplication every heavy resolver, cache, repository, mapping delegate, data source, and lightweight identity service still needed by Web Lookup/Data; do not perform block-55 removals or edit block 39.
- [ ] 4.4 Verify registration factories remain lazy and do not initialize country indexes, geodata, DuckDB, downloads, skipped storage, or database work during Web startup.

## 5. Add architecture and regression guards

- [ ] 5.1 Add compile-time/dependency tests proving coordinator and Web control-plane constructors cannot depend on the executor or an in-process adapter and expose only one child backend resolution contract.
- [ ] 5.2 Add descriptor/reference-identity tests proving Web has one non-keyed child backend and no executor/in-process/selection descriptor, while worker composition has exactly one authoritative executor identity and no Web coordinator/scheduler/UI graph.
- [ ] 5.3 Add a search-based production-source guard for the removed enum/selection/keyed registration and resolution/in-process adapter/fallback identifiers, with explicit allowlisted executor construction/registration locations limited to worker composition and test fixtures.
- [ ] 5.4 Replace transition selection tests with child-only manual and eligible-scheduled tests covering request identity, duplicate rejection, state parity, exact Stop, Completed/Cancelled/Failed terminals, process-local rejection, advisory Busy Failed-terminal/exit-3 behavior, and forced-kill raw evidence, cleanup, retrigger, and zero fallback/retry.
- [ ] 5.5 Retain block 36's detector-zero regression unchanged and prove one lightweight detector operation, local zero finalization, and zero backend/launcher/protocol/executor/heavy-processing effects.
- [ ] 5.6 Run Lookup/Data composition or focused smoke tests proving retained Web-heavy dependencies still resolve and behave without making the executor reachable.

## 6. Validate packaging, scope, and rollback

- [ ] 6.1 Run focused coordinator/backend/scheduler/composition/architecture tests and `npm run test` with default Integration/Performance exclusions.
- [ ] 6.2 Run the strict Phase 4 worker process-fixture/integration matrix for packaged startup, success/no-work, process-local rejection, advisory Busy/exit 3, cancellation, protocol/crash, forced-kill raw evidence, cleanup, no orphan, and safe retrigger outcomes.
- [ ] 6.3 Publish/inspect the production image and prove the Web role cannot resolve/invoke the executor, the internal-worker role can execute it from the same assembly/image, and all required managed/native child runtime files remain staged.
- [ ] 6.4 Run the production-source search guard, compile/dependency checks, and a block-38-only diff review proving no block-39 artifact/code edit, block-55 heavy-service removal, protocol/deployment-mode/configuration change, or unrelated numbered work entered the change.
- [ ] 6.5 Run `openspec validate 38-remove-production-in-process-execution --strict` and require success, then inspect final `openspec status --change 38-remove-production-in-process-execution`.
- [ ] 6.6 Record that rollback after this removal is only a source/version revert followed by rebuild and redeploy; preserve diagnostics and never add a runtime selector, code-only emergency seam, or per-run in-process fallback.

## Audit Reconciliation

“Local contention” means only Web/coordinator admission rejection before dispatch; it is not PostgreSQL advisory-lock Busy. Preserve distinct authoritative committed terminals, local admission rejection without a child, canonical advisory Busy as a failed child terminal with no eligibility and four zero counts, and forced raw kill as classification evidence rather than a terminal. No case restores an in-process fallback.

