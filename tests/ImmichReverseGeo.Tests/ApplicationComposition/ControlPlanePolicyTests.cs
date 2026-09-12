using ImmichReverseGeo.Overture.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using ImmichReverseGeo.Web.Services;
using System.Reflection.Emit;
using Npgsql;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
[TestCategory("Change56")]
public sealed class ControlPlanePolicyTests
{
    [TestMethod]
    public void InheritedPrivateGeneratedInjection_CannotHideAHeavyDependency()
    {
        var inspection = new WebBoundaryInspection();
        inspection.Inspect([], [typeof(GeneratedPage)]);
        Assert.IsTrue(inspection.Failures.Any(failure => failure.Contains(nameof(OvertureDivisionsService), StringComparison.Ordinal)),
            "Inherited private injection must identify the page-to-country-index edge without constructing the page.");
        BoundaryDiagnostic diagnostic = inspection.Diagnostics.Single(d => d.Root.Contains(nameof(GeneratedPage), StringComparison.Ordinal));
        Assert.AreEqual("ForbiddenDependency", diagnostic.Rule);
        Assert.AreEqual("country index", diagnostic.Category);
        Assert.IsTrue(diagnostic.Path.Any(p => p.Contains("inject", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ApprovedInterfacesAndCyclicMultiHopAliases_DoNotHideTheirImplementationClosure()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Root>();
        services.AddSingleton<ILightContract, HiddenImplementation>();
        services.AddSingleton<Cycle>();
        services.AddSingleton<Middle>();
        foreach (BoundaryRole role in new[] { BoundaryRole.Standard, BoundaryRole.WebOnly })
        {
            var inspection = new WebBoundaryInspection(role);
            inspection.Inspect(services, []);
            BoundaryDiagnostic diagnostic = inspection.Diagnostics.First(d => d.Root == "descriptor " + typeof(Root)
                && d.Offender == typeof(OvertureDivisionsService).ToString());
            Assert.AreEqual(role, diagnostic.Role);
            string path = string.Join(" -> ", diagnostic.Path);
            StringAssert.Contains(path, nameof(Root));
            StringAssert.Contains(path, nameof(ILightContract));
            StringAssert.Contains(path, nameof(HiddenImplementation));
            StringAssert.Contains(path, nameof(Middle));
            var reversed = new WebBoundaryInspection(role);
            reversed.Inspect(services.Reverse(), []);
            CollectionAssert.AreEqual(inspection.Failures.ToArray(), reversed.Failures.ToArray(), "Diagnostics are independent of root enumeration order.");
        }
    }

    [TestMethod]
    public void EveryExactLightweightAllowance_IsAnInspectedEntryRatherThanAnExemption()
    {
        foreach (BoundaryPolicyEntry entry in ControlPlaneDependencyPolicy.Lightweight)
        {
            var allowed = new WebBoundaryInspection();
            allowed.Inspect([ServiceDescriptor.Singleton(entry.Contract, entry.Contract)], []);
            Assert.IsEmpty(allowed.Diagnostics, entry.Contract + ": " + string.Join(Environment.NewLine, allowed.Diagnostics));
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Rationale));
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Owner));
            var polluted = new WebBoundaryInspection();
            polluted.Inspect([ServiceDescriptor.Singleton(entry.Contract, typeof(HiddenImplementation))], []);
            Assert.IsTrue(polluted.Diagnostics.Any(d => d.Category == "country index"), entry.Contract.ToString());
        }
    }

    [TestMethod]
    public void OpaqueFactoryAndEagerPostgresConnection_AreRejectedWithoutInvokingEither()
    {
        var dynamic = new DynamicMethod("unclassified", typeof(object), [typeof(IServiceProvider)]);
        ILGenerator il = dynamic.GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        var services = new ServiceCollection();
        services.AddSingleton(dynamic.CreateDelegate<Func<IServiceProvider, object>>());
        var opaque = new WebBoundaryInspection();
        opaque.Inspect(services, []);
        Assert.AreEqual("UnclassifiedFactory", opaque.Diagnostics.Single().Rule);

        services.Clear();
        services.AddSingleton<object>(sp => sp.GetRequiredService<NpgsqlDataSource>().OpenConnection());
        var eager = new WebBoundaryInspection();
        eager.Inspect(services, []);
        Assert.IsTrue(eager.Diagnostics.Any(d => d.Rule == "ProviderScope" && d.Category == "eager PostgreSQL"));
    }

    [TestMethod]
    public void NewRegistrationInsideTheWebBoundary_StillNeedsItsOwnReviewedContract()
    {
        BoundaryDiagnostic failure = ControlPlaneDependencyPolicy.InspectWebRoots(BoundaryRole.Standard, [typeof(Root)]).Single();
        Assert.AreEqual("UnreviewedRoot", failure.Rule);
        Assert.AreEqual(typeof(Root).FullName, failure.Offender);
        Assert.IsEmpty(ControlPlaneDependencyPolicy.InspectWebRoots(BoundaryRole.Standard, [typeof(CountryCodeService)]));
    }

    [TestMethod]
    public void NamespaceAndOpaqueActivationDiagnostics_KeepOnlyTheExactFriendGrantExemption()
    {
        Assert.IsNull(ControlPlaneDependencyPolicy.InspectSource("[assembly: InternalsVisibleTo(\"ImmichReverseGeo.Worker\")]", "friend.cs", BoundaryRole.Standard));
        foreach (string source in new[] { "@using ImmichReverseGeo.Gadm.Services", "namespace ImmichReverseGeo.Overture;", "using NetTopologySuite;", "NativeLibrary.Load(name)" })
        {
            BoundaryDiagnostic failure = ControlPlaneDependencyPolicy.InspectSource(source, "generated.razor", BoundaryRole.WebOnly)!;
            Assert.IsNotNull(failure);
            Assert.AreEqual("SourceDependency", failure.Rule);
            Assert.AreEqual(BoundaryRole.WebOnly, failure.Role);
            Assert.AreEqual("generated.razor", failure.Root);
        }
    }

    private interface ILightContract;
    private sealed class Root(ILightContract contract)
    {
        internal ILightContract Contract { get; } = contract;
    }
    private sealed class HiddenImplementation(Middle middle, Cycle cycle) : ILightContract
    {
        internal Middle Middle { get; } = middle;
        internal Cycle Cycle { get; } = cycle;
    }
    private sealed class Middle(OvertureDivisionsService index)
    {
        internal OvertureDivisionsService Index { get; } = index;
    }
    private sealed class Cycle(Root root)
    {
        internal Root Root { get; } = root;
    }

    private class GeneratedBase : ComponentBase
    {
        [Inject] private OvertureDivisionsService CountryIndex { get; set; } = null!;
    }

    [System.Runtime.CompilerServices.CompilerGenerated]
    private sealed class GeneratedPage : GeneratedBase;
}
