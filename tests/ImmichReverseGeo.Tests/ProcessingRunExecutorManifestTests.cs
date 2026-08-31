using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change14")]
public class ProcessingRunExecutorManifestTests
{
    [TestMethod]
    public void VerificationManifest_PartitionsAllScenariosTasksCasesAndExternalGatesExactlyOnce()
    {
        Assert.AreEqual(43, ExecutorVerificationCatalog.ScenarioIds.Count);
        Assert.AreEqual(43, ExecutorVerificationCatalog.ScenarioIds.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(42, ExecutorVerificationCatalog.TaskIds.Count);
        Assert.AreEqual(42, ExecutorVerificationCatalog.TaskIds.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(4, ExecutorVerificationCatalog.ExternalGateIds.Count);
        Assert.AreEqual(4, ExecutorVerificationCatalog.ExternalGateIds.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(50, ExecutorVerificationCatalog.Methods.Count);
        Assert.AreEqual(46, ExecutorVerificationCatalog.Methods.Count(method => method.Active));
        Assert.AreEqual(67, ExecutorVerificationCatalog.Bindings.Count);
        Assert.AreEqual(67, ExecutorContractAuthority.Cases.Count);
        Assert.AreEqual(7, ExecutorVerificationCatalog.ProofBindings.Count);
        ExecutorVerificationCatalog.AssertCompletePartitionsForSchemaTest(ExecutorContractAuthority.Cases.Values, ExecutorVerificationCatalog.ProofBindings);
        CollectionAssert.AreEquivalent(ExecutorVerificationCatalog.Bindings.Keys.ToArray(), ExecutorContractAuthority.Cases.Keys.ToArray());
        Assert.IsTrue(ExecutorContractAuthority.Cases.Values.All(contract => contract.NoExtras));
        Assert.IsTrue(ExecutorVerificationCatalog.Bindings.Values.All(binding => ExecutorVerificationCatalog.Methods.Any(method => method.MethodId == binding.MethodId && method.Active)));
    }

    [TestMethod]
    public void VerificationManifest_ProofBindingsResolveTypedMethodsGatesAndSemanticClauses()
    {
        var assembly = typeof(ProcessingRunExecutorManifestTests).Assembly;
        var expected = new Dictionary<ExecutorProofClause, (ExecutorProofKind Kind, string Target)>
        {
            [ExecutorProofClause.FixtureIsolation] = (ExecutorProofKind.CompiledStructural, "ExecutorCharacterizationFixture_UsesFixedUtcGatesAndOnlyApprovedInMemoryDependencies"),
            [ExecutorProofClause.DirectExtractionReuse] = (ExecutorProofKind.CompiledStructural, "ExecuteAsync_MixedBranches_ProduceExactCombinedCausalEventsWritesAndAccounting"),
            [ExecutorProofClause.HostCompositionOutsideFixture] = (ExecutorProofKind.ExternalGate, "GATE-9.5"),
            [ExecutorProofClause.StrictScopeReview] = (ExecutorProofKind.ExternalGate, "GATE-9.5"),
            [ExecutorProofClause.CompiledInventory] = (ExecutorProofKind.CompiledStructural, "VerificationManifest_DeclaredMatrixCasesResolveExactlyOnceInCompiledTypes"),
            [ExecutorProofClause.FocusedExecutorGate] = (ExecutorProofKind.ExternalGate, "GATE-9.2"),
            [ExecutorProofClause.CanonicalSuiteGate] = (ExecutorProofKind.ExternalGate, "GATE-9.3"),
            [ExecutorProofClause.ArchitectureGate] = (ExecutorProofKind.ExternalGate, "GATE-9.4")
        };
        foreach (var proof in ExecutorVerificationCatalog.ProofBindings)
        {
            foreach (var clause in proof.SemanticClauses)
            {
                Assert.IsTrue(expected.TryGetValue(clause, out var binding), proof.ProofId);
                Assert.AreEqual(proof.Kind, binding.Kind, proof.ProofId);
                Assert.AreEqual(proof.Kind == ExecutorProofKind.CompiledStructural ? proof.MethodId : proof.GateId, binding.Target, proof.ProofId);
            }
            if (proof.Kind == ExecutorProofKind.ExternalGate)
            {
                Assert.IsNull(proof.MethodId, proof.ProofId);
                Assert.IsTrue(ExecutorVerificationCatalog.ExternalGateIds.Contains(proof.GateId!), proof.ProofId);
                continue;
            }
            Assert.IsNull(proof.GateId, proof.ProofId);
            var methodContract = ExecutorVerificationCatalog.Methods.Single(method => method.Active && method.MethodId == proof.MethodId);
            var method = ResolveMethod(assembly, methodContract);
            Assert.IsNotNull(method.GetCustomAttribute<TestMethodAttribute>(), proof.ProofId);
            var members = TransitiveIlWalker.Walk([method], assembly).Members.OfType<MethodBase>().ToArray();
            Assert.IsTrue(members.Any(member => member.DeclaringType == typeof(Assert)), proof.ProofId);
            if (proof.SemanticClauses.Contains(ExecutorProofClause.DirectExtractionReuse))
            {
                Assert.IsTrue(members.Any(member => member.DeclaringType == typeof(ProcessingRunExecutor) && member.Name == nameof(ProcessingRunExecutor.ExecuteAsync)), proof.ProofId);
            }
        }
    }

    [TestMethod]
    public void VerificationManifest_RemovingOneScenarioOrTaskBindingFailsClosed()
    {
        var proofs = ExecutorVerificationCatalog.ProofBindings.ToArray();
        var missingScenario = proofs.Select(proof => proof.ProofId == "P01-fixture-isolation"
            ? proof with { ScenarioIds = ImmutableArray<string>.Empty } : proof).ToArray();
        var missingTask = proofs.Select(proof => proof.ProofId == "P11-compiled-inventory"
            ? proof with { TaskIds = ImmutableArray<string>.Empty } : proof).ToArray();
        Assert.ThrowsExactly<AssertFailedException>(() => ExecutorVerificationCatalog.AssertCompletePartitionsForSchemaTest(ExecutorContractAuthority.Cases.Values, missingScenario));
        Assert.ThrowsExactly<AssertFailedException>(() => ExecutorVerificationCatalog.AssertCompletePartitionsForSchemaTest(ExecutorContractAuthority.Cases.Values, missingTask));
    }

    [TestMethod]
    public void DirectProofMethods_AreTestMethodsAndNeverInvokeAnotherTestMethod()
    {
        var assembly = typeof(ProcessingRunExecutorManifestTests).Assembly;
        var activeMethods = ResolveActiveMethods(assembly).ToArray();
        foreach (var method in activeMethods)
        {
            Assert.IsNotNull(method.GetCustomAttribute<TestMethodAttribute>(), method.Name);
            var references = TransitiveIlWalker.Walk([method], assembly).Members.OfType<MethodInfo>().ToArray();
            Assert.IsFalse(references.Any(candidate => candidate != method && candidate.GetCustomAttribute<TestMethodAttribute>() is not null), $"{method.Name} invokes another TestMethod.");
        }
    }

    [TestMethod]
    public void DirectProofMethods_ConstructExecutorAndDoNotDiscardExecuteAsyncResultsThroughWrappers()
    {
        var assembly = typeof(ProcessingRunExecutorManifestTests).Assembly;
        foreach (var method in ResolveActiveMethods(assembly).Where(method => method.Name.StartsWith("ExecuteAsync_", StringComparison.Ordinal)))
        {
            var references = TransitiveIlWalker.Walk([method], assembly).Members.OfType<MethodInfo>().ToArray();
            Assert.IsTrue(references.Any(candidate => candidate.DeclaringType == typeof(ProcessingRunExecutor) && candidate.Name == nameof(ProcessingRunExecutor.ExecuteAsync)), method.Name);
            var bindingCases = ExecutorVerificationCatalog.Bindings.Values.Where(binding => binding.MethodId == method.Name).ToArray();
            if (bindingCases.Length > 0)
            {
                Assert.IsTrue(references.Any(candidate => candidate.DeclaringType == typeof(ExecutorCaseContractEngine) && candidate.Name.StartsWith("Verify", StringComparison.Ordinal)), method.Name);
            }
        }
        var wrapper = typeof(ExecutorFixture).GetMethod(nameof(ExecutorFixture.ExecuteAsync), BindingFlags.Instance | BindingFlags.Public)!;
        Assert.AreEqual(typeof(Task<ProcessingRunResult>), wrapper.ReturnType);
        Assert.AreEqual(typeof(Task<ProcessingRunResult>), typeof(ProcessingRunExecution).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(method => method.Name == nameof(ProcessingRunExecution.RunOnceAsync) && method.GetParameters().Length == 6).ReturnType);
    }

    [TestMethod]
    public void VerificationManifest_ContainsNoPlaceholderOrSourceGrepProof()
    {
        Assert.IsTrue(ExecutorVerificationCatalog.Methods.All(method => !method.MethodId.EndsWith("_TODO", StringComparison.OrdinalIgnoreCase) && !method.MethodId.EndsWith("_TBD", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(ExecutorContractAuthority.Cases.Values.All(contract => contract.Forbidden.Retries.Length == 6 && contract.NoExtras));
        var assembly = typeof(ProcessingRunExecutorManifestTests).Assembly;
        foreach (var method in ResolveActiveMethods(assembly))
        {
            var references = TransitiveIlWalker.Walk([method], assembly).Members;
            Assert.IsFalse(references.Any(member => member.DeclaringType == typeof(File) || member.DeclaringType == typeof(Directory) || member.DeclaringType == typeof(Path)), method.Name);
        }
    }

    [TestMethod]
    public void VerificationManifest_DeclaredMatrixCasesResolveExactlyOnceInCompiledTypes()
    {
        var assembly = typeof(ProcessingRunExecutorManifestTests).Assembly;
        foreach (var methodContract in ExecutorVerificationCatalog.Methods.Where(item => item.Active))
        {
            var method = ResolveMethod(assembly, methodContract);
            var bindings = ExecutorVerificationCatalog.Bindings.Values.Where(binding => binding.MethodId == methodContract.MethodId).ToArray();
            foreach (var binding in bindings)
            {
                if (binding.BindingKind == "no-argument")
                {
                    Assert.AreEqual(0, method.GetParameters().Length);
                }
                else if (binding.BindingKind == "DataRow")
                {
                    Assert.AreEqual(1, method.GetCustomAttributesData().Where(attribute => attribute.AttributeType == typeof(DataRowAttribute)).Count(attribute => DataRowValues(attribute).SequenceEqual(binding.OrderedArguments)), binding.CaseId);
                }
                else
                {
                    Assert.AreEqual("typed-case-table", binding.BindingKind);
                    var attribute = method.GetCustomAttributesData().Single(item => item.AttributeType == typeof(DynamicDataAttribute));
                    var memberName = (string)attribute.ConstructorArguments[0].Value!;
                    var declaredType = attribute.NamedArguments.SingleOrDefault(item => item.MemberName == "DeclaringType").TypedValue.Value as Type;
                    var sourceType = declaredType ?? method.DeclaringType!;
                    var rows = ReadDynamicRows(sourceType, memberName).ToArray();
                    Assert.AreEqual(1, rows.Count(row => row.SequenceEqual(binding.OrderedArguments)), binding.CaseId);
                    CollectionAssert.AreEquivalent(bindings.Select(item => item.CaseId).ToArray(), rows.Select(row => (string)row[0]!).ToArray(), method.Name);
                }
            }
        }
    }

    [TestMethod]
    public void ExecutorEventCorrelation_ResultSameIsNullWithoutReturnAndExactByReferenceWithReturn()
    {
        var request = new ProcessingRunRequest(Guid.Parse("11111111-1111-1111-1111-111111111111"), ProcessingRunTrigger.Manual);
        var started = new DateTimeOffset(2026, 8, 31, 13, 52, 21, TimeSpan.Zero);
        var result = new ProcessingRunResult(request, started, started.AddSeconds(1), 0, 0, 0, 0, ProcessingRunOutcome.Completed, null);
        var distinct = new ProcessingRunResult(request, started, started.AddSeconds(1), 0, 0, 0, 0, ProcessingRunOutcome.Completed, null);
        var finished = new RunFinished(request, result);
        var correlation = new ExecutorEventCorrelation();

        Assert.IsNull(correlation.Create(finished, request, null).ResultSame);
        Assert.AreEqual(true, correlation.Create(finished, request, result).ResultSame);
        Assert.AreEqual(false, correlation.Create(finished, request, distinct).ResultSame);
    }

    [TestMethod]
    public void EmbeddedAuthority_StrictSchemaRejectsUnknownTopLevelAndNestedBehaviorKeys()
    {
        var json = ExecutorContractAuthority.ReadEmbeddedJsonForSchemaTest();
        var unknownTopLevel = "{\"unknownTopLevel\":true," + json[1..];
        var nestedIndex = json.IndexOf("\"caseId\":", StringComparison.Ordinal);
        Assert.IsTrue(nestedIndex > 0);
        var unknownNested = json.Insert(nestedIndex, "\"unknownBehavior\":true,");

        Assert.ThrowsExactly<JsonException>(() => ExecutorContractAuthority.DeserializeForSchemaTest(unknownTopLevel));
        Assert.ThrowsExactly<JsonException>(() => ExecutorContractAuthority.DeserializeForSchemaTest(unknownNested));
    }

    [TestMethod]
    public void ContractEngine_RejectsBehavioralFallbackAndEveryStructuralCommonMutation()
    {
        const string structuralCaseId = "unreachable-no-city-guard";
        var structural = ExecutorContractAuthority.Cases[structuralCaseId];
        var behavioral = ExecutorContractAuthority.Cases["positive-immediate-empty"];
        Assert.ThrowsExactly<AssertFailedException>(() => ExecutorCaseContractEngine.VerifyBehavioralCommonForSchemaTest(
            behavioral with { FallbackShapes = structural.FallbackShapes }, behavioral.CaseId));

        var mutations = new (string Name, ExecutorCaseContract Contract)[]
        {
            ("semantics", structural with { Semantics = ContractSemantics.ConcurrentSet }),
            ("assets", structural with { Assets = ImmutableArray.Create(Guid.Parse("00000000-0000-0000-0000-000000000001")) }),
            ("effect-identities", structural with { EffectIdentities = ImmutableArray.Create(Guid.Parse("00000000-0000-0000-0000-000000000001")) }),
            ("forbidden-boolean", structural with { Forbidden = structural.Forbidden with { AdditionalCalls = false } }),
            ("forbidden-retries", structural with { Forbidden = structural.Forbidden with { Retries = ImmutableArray<string>.Empty } }),
            ("expected-tokens", structural with { ExpectedTokens = ImmutableArray.Create(new ExecutorTokenContract(TokenSourceKind.Call, 0, TokenRole.Run)) }),
            ("cleanup", structural with { Cleanup = structural.Cleanup with { SessionConstructed = true } }),
            ("causal-edges", structural with { CausalEdges = ImmutableArray.Create(new ExecutorCausalEdgeContract(new ExecutorEdgePointContract(ExecutorEdgePointKind.Call, 0), new ExecutorEdgePointContract(ExecutorEdgePointKind.Call, 0))) }),
            ("seam-exceptions", structural with { SeamExceptions = ImmutableArray.Create(new ExecutorSeamExceptionContract(ExecutorCallKind.Count, null, typeof(Exception).FullName!, null, null)) })
        };

        foreach (var mutation in mutations)
        {
            Assert.ThrowsExactly<AssertFailedException>(
                () => ExecutorCaseContractEngine.VerifyStructuralCommonForSchemaTest(mutation.Contract, structuralCaseId),
                mutation.Name);
        }
    }

    private static IEnumerable<MethodInfo> ResolveActiveMethods(Assembly assembly) => ExecutorVerificationCatalog.Methods.Where(item => item.Active).Select(item => ResolveMethod(assembly, item));
    private static MethodInfo ResolveMethod(Assembly assembly, CompiledMethodContract item)
    {
        var type = assembly.GetType(item.DeclaringType, throwOnError: true)!;
        return type.GetMethod(item.MethodId, BindingFlags.Instance | BindingFlags.Public, null, item.ParameterTypes.Select(ExecutorVerificationCatalog.ResolveType).ToArray(), null)
            ?? throw new AssertFailedException($"Missing compiled method {item.DeclaringType}.{item.MethodId}.");
    }
    private static IEnumerable<object?[]> ReadDynamicRows(Type sourceType, string memberName)
    {
        var property = sourceType.GetProperty(memberName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        object? value = property?.GetValue(null);
        if (property is null)
        {
            var method = sourceType.GetMethod(memberName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                ?? throw new AssertFailedException($"DynamicData member {sourceType.FullName}.{memberName} is missing.");
            value = method.Invoke(null, null);
        }
        return ((IEnumerable)value!).Cast<object?[]>();
    }
    private static IEnumerable<object?> DataRowValues(CustomAttributeData attribute)
    {
        if (attribute.ConstructorArguments.Count == 1 && attribute.ConstructorArguments[0].Value is IReadOnlyCollection<CustomAttributeTypedArgument> packed)
        {
            return packed.Select(argument => argument.Value);
        }
        return attribute.ConstructorArguments.Select(argument => argument.Value);
    }
}
