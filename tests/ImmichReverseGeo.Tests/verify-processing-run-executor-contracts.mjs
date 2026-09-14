import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";

const artifactPath = "tests/ImmichReverseGeo.Tests/processing-run-executor-contracts.json";
const planPath = "openspec/changes/14-cover-executor-independently-from-scheduler/verification-plan.json";
const specPath = "openspec/changes/14-cover-executor-independently-from-scheduler/specs/processing-run-executor-testing/spec.md";
const tasksPath = "openspec/changes/14-cover-executor-independently-from-scheduler/tasks.md";
const artifact = JSON.parse(readFileSync(artifactPath, "utf8"));
const plan = JSON.parse(readFileSync(planPath, "utf8"));

function assert(condition, message) {
  if (!condition) throw new Error(message);
}
function canonical(value) {
  if (Array.isArray(value)) return "[" + value.map(canonical).join(",") + "]";
  if (value && typeof value === "object") {
    return "{" + Object.keys(value).sort().map(key => JSON.stringify(key) + ":" + canonical(value[key])).join(",") + "}";
  }
  return JSON.stringify(value);
}
function semanticDigest(value) {
  return createHash("sha256").update(canonical(value) + "\n", "utf8").digest("hex");
}
function exactArray(left, right, message) {
  assert(canonical(left) === canonical(right), message);
}
function exactKeys(value, keys, path) {
  assert(value && typeof value === "object" && !Array.isArray(value), path + " must be an object");
  const actual = Object.keys(value).sort();
  const expected = [...keys].sort();
  assert(canonical(actual) === canonical(expected), path + " exact keys differ: expected " + expected.join(",") + "; actual " + actual.join(","));
}
function collectKeys(value, path = "$") {
  if (Array.isArray(value)) return value.flatMap((item, index) => collectKeys(item, path + "[" + index + "]"));
  if (!value || typeof value !== "object") return [];
  return Object.entries(value).flatMap(([key, child]) => [{ key, path: path + "." + key }, ...collectKeys(child, path + "." + key)]);
}
function unique(values, label) {
  assert(new Set(values).size === values.length, label + " must be unique");
}

exactKeys(artifact, ["schemaVersion", "authority", "sourceChange", "baseline", "provenance", "scenarioIds", "taskIds", "externalGateIds", "methods", "contracts", "proofBindings"], "authority");
exactKeys(artifact.provenance, ["approvedSources", "rejectedDraftHistory", "runtimeObservationAuthority", "behavioralSourceCount", "equivalenceGate", "auditRound3", "auditRound4", "auditRound5", "auditRound6", "auditRound7"], "provenance");
exactKeys(artifact.provenance.auditRound3, ["result", "targetMisses", "remediationAuthority"], "provenance.auditRound3");
exactKeys(artifact.provenance.auditRound4, ["result", "targetMisses", "remediationAuthority"], "provenance.auditRound4");
exactKeys(artifact.provenance.auditRound5, ["result", "targetMisses", "remediationAuthority"], "provenance.auditRound5");
exactKeys(artifact.provenance.auditRound6, ["result", "healthScore", "targetMisses", "remediationAuthority"], "provenance.auditRound6");
exactKeys(artifact.provenance.auditRound7, ["result", "targetMisses", "remediationAuthority"], "provenance.auditRound7");
assert(artifact.schemaVersion === "8.0.0", "unexpected complete authority schema");
assert(artifact.provenance.runtimeObservationAuthority === false, "runtime observations cannot be authority");
assert(artifact.provenance.behavioralSourceCount === 1, "there must be one behavioral authority");
assert(artifact.provenance.approvedSources.every(item => typeof item === "string"), "approved sources must be strings");
assert(artifact.provenance.auditRound3.targetMisses.every(item => typeof item === "string"), "round3 misses must be strings");
assert(artifact.provenance.auditRound4.targetMisses.every(item => typeof item === "string"), "round4 misses must be strings");
assert(artifact.provenance.auditRound5.targetMisses.every(item => typeof item === "string"), "round5 misses must be strings");
assert(artifact.provenance.auditRound6.healthScore === 80 && artifact.provenance.auditRound6.targetMisses.every(item => typeof item === "string"), "formal pass1 provenance drift");
assert(artifact.provenance.auditRound7.targetMisses.length === 1 && artifact.provenance.auditRound7.targetMisses.every(item => typeof item === "string"), "round7 P0 provenance drift");
assert(plan.contractAuthority.path === artifactPath, "plan points at another authority");
const semanticSha256 = semanticDigest(artifact);
const reorderedRoot = Object.fromEntries(Object.entries(artifact).reverse());
assert(semanticDigest(reorderedRoot) === semanticSha256, "semantic hash depends on source property order");
assert(plan.contractAuthority.semanticSha256 === semanticSha256, "semantic canonical authority SHA differs from plan");
assert(plan.contractAuthority.provenance.semanticSha256 === semanticSha256, "provenance semantic SHA differs");
assert(!Object.hasOwn(plan.contractAuthority, "sha256"), "raw-byte SHA field is forbidden");

assert(artifact.methods.length === 50, "method count must be 50");
assert(artifact.methods.filter(item => item.active).length === 46, "active method count must be 46");
unique(artifact.methods.map(item => item.methodId), "method IDs");
assert(artifact.methods.every(item => item.parameterTypes.every(type => typeof type === "string")), "method parameter types must be strings");
const methodMap = new Map(artifact.methods.map(item => [item.methodId, item]));
for (const method of artifact.methods) {
  exactKeys(method, ["methodId", "declaringType", "parameterTypes", "active"], "method " + method.methodId);
  assert(method.declaringType.startsWith("ImmichReverseGeo.Tests."), "invalid declaring type metadata " + method.methodId);
}

assert(artifact.scenarioIds.length === 43, "scenario count must be 43");
assert(artifact.taskIds.length === 42, "task count must be 42");
assert(artifact.externalGateIds.length === 4, "gate count must be 4");
unique(artifact.scenarioIds, "scenario IDs"); unique(artifact.taskIds, "task IDs"); unique(artifact.externalGateIds, "gate IDs");
exactArray(artifact.externalGateIds, ["GATE-9.2", "GATE-9.3", "GATE-9.4", "GATE-9.5"], "external gate partition drift");
const specScenarios = [...new Set(readFileSync(specPath, "utf8").match(/\bS(?:0[1-9]|[1-3][0-9]|4[0-3])\b/g) ?? [])].sort();
const taskIds = [...readFileSync(tasksPath, "utf8").matchAll(/^- \[[ x]\] (\d+\.\d+) /gm)].map(match => match[1]);
exactArray([...artifact.scenarioIds].sort(), specScenarios, "spec/authority scenario partition drift");
exactArray(artifact.taskIds, taskIds, "tasks/authority partition drift");

const proofTargets = new Map([
  ["FixtureIsolation", ["CompiledStructural", "ExecutorCharacterizationFixture_UsesFixedUtcGatesAndOnlyApprovedInMemoryDependencies"]],
  ["DirectExtractionReuse", ["CompiledStructural", "ExecuteAsync_MixedBranches_ProduceExactCombinedCausalEventsWritesAndAccounting"]],
  ["HostCompositionOutsideFixture", ["ExternalGate", "GATE-9.5"]],
  ["StrictScopeReview", ["ExternalGate", "GATE-9.5"]],
  ["CompiledInventory", ["CompiledStructural", "VerificationManifest_DeclaredMatrixCasesResolveExactlyOnceInCompiledTypes"]],
  ["FocusedExecutorGate", ["ExternalGate", "GATE-9.2"]],
  ["CanonicalSuiteGate", ["ExternalGate", "GATE-9.3"]],
  ["ArchitectureGate", ["ExternalGate", "GATE-9.4"]]
]);
assert(artifact.proofBindings.length === 7, "proof binding count must be 7");
unique(artifact.proofBindings.map(item => item.proofId), "proof IDs");
for (const proof of artifact.proofBindings) {
  exactKeys(proof, ["proofId", "kind", "methodId", "gateId", "scenarioIds", "taskIds", "semanticClauses"], "proof " + proof.proofId);
  assert(proof.scenarioIds.every(id => artifact.scenarioIds.includes(id)), "invalid proof scenario " + proof.proofId);
  assert(proof.taskIds.every(id => artifact.taskIds.includes(id)), "invalid proof task " + proof.proofId);
  assert(proof.semanticClauses.length > 0, "proof has no enforceable clauses " + proof.proofId);
  for (const clause of proof.semanticClauses) {
    const expected = proofTargets.get(clause);
    assert(expected && expected[0] === proof.kind, "unverified proof clause " + proof.proofId + "/" + clause);
    assert((proof.kind === "CompiledStructural" ? proof.methodId : proof.gateId) === expected[1], "proof target drift " + proof.proofId + "/" + clause);
  }
  if (proof.kind === "CompiledStructural") {
    assert(proof.gateId === null && methodMap.get(proof.methodId)?.active === true, "compiled proof target is not active " + proof.proofId);
  } else {
    assert(proof.kind === "ExternalGate" && proof.methodId === null && artifact.externalGateIds.includes(proof.gateId), "external proof gate drift " + proof.proofId);
  }
}
function verifyCompletePartitions(contracts, proofs) {
  for (const id of artifact.scenarioIds) assert(contracts.some(item => item.scenarioIds.includes(id)) || proofs.some(item => item.scenarioIds.includes(id)), "unbound scenario " + id);
  for (const id of artifact.taskIds) assert(contracts.some(item => item.taskIds.includes(id)) || proofs.some(item => item.taskIds.includes(id)), "unbound task " + id);
}
verifyCompletePartitions(artifact.contracts, artifact.proofBindings);
const scenarioRemoval = artifact.proofBindings.map(proof => proof.proofId === "P01-fixture-isolation" ? { ...proof, scenarioIds: [] } : proof);
const taskRemoval = artifact.proofBindings.map(proof => proof.proofId === "P11-compiled-inventory" ? { ...proof, taskIds: [] } : proof);
assertRejected(() => verifyCompletePartitions(artifact.contracts, scenarioRemoval), "removed scenario partition");
assertRejected(() => verifyCompletePartitions(artifact.contracts, taskRemoval), "removed task partition");

assert(artifact.contracts.length === 67, "contract count must be 67");
unique(artifact.contracts.map(item => item.caseId), "case IDs");
const callFields = ["kind", "ordinal", "cursorCreatedAtUtc", "cursorId", "batchSize", "assetId", "delayMs", "detail"];
const effectFields = ["kind", "assetId", "country", "state", "city", "detail"];
const eventFields = ["kind", "timestampUtc", "eligibleCount", "level", "message", "processedCount", "updatedCount", "skippedCount", "failedCount", "label", "outcome", "failureMessage", "requestSame", "resultSame"];
const resultFields = ["returned", "requestRunId", "startedAtUtc", "endedAtUtc", "processedCount", "updatedCount", "skippedCount", "failedCount", "outcome", "failureMessage", "propagatedException"];
const fallbackFields = ["inputCountry", "inputState", "inputCity", "inputHasMatch", "outputCountry", "outputState", "outputCity", "outputHasMatch", "guardMatched"];
const structuralCaseId = "unreachable-no-city-guard";
const exactRetries = ["Batch", "Persistence", "ReporterTerminal", "Rollback", "Compensation", "CrossStoreTransaction"];
function verifyBehavioralCommon(contract) {
  assert(contract.fallbackShapes.length === 0, "behavioral fallback shapes must be empty " + contract.caseId);
}
function verifyStructuralCommon(contract) {
  assert(contract.caseId === structuralCaseId, "structural case ID drift");
  exactArray(contract.scenarioIds, ["S16"], "structural scenarios drift");
  exactArray(contract.taskIds, ["5.1", "5.6"], "structural tasks drift");
  assert(contract.binding.declaringType === "ImmichReverseGeo.Tests.ProcessingRunExecutorChange11Tests", "structural declaring type drift");
  assert(contract.binding.methodId === "WithFallbackCity_MakesLoggerOnlyNoCityExecutorBranchUnreachableForEveryHasMatchShape", "structural method drift");
  assert(contract.binding.bindingKind === "no-argument" && contract.binding.parameterSignature === "()" && contract.binding.dynamicDataMember === null && contract.binding.orderedArguments.length === 0, "structural binding drift");
  assert(contract.semantics === "Ordered", "structural semantics drift");
  assert(contract.assets.length === 0, "structural assets must be empty");
  assert(contract.effectIdentities.length === 0, "structural effect identities must be empty");
  assert(["additionalCalls", "additionalEffects", "additionalAttemptedEvents", "additionalAcceptedEvents", "additionalLogs", "additionalAssets", "additionalDispositions"].every(key => contract.forbidden[key] === true), "structural forbidden boolean drift");
  exactArray(contract.forbidden.retries, exactRetries, "structural retries drift");
  assert(contract.expectedTokens.length === 0, "structural expected tokens must be empty");
  exactArray(contract.cleanup, { sessionConstructed: false, sessionReturned: false, terminalAttempted: false, terminalAccepted: false, activitiesBalanced: true }, "structural cleanup drift");
  assert(contract.causalEdges.length === 0, "structural causal edges must be empty");
  assert(contract.seamExceptions.length === 0, "structural seam exceptions must be empty");
  assert(contract.noExtras === true, "structural noExtras drift");
}
function assertRejected(action, label) {
  let rejected = false;
  try { action(); } catch { rejected = true; }
  assert(rejected, "mutation was not consumed: " + label);
}
const scenarioSet = new Set(artifact.scenarioIds), taskSet = new Set(artifact.taskIds);
for (const contract of artifact.contracts) {
  exactKeys(contract, ["caseId", "scenarioIds", "taskIds", "binding", "semantics", "calls", "effects", "attemptedEvents", "acceptedEvents", "logs", "result", "assets", "effectIdentities", "dispositions", "causalEdges", "seamExceptions", "forbidden", "expectedTokens", "cleanup", "fallbackShapes", "noExtras"], "contract " + contract.caseId);
  if (contract.caseId !== structuralCaseId) verifyBehavioralCommon(contract);
  assert(contract.scenarioIds.length > 0 && contract.scenarioIds.every(id => scenarioSet.has(id)), "invalid scenario refs " + contract.caseId);
  assert(contract.taskIds.length > 0 && contract.taskIds.every(id => taskSet.has(id)), "invalid task refs " + contract.caseId);
  const method = methodMap.get(contract.binding.methodId);
  assert(method?.active === true, "binding does not reference active method " + contract.caseId);
  exactKeys(contract.binding, ["declaringType", "methodId", "bindingKind", "parameterSignature", "dynamicDataMember", "orderedArguments"], "binding " + contract.caseId);
  assert(contract.binding.declaringType === method.declaringType, "binding declaring type drift " + contract.caseId);
  assert(contract.binding.parameterSignature === "(" + method.parameterTypes.join(",") + ")", "binding signature drift " + contract.caseId);
  assert(contract.binding.orderedArguments.length === method.parameterTypes.length, "ordered argument count drift " + contract.caseId);
  contract.binding.orderedArguments.forEach((arg, index) => {
    exactKeys(arg, ["type", "value"], "argument " + contract.caseId + "/" + index);
    assert(arg.type === method.parameterTypes[index], "ordered argument type drift " + contract.caseId);
  });
  if (contract.binding.bindingKind === "typed-case-table") {
    assert(typeof contract.binding.dynamicDataMember === "string" && contract.binding.dynamicDataMember.length > 0, "missing DynamicData mapping metadata " + contract.caseId);
  } else {
    assert(contract.binding.dynamicDataMember === null, "unexpected dynamic member metadata " + contract.caseId);
  }
  contract.calls.forEach((item, i) => exactKeys(item, callFields, "call " + contract.caseId + "/" + i));
  contract.effects.forEach((item, i) => exactKeys(item, effectFields, "effect " + contract.caseId + "/" + i));
  contract.attemptedEvents.forEach((item, i) => { exactKeys(item, eventFields, "attempt " + contract.caseId + "/" + i); assert(item.resultSame === null || typeof item.resultSame === "boolean", "attempt resultSame must be nullable boolean " + contract.caseId + "/" + i); });
  contract.acceptedEvents.forEach((item, i) => { exactKeys(item, eventFields, "event " + contract.caseId + "/" + i); assert(item.resultSame === null || typeof item.resultSame === "boolean", "event resultSame must be nullable boolean " + contract.caseId + "/" + i); });
  contract.logs.forEach((item, i) => exactKeys(item, ["sink", "level", "message", "exceptionType", "exceptionMessage"], "log " + contract.caseId + "/" + i));
  exactKeys(contract.result, resultFields, "result " + contract.caseId);
  if (contract.result.propagatedException !== null) exactKeys(contract.result.propagatedException, ["type", "message", "sameReference"], "propagated exception " + contract.caseId);
  exactKeys(contract.cleanup, ["sessionConstructed", "sessionReturned", "terminalAttempted", "terminalAccepted", "activitiesBalanced"], "cleanup " + contract.caseId);
  contract.dispositions.forEach((item, i) => { exactKeys(item, ["assetOrdinal", "outcome", "processedCount", "updatedCount", "skippedCount", "failedCount"], "disposition " + contract.caseId + "/" + i); assert(item.assetOrdinal > 0 && item.assetOrdinal <= contract.assets.length, "disposition asset ref drift " + contract.caseId); });
  contract.seamExceptions.forEach((item, i) => exactKeys(item, ["kind", "assetOrdinal", "type", "message", "cancellationOwner"], "exception " + contract.caseId + "/" + i));
  contract.expectedTokens.forEach((item, i) => { exactKeys(item, ["source", "index", "role"], "token " + contract.caseId + "/" + i); const length = item.source === "Call" ? contract.calls.length : contract.attemptedEvents.length; assert(item.index >= 0 && item.index < length, "token ref drift " + contract.caseId); });
  contract.causalEdges.forEach((edge, i) => { exactKeys(edge, ["before", "after"], "edge " + contract.caseId + "/" + i); for (const point of [edge.before, edge.after]) { exactKeys(point, ["kind", "index"], "edge point " + contract.caseId); const lengths = { Call: contract.calls.length, AcceptedEvent: contract.acceptedEvents.length, Disposition: contract.dispositions.length }; assert(point.index >= 0 && point.index < lengths[point.kind], "edge ref drift " + contract.caseId); } });
  contract.fallbackShapes.forEach((item, i) => exactKeys(item, fallbackFields, "fallback " + contract.caseId + "/" + i));
  assert(contract.noExtras === true, "noExtras required " + contract.caseId);
  exactKeys(contract.forbidden, ["additionalCalls", "additionalEffects", "additionalAttemptedEvents", "additionalAcceptedEvents", "additionalLogs", "additionalAssets", "additionalDispositions", "retries"], "forbidden " + contract.caseId);
  assert(["additionalCalls", "additionalEffects", "additionalAttemptedEvents", "additionalAcceptedEvents", "additionalLogs", "additionalAssets", "additionalDispositions"].every(key => contract.forbidden[key] === true), "forbidden extras incomplete " + contract.caseId);
  assert(typeof contract.semantics === "string" && ["Ordered", "ConcurrentSet"].includes(contract.semantics), "semantics must be a known string " + contract.caseId);
  assert(Array.isArray(contract.assets) && contract.assets.every(item => typeof item === "string"), "assets must be strings " + contract.caseId);
  assert(Array.isArray(contract.effectIdentities) && contract.effectIdentities.every(item => typeof item === "string"), "effect identities must be strings " + contract.caseId);
  assert(Array.isArray(contract.forbidden.retries) && contract.forbidden.retries.every(item => typeof item === "string"), "retries must be strings " + contract.caseId);
}
function verifyFinishRejectionCorrelation(contract) {
  assert(contract.caseId === "reporter-finish-rejection", "finish rejection case drift");
  assert(contract.result.returned === false && contract.result.requestRunId === null, "finish rejection must return no result");
  assert(contract.result.propagatedException?.type === "ImmichReverseGeo.Tests.ProcessingRunExecutorChange11Tests+TestSinkException"
    && contract.result.propagatedException.message === "terminal sink failed" && contract.result.propagatedException.sameReference === true, "finish rejection sink identity drift");
  const finishes = contract.attemptedEvents.filter(item => item.kind === "RunFinished");
  assert(finishes.length === 1, "finish rejection exact attempt count drift");
  exactArray(finishes[0], { kind: "RunFinished", timestampUtc: null, eligibleCount: null, level: null, message: null,
    processedCount: 0, updatedCount: 0, skippedCount: 0, failedCount: 0, label: null, outcome: "Completed",
    failureMessage: null, requestSame: true, resultSame: null }, "finish rejection attempted terminal content drift");
  assert(contract.acceptedEvents.every(item => item.kind !== "RunFinished"), "finish rejection cannot accept terminal event");
  assert(contract.cleanup.terminalAttempted === true && contract.cleanup.terminalAccepted === false, "finish rejection cleanup drift");
  exactArray(contract.forbidden.retries, exactRetries, "finish rejection retry/recursion drift");
}
const finishRejection = artifact.contracts.find(item => item.caseId === "reporter-finish-rejection");
verifyFinishRejectionCorrelation(finishRejection);
const finishIndex = finishRejection.attemptedEvents.findIndex(item => item.kind === "RunFinished");
const falseTrueMutation = { ...finishRejection, attemptedEvents: finishRejection.attemptedEvents.map((item, index) => index === finishIndex ? { ...item, resultSame: true } : item) };
assertRejected(() => verifyFinishRejectionCorrelation(falseTrueMutation), "null finish correlation changed to true");

const s16 = artifact.contracts.find(item => item.caseId === structuralCaseId);
verifyStructuralCommon(s16);
assert(s16.fallbackShapes.length === 5, "S16 must contain five exact typed fallback shapes");
assert(s16.fallbackShapes.every(item => item.inputHasMatch && item.outputHasMatch && !item.guardMatched), "S16 input/output/HasMatch/guard drift");
const behavioralSentinel = artifact.contracts.find(item => item.caseId === "positive-immediate-empty");
assertRejected(() => verifyBehavioralCommon({ ...behavioralSentinel, fallbackShapes: s16.fallbackShapes }), "behavioral fallback row");
const structuralMutations = [
  ["semantics", { ...s16, semantics: "ConcurrentSet" }],
  ["assets", { ...s16, assets: ["00000000-0000-0000-0000-000000000001"] }],
  ["effectIdentities", { ...s16, effectIdentities: ["00000000-0000-0000-0000-000000000001"] }],
  ["forbiddenBoolean", { ...s16, forbidden: { ...s16.forbidden, additionalCalls: false } }],
  ["forbiddenRetries", { ...s16, forbidden: { ...s16.forbidden, retries: [] } }],
  ["expectedTokens", { ...s16, expectedTokens: [{ source: "Call", index: 0, role: "Run" }] }],
  ["cleanup", { ...s16, cleanup: { ...s16.cleanup, sessionConstructed: true } }],
  ["causalEdges", { ...s16, causalEdges: [{ before: { kind: "Call", index: 0 }, after: { kind: "Call", index: 0 } }] }],
  ["seamExceptions", { ...s16, seamExceptions: [{ kind: "Count", assetOrdinal: null, type: "System.Exception", message: null, cancellationOwner: null }] }]
];
for (const [label, mutation] of structuralMutations) assertRejected(() => verifyStructuralCommon(mutation), "structural " + label);

const mapping = { scenarioIds: artifact.scenarioIds, taskIds: artifact.taskIds, externalGateIds: artifact.externalGateIds, methods: artifact.methods.map(({ methodId, declaringType, parameterTypes, active }) => ({ methodId, declaringType, parameterTypes, active })), cases: artifact.contracts.map(({ caseId, scenarioIds, taskIds, binding }) => ({ caseId, scenarioIds, taskIds, binding, verificationKind: caseId === structuralCaseId ? "structural-fallback" : "behavioral" })), proofBindings: artifact.proofBindings, scenarioPartition: artifact.scenarioIds.map(id => ({ id, caseIds: artifact.contracts.filter(item => item.scenarioIds.includes(id)).map(item => item.caseId), proofIds: artifact.proofBindings.filter(item => item.scenarioIds.includes(id)).map(item => item.proofId) })), taskPartition: artifact.taskIds.map(id => ({ id, caseIds: artifact.contracts.filter(item => item.taskIds.includes(id)).map(item => item.caseId), proofIds: artifact.proofBindings.filter(item => item.taskIds.includes(id)).map(item => item.proofId) })) };
assert(mapping.scenarioPartition.every(item => item.caseIds.length + item.proofIds.length > 0), "empty scenario mapping partition");
assert(mapping.taskPartition.every(item => item.caseIds.length + item.proofIds.length > 0), "empty task mapping partition");
exactArray(plan.mappingAuthority.table, mapping, "inspectable plan/authority mapping table drift");
const mappingDigestSha256 = semanticDigest(mapping);
assert(plan.mappingAuthority.digestSha256 === mappingDigestSha256, "plan mapping digest drift");
assert(plan.equivalenceGate.expected.mappingDigestSha256 === mappingDigestSha256, "expected mapping digest drift");
exactArray(plan.mappingAuthority.table.scenarioIds, artifact.scenarioIds, "plan scenario mapping drift");
exactArray(plan.mappingAuthority.table.taskIds, artifact.taskIds, "plan task mapping drift");

const rejected = new Set(["cardinalities", "partialedges", "expandedseams", "capturedsnapshot", "countprefix", "specificationpoint", "actualderived", "progressstates"]);
const badKeys = collectKeys(artifact).filter(item => rejected.has(item.key.toLowerCase()));
assert(badKeys.length === 0, "rejected authority keys: " + badKeys.map(item => item.path).join(", "));
console.log(JSON.stringify({ ok: true, semanticSha256, mappingDigestSha256, contracts: 67, proofBindings: 7, methods: 50, activeMethods: 46, scenarios: 43, tasks: 42, gates: 4, authority: artifact.authority }));
