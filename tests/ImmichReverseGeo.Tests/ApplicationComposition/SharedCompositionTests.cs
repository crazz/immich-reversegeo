using System.Text.Json;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
public sealed class SharedCompositionTests
{
    public static IEnumerable<object[]> PathCases()
    {
        yield return ["development-defaults", "Development", "/composition/development-content", null!, null!, "/composition/development-content/localdata", "/composition/development-content/localdata", "/composition/development-content/bundled-data"];
        yield return ["production-defaults", "Production", "/composition/production-content", null!, null!, "/data", "/config", "/app/bundled-data"];
        yield return ["data-only-override", "Development", "/composition/data-override-content", "/volumes/geodata", null!, "/volumes/geodata", "/composition/data-override-content/localdata", "/composition/data-override-content/bundled-data"];
        yield return ["config-only-override", "Production", "/composition/config-override-content", null!, "/volumes/settings", "/data", "/volumes/settings", "/app/bundled-data"];
        yield return ["both-overrides", "Development", "/composition/both-overrides-content", "/volumes/data", "/volumes/config", "/volumes/data", "/volumes/config", "/composition/both-overrides-content/bundled-data"];
    }

    [TestMethod]
    [DynamicData(nameof(PathCases))]
    public void Context_ResolvesIndependentEstablishedRoots(
        string label,
        string environmentName,
        string contentRoot,
        string? dataOverride,
        string? configOverride,
        string expectedDataDirectory,
        string expectedConfigDirectory,
        string expectedBundledDataDirectory)
    {
        var environment = Enum.Parse<CompositionEnvironment>(environmentName);
        var context = ApplicationCompositionContext.Create(environment, contentRoot, dataOverride, configOverride);

        Assert.AreEqual(environment, context.Environment, label);
        Assert.AreEqual(contentRoot, context.ContentRoot, label);
        Assert.AreEqual(expectedDataDirectory, context.DataDirectory, label);
        Assert.AreEqual(expectedConfigDirectory, context.ConfigDirectory, label);
        Assert.AreEqual(expectedBundledDataDirectory, context.BundledDataDirectory, label);
    }

    [TestMethod]
    public void Context_RejectsUnsupportedEnvironment()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ApplicationCompositionContext.Create((CompositionEnvironment)99, "/composition/unsupported", null, null));
    }

    [TestMethod]
    public void Context_ToString_ContainsOnlyEnvironment()
    {
        var context = ApplicationCompositionContext.Create(
            CompositionEnvironment.Development,
            "/composition/content",
            "/composition/data",
            "/composition/config");

        Assert.AreEqual("ApplicationCompositionContext (Development)", context.ToString());
    }

    [TestMethod]
    public void SharedAndReusableHeavyComposition_ExcludeWorkerCommandBuilderGraph()
    {
        var commandTypes = new[]
        {
            typeof(WorkerCommandAmbientRuntimeObservationSource),
            typeof(IWorkerCommandRuntimeObservationSource),
            typeof(WorkerCommandRuntimeFactsCapture),
            typeof(IWorkerCommandRuntimeFactsCapture),
            typeof(WorkerCommandInvocationBuilder),
            typeof(IWorkerCommandInvocationBuilder),
            typeof(ImmichReverseGeo.Web.ChildWorkerLaunching.SystemChildProcessFactory),
            typeof(ImmichReverseGeo.Web.ChildWorkerLaunching.IChildProcessFactory),
            typeof(ImmichReverseGeo.Web.ChildWorkerLaunching.ChildWorkerLauncher),
            typeof(ImmichReverseGeo.Web.ChildWorkerLaunching.IChildWorkerLauncher)
        };
        var shared = new ServiceCollection();
        shared.AddSharedComposition(ApplicationCompositionContext.Create(CompositionEnvironment.Development, "/composition/shared", null, null));
        var reusable = new ServiceCollection();
        reusable.AddReusableHeavyComposition();

        foreach (var commandType in commandTypes)
        {
            Assert.AreEqual(0, shared.Count(descriptor => descriptor.ServiceType == commandType), "shared-absent-" + commandType.Name);
            Assert.AreEqual(0, reusable.Count(descriptor => descriptor.ServiceType == commandType), "reusable-absent-" + commandType.Name);
        }
    }

    [TestMethod]
    public void SharedRegistration_UsesOneSingletonDescriptorForEachSharedOwner()
    {
        var services = new ServiceCollection();
        services.AddSharedComposition(ApplicationCompositionContext.Create(
            CompositionEnvironment.Development,
            "/composition/descriptors",
            null,
            null));

        Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == typeof(StorageOptions)), "storage-options-count");
        Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == typeof(ConfigService)), "config-service-count");
        Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == typeof(CountryCodeService)), "country-code-count");
        Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == typeof(CityResolverProfileCatalogService)), "city-profile-catalog-count");
        Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource)), "npgsql-data-source-count");
        Assert.IsTrue(services.Any(descriptor => descriptor.ServiceType == typeof(IHttpClientFactory)), "http-client-infrastructure");

        foreach (var descriptor in services.Where(descriptor =>
                     descriptor.ServiceType == typeof(StorageOptions) ||
                     descriptor.ServiceType == typeof(ConfigService) ||
                     descriptor.ServiceType == typeof(CountryCodeService) ||
                     descriptor.ServiceType == typeof(CityResolverProfileCatalogService) ||
                     descriptor.ServiceType == typeof(NpgsqlDataSource)))
        {
            Assert.AreEqual(ServiceLifetime.Singleton, descriptor.Lifetime, descriptor.ServiceType.Name);
        }
    }

    [TestMethod]
    public void SharedRegistration_ResolvesSingletonOwnersAndPreservesIdentity()
    {
        var services = new ServiceCollection();
        services.AddSharedComposition(ApplicationCompositionContext.Create(
            CompositionEnvironment.Development,
            "/composition/singleton-content",
            "/composition/data",
            "/composition/config"));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<StorageOptions>();
        var config = provider.GetRequiredService<ConfigService>();
        var catalog = provider.GetRequiredService<CityResolverProfileCatalogService>();
        var firstDataSource = provider.GetRequiredService<NpgsqlDataSource>();
        var secondDataSource = provider.GetRequiredService<NpgsqlDataSource>();

        Assert.AreEqual("/composition/data", storage.DataDir, "storage-data-directory");
        Assert.AreEqual("/composition/singleton-content/bundled-data", storage.BundledDataDir, "storage-bundled-directory");
        Assert.AreSame(config, provider.GetRequiredService<ConfigService>(), "config-singleton-identity");
        Assert.AreSame(catalog, provider.GetRequiredService<CityResolverProfileCatalogService>(), "catalog-singleton-identity");
        Assert.AreSame(firstDataSource, secondDataSource, "data-source-singleton-identity");
        Assert.IsNotNull(provider.GetRequiredService<IHttpClientFactory>(), "http-client-factory");
    }

    [TestMethod]
    public void ProviderBuild_DoesNotMaterializeCountryCodeService()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var bundledDirectory = Path.Combine(fixtureRoot, "bundled-data");
            Directory.CreateDirectory(bundledDirectory);
            File.WriteAllText(Path.Combine(bundledDirectory, "iso3166.json"), "{");

            var services = new ServiceCollection();
            services.AddSharedComposition(ApplicationCompositionContext.Create(
                CompositionEnvironment.Development,
                fixtureRoot,
                null,
                null));

            using var provider = services.BuildServiceProvider();
            Assert.IsNotNull(provider, "provider-build-completes-with-poisoned-country-fixture");
            Assert.ThrowsExactly<JsonException>(
                () => provider.GetRequiredService<CountryCodeService>(),
                "country-resolution-reads-poisoned-fixture");
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }
    }
}
