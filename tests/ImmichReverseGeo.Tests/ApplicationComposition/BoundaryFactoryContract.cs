using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

internal sealed record BoundaryFactoryContract(string Key, string Owner, string Service, string Lifetime, string[] Dependencies)
{
    internal static BoundaryFactoryContract Describe(ServiceDescriptor descriptor)
    {
        Delegate factory = (descriptor.IsKeyedService ? descriptor.KeyedImplementationFactory : descriptor.ImplementationFactory)!;
        string owner = factory.Method.DeclaringType?.FullName ?? "<dynamic>";
        string key = descriptor.ServiceType + "|" + owner + "." + factory.Method.Name
            + (descriptor.IsKeyedService ? "|key:" + (descriptor.ServiceKey as string ?? descriptor.ServiceKey?.GetType().FullName) : "");
        return new(key, owner, descriptor.ServiceType.ToString(), descriptor.Lifetime.ToString(),
            factory.Method.DeclaringType is null ? ["<unavailable metadata>"]
                : BoundaryIlMetadata.FactoryDependencies(factory).Select(t => t.ToString()).Order(StringComparer.Ordinal).ToArray());
    }

    internal static IReadOnlyList<BoundaryDiagnostic> Validate(BoundaryRole role,
        IEnumerable<BoundaryFactoryContract> actual, IEnumerable<BoundaryFactoryContract> declarations)
    {
        var declared = declarations.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var failures = new List<BoundaryDiagnostic>();
        foreach (BoundaryFactoryContract factory in actual)
        {
            if (!declared.TryGetValue(factory.Key, out BoundaryFactoryContract? contract))
            {
                failures.Add(ControlPlaneDependencyPolicy.Diagnostic("UnclassifiedFactory", role, factory.Owner,
                    "factory metadata", factory.Service, factory.Key));
            }
            else if (factory.Owner != contract.Owner || factory.Service != contract.Service || factory.Lifetime != contract.Lifetime
                || !factory.Dependencies.SequenceEqual(contract.Dependencies, StringComparer.Ordinal))
            {
                failures.Add(ControlPlaneDependencyPolicy.Diagnostic("FactoryContractMismatch", role, factory.Owner,
                    "factory metadata", factory.Service, factory.Key + " -> compiled dependencies " + string.Join(", ", factory.Dependencies)));
            }
        }
        return ControlPlaneDependencyPolicy.Sort(failures);
    }
}

[TestClass]
[TestCategory("Change56")]
public sealed class ControlPlaneFactoryPolicyTests
{
    [TestMethod]
    public void EveryProductionApplicationFactory_MatchesItsReviewedMetadataDeclaration()
    {
        var all = new List<(BoundaryRole Role, BoundaryFactoryContract Contract)>();
        foreach (BoundaryRole role in new[] { BoundaryRole.Standard, BoundaryRole.WebOnly })
        {
            using var fixture = ControlPlaneRolePolicyTests.RoleDescriptors.Create(role);
            all.AddRange(fixture.Services.Where(d =>
            {
                Delegate? factory = d.IsKeyedService ? d.KeyedImplementationFactory : d.ImplementationFactory;
                return factory is not null && (ControlPlaneDependencyPolicy.IsApplication(d.ServiceType)
                    || ControlPlaneDependencyPolicy.IsApplication(factory.Method.DeclaringType));
            }).Select(d => (role, BoundaryFactoryContract.Describe(d))));
        }
        BoundaryFactoryContract[] actual = all.Select(item => item.Contract).DistinctBy(c => c.Key).OrderBy(c => c.Key, StringComparer.Ordinal).ToArray();
        string root = WebControlPlaneGuardTests.FindRoot();
        string manifest = Path.Combine(root, "tests", "ImmichReverseGeo.Tests", "ApplicationComposition", "control-plane-factories.json");
        string observed = Path.Combine(root, "_out", "execution", "56", "factory-catalog-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(observed)!);
        File.WriteAllText(observed, JsonSerializer.Serialize(actual, new JsonSerializerOptions { WriteIndented = true }));
        Assert.IsTrue(File.Exists(manifest), "Review compiled factory metadata and declare the exact contracts: " + observed);
        BoundaryFactoryContract[] declared = JsonSerializer.Deserialize<BoundaryFactoryContract[]>(File.ReadAllText(manifest))!;
        IReadOnlyList<BoundaryDiagnostic> failures = all.GroupBy(item => item.Role)
            .SelectMany(group => BoundaryFactoryContract.Validate(group.Key, group.Select(item => item.Contract), declared)).ToArray();
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
        CollectionAssert.AreEquivalent(declared.Select(c => c.Key).ToArray(), actual.Select(c => c.Key).ToArray(), "Stale declarations must be removed, not retained as exemptions.");
    }

    [TestMethod]
    public void MissingDishonestAndStaleFactoryDeclarations_CannotApproveAnImplementation()
    {
        var valid = new BoundaryFactoryContract("root", "approved owner", "approved interface", "Singleton", ["lightweight dependency"]);
        Assert.IsEmpty(BoundaryFactoryContract.Validate(BoundaryRole.WebOnly, [valid], [valid]));
        Assert.AreEqual("UnclassifiedFactory", BoundaryFactoryContract.Validate(BoundaryRole.WebOnly, [valid], []).Single().Rule);
        var dishonest = valid with { Dependencies = ["ImmichReverseGeo.Overture.Services.OvertureDivisionsService"] };
        BoundaryDiagnostic failure = BoundaryFactoryContract.Validate(BoundaryRole.WebOnly, [dishonest], [valid]).Single();
        Assert.AreEqual("FactoryContractMismatch", failure.Rule);
        Assert.AreEqual(BoundaryRole.WebOnly, failure.Role);
        Assert.AreEqual(valid.Owner, failure.Root);
        Assert.AreEqual(valid.Service, failure.Offender);
        StringAssert.Contains(failure.Path[^1], nameof(ImmichReverseGeo.Overture.Services.OvertureDivisionsService));
        Assert.AreEqual("FactoryContractMismatch", BoundaryFactoryContract.Validate(BoundaryRole.Standard, [valid with { Lifetime = "Scoped" }], [valid]).Single().Rule);
    }
}
