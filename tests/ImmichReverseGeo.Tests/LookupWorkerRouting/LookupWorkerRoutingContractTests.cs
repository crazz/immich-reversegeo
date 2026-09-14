using System.Reflection;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ImmichReverseGeo.Tests.LookupWorkerRouting;

[TestClass]
[TestCategory("Change49")]
public sealed class LookupWorkerRoutingContractTests
{
    [TestMethod]
    public void Lookup_ComponentOwnsAsyncCleanupAndInjectsNoHeavyGeodataService()
    {
        Type component = typeof(Lookup);
        Type[] injectedTypes = component
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(static property => property.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(static property => property.PropertyType)
            .ToArray();

        Assert.IsTrue(typeof(IAsyncDisposable).IsAssignableFrom(component));
        CollectionAssert.DoesNotContain(injectedTypes, typeof(GadmDivisionsService));
        CollectionAssert.DoesNotContain(injectedTypes, typeof(GadmDivisionCacheService));
        CollectionAssert.DoesNotContain(injectedTypes, typeof(OverturePlacesService));
        CollectionAssert.DoesNotContain(injectedTypes, typeof(OvertureDivisionCacheService));
        CollectionAssert.DoesNotContain(injectedTypes, typeof(OvertureDivisionsService));
    }

    [TestMethod]
    public void Lookup_DisplayHelpersDistinguishClosedStatesAndUnknownFacts()
    {
        Type component = typeof(Lookup);

        Assert.AreEqual("unavailable", Invoke<string>(
            component,
            "DescribeSourceState",
            CoordinateLookupSourceState.Unavailable));
        Assert.AreEqual("failed", Invoke<string>(
            component,
            "DescribeSourceState",
            CoordinateLookupSourceState.Failed));
        Assert.AreEqual("no match", Invoke<string>(
            component,
            "DescribeSourceState",
            CoordinateLookupSourceState.NoMatch));
        Assert.AreEqual("unknown", Invoke<string?>(component, "DescribeFact", null));
        Assert.AreEqual("unknown", Invoke<string?>(component, "DescribeDistance", null));
        Assert.AreEqual("unknown", Invoke<string?>(component, "DescribeConfidence", null));
        Assert.AreEqual("unknown", Invoke<string?>(component, "DescribeArea", null));
    }

    private static T Invoke<T>(Type component, string name, object? argument)
    {
        MethodInfo method = component.GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertFailedException($"Lookup.{name} was not found.");
        return (T)method.Invoke(null, [argument])!;
    }
}
