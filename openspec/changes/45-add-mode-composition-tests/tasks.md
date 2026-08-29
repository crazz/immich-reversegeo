## 1. Reconcile finalized prerequisites

- [ ] 1.1 Verify blocks 40–44 are applied, then inventory the exact landed startup decision, immutable mode snapshot, four composition roots, host-kind seam, status registrations, scheduler/coordinator/launcher/detector/executor/protocol services, validators/initializers, and service lifetimes; stop rather than create replacement contracts if a prerequisite is absent.
- [ ] 1.2 Map the block 40–44 focused test ownership and identify only the representative cross-mode cases needed here, leaving exhaustive parser/redaction/persistence, per-mode lifecycle/outcome/signal/disposal, and UI transition coverage in their owning suites.
- [ ] 1.3 Record the exact transitional Lookup/Data registrations allowed in Standard and Web-only and the processing-root services that must not reach them, so matrix assertions do not incorrectly require the whole Phase 6 Web provider to be geodata-free.

## 2. Establish the hermetic matrix harness

- [ ] 2.1 Reuse the landed production registration helpers through the smallest side-effect-free startup/composition seam; only extract such a seam when necessary, without changing public behavior or duplicating a composition root.
- [ ] 2.2 Add an immutable dictionary-backed mode source with read counters for missing, exact, and invalid values, and build a fresh independently disposable provider/fake set for every matrix row.
- [ ] 2.3 Add descriptor expectations plus fail-on-construction/resolution sentinels for builder/provider side effects, Web server/listener, scheduler/waits, detector, coordinator/launcher/child, executor, processing geodata, private protocol, database, filesystem/settings, downloads, Docker/process spawn, HTTP, and sockets.
- [ ] 2.4 Add controlled fakes for startup prerequisites/shutdown budget, clock/wait, saved schedules, detector, child boundary, executor/reporter, host lifetime, paths, and disposal so behavior rows can run without live PostgreSQL, geodata, Docker, real workers, HTTP, or bound ports.
- [ ] 2.5 Configure normal rows for parallel execution and add a concurrent isolation test; if a real process-environment entrypoint fixture is unavoidable, isolate only that fixture as non-parallel and restore the exact previous missing-or-value state in guaranteed cleanup.

## 3. Cover startup selection and precedence

- [ ] 3.1 Add table rows proving a missing mode source and exact `standard` select equivalent Standard composition, while exact `web-only` and `run-once` select their distinct roots from one immutable read.
- [ ] 3.2 Add representative empty, whitespace-only, padded, case-varied, and unknown rows proving stable invalid-deployment-mode/exit-2 classification before builder, host, provider, logging, path, filesystem, settings, listener, or work side effects.
- [ ] 3.3 Add private-precedence rows proving the exact sole `--internal-worker` invocation selects InternalWorker with zero mode reads for missing, accepted, and invalid/canary values, and malformed/duplicate/augmented reserved syntax retains its failure with zero mode reads.
- [ ] 3.4 Prove accepted public values never select or construct InternalWorker controller transport and that ordinary public startup does not reinterpret the private token contract.

## 4. Cover descriptors, host types, and identities

- [ ] 4.1 Add Standard missing/default and explicit descriptor/provider assertions for Web host, server/UI/endpoints, finalized Web status, exactly one scheduler concrete singleton and reference-identical hosted alias, coordinator, detector, launcher/backend, validator, and lifecycle owner.
- [ ] 4.2 Add Web-only assertions for the same common Web/UI/manual/status graph while proving scheduler concrete/hosted aliases, waits, due callbacks, scheduled pending path, and scheduled-only detector activation are absent rather than no-op.
- [ ] 4.3 Add Run-once assertions for the non-Web one-shot host, direct executor/reporter/lock/geodata identities, and absence of server/UI/Data Protection/endpoints/ports, scheduler, coordinator, detector, launcher/bridge/child, and private protocol.
- [ ] 4.4 Add InternalWorker assertions for its non-Web worker host, executor/geodata and private controller transport, and absence of server/UI/ports, scheduler, coordinator, and child launcher.
- [ ] 4.5 Resolve every finalized singleton through each intentional alias and assert per-provider reference identity, exact applicable hosted-service counts, async disposal, and no singleton sharing across concurrently built mode providers.
- [ ] 4.6 Assert Standard/Web-only asset-processing roots cannot resolve or construct the authoritative executor, in-process processing backend, or processing geodata while explicitly allowing—but isolating—current-phase Lookup/Data dependencies.

## 5. Cover comparative trigger and startup behavior

- [ ] 5.1 Add a Standard row proving manual admission launches one fake child without detector use, detector-empty scheduled work launches none, and detector-positive scheduled work launches exactly one.
- [ ] 5.2 Add Web-only rows for enabled-valid, disabled, empty, and invalid saved schedules proving zero waits, detector calls, scheduled pending transitions, automatic children, or persistence mutations, plus one manual child without detector use.
- [ ] 5.3 Add eligible and authoritative no-work Run-once rows proving one fresh RunOnce request, one direct executor invocation, one attempt/disposal, and no detector, child resolution/spawn, retry, replay, replacement, or second pass.
- [ ] 5.4 Add Web-mode startup validation rows proving success reaches acceptance readiness without child/executor/geodata/listener construction and failure prevents readiness/work; add direct-root initializer rows proving Run-once/InternalWorker do not construct foreign Web/scheduler/coordinator/launcher/transport graphs.
- [ ] 5.5 Keep InternalWorker execution at the fake controller/executor boundary only; do not duplicate protocol framing or spawn a process.

## 6. Verify hermetic scope and ownership

- [ ] 6.1 Run the focused block-45 matrix and parallel-isolation tests and confirm all database, geodata, download, Docker, process, HTTP, and socket sentinels remain untouched.
- [ ] 6.2 Run `npm run test` with the repository's normal Integration/Performance exclusions and investigate any ordering or shared-state failure rather than serializing the whole matrix.
- [ ] 6.3 Run `openspec validate 45-add-mode-composition-tests --strict` and inspect `openspec status --change 45-add-mode-composition-tests` for complete artifacts.
- [ ] 6.4 Review the final diff as block-45-only: no edits to block 44 or its UI behavior, no production Docker/image/entrypoint/port/UID/volume smoke from block 46, and no live database, geodata, or worker-process fixture unless separately explicit and justified.
