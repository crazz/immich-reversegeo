using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
public sealed class ReusableHeavyCompositionTests
{
    [TestMethod]
    public void ReusableHeavyRegistration_UsesExpectedSingletonDescriptorMatrix()
    {
        var services = CreateServices("/composition/heavy-descriptors");

        var requiredServices = new[]
        {
            typeof(AdministrativeAreaResolverService),
            typeof(OvertureDivisionCacheService),
            typeof(OverturePlacesService),
            typeof(OvertureDivisionsService),
            typeof(GadmDivisionCacheService),
            typeof(GadmDivisionsService),
            typeof(ImmichDbRepository),
            typeof(SkippedAssetsRepository),
            typeof(IProcessingRunConfiguration),
            typeof(IProcessingAssetRepository),
            typeof(IProcessingSkippedStore),
            typeof(IProcessingAdministrativeResolver),
            typeof(ProcessingInfrastructureLookup),
            typeof(IProcessingInfrastructureLookup),
            typeof(TimeProvider),
            typeof(ProcessingRunDelay),
            typeof(IProcessingRunDelay),
            typeof(ProcessingRunExecutor),
            typeof(IProcessingRunExecutor)
        };

        foreach (var serviceType in requiredServices)
        {
            var descriptors = services.Where(descriptor => descriptor.ServiceType == serviceType).ToArray();
            Assert.AreEqual(1, descriptors.Length, $"{serviceType.Name}-count");
            Assert.AreEqual(ServiceLifetime.Singleton, descriptors[0].Lifetime, $"{serviceType.Name}-lifetime");
        }
    }

    [TestMethod]
    public void ReusableHeavyRegistration_ExcludesControlPlaneDescriptors()
    {
        var services = CreateServices("/composition/heavy-forbidden");

        var forbiddenServices = new[]
        {
            typeof(ProcessingState),
            typeof(ProcessingStateEventReporter),
            typeof(IProcessingEventReporter),
            typeof(IProcessingScheduleConfiguration),
            typeof(ProcessingRunCoordinator),
            typeof(IManualProcessingRunCoordinator),
            typeof(IScheduledRunTrigger),
            typeof(ProcessingBackgroundService),
            typeof(IHostedService)
        };

        foreach (var serviceType in forbiddenServices)
        {
            Assert.AreEqual(0, services.Count(descriptor => descriptor.ServiceType == serviceType), $"forbidden-{serviceType.Name}");
        }
    }

    [TestMethod]
    public void ReusableHeavyRegistration_ResolvesExecutorGraphAndPreservesAliasIdentity()
    {
        var fixtureRoot = CreateBundledIdentityFixture();
        try
        {
            var services = CreateServices(fixtureRoot);
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

            var config = provider.GetRequiredService<ConfigService>();
            var repository = provider.GetRequiredService<ImmichDbRepository>();
            var skipped = provider.GetRequiredService<SkippedAssetsRepository>();
            var administrativeResolver = provider.GetRequiredService<AdministrativeAreaResolverService>();
            var infrastructure = provider.GetRequiredService<ProcessingInfrastructureLookup>();
            var delay = provider.GetRequiredService<ProcessingRunDelay>();
            var executor = provider.GetRequiredService<ProcessingRunExecutor>();

            Assert.AreSame(config, provider.GetRequiredService<IProcessingRunConfiguration>(), "config-run-configuration-alias");
            Assert.AreSame(repository, provider.GetRequiredService<IProcessingAssetRepository>(), "repository-asset-alias");
            Assert.AreSame(skipped, provider.GetRequiredService<IProcessingSkippedStore>(), "skipped-store-alias");
            Assert.AreSame(administrativeResolver, provider.GetRequiredService<IProcessingAdministrativeResolver>(), "administrative-resolver-alias");
            Assert.AreSame(infrastructure, provider.GetRequiredService<IProcessingInfrastructureLookup>(), "infrastructure-lookup-alias");
            Assert.AreSame(delay, provider.GetRequiredService<IProcessingRunDelay>(), "run-delay-alias");
            Assert.AreSame(executor, provider.GetRequiredService<IProcessingRunExecutor>(), "run-executor-alias");
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ReusableHeavyRegistration_ProviderBuildDoesNotMaterializeCountryOrSkippedStore()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var bundledDirectory = Path.Combine(fixtureRoot, "bundled-data");
            Directory.CreateDirectory(bundledDirectory);
            File.WriteAllText(Path.Combine(bundledDirectory, "iso3166.json"), "{");

            var services = CreateServices(fixtureRoot);
            using var provider = services.BuildServiceProvider();

            Assert.IsNotNull(provider, "provider-build-with-poisoned-country-identity-fixture");
            Assert.IsFalse(File.Exists(Path.Combine(fixtureRoot, "localdata", "skipped.db")), "skipped-store-not-initialized");
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }
    }

    private static ServiceCollection CreateServices(string contentRoot)
    {
        var services = new ServiceCollection();
        services.AddSharedComposition(ApplicationCompositionContext.Create(
            CompositionEnvironment.Development,
            contentRoot,
            null,
            null));
        services.AddReusableHeavyComposition();
        return services;
    }

    private static string CreateBundledIdentityFixture()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var bundledDirectory = Path.Combine(fixtureRoot, "bundled-data");
            Directory.CreateDirectory(bundledDirectory);

            var sourcePath = Path.Combine(AppContext.BaseDirectory, "data", "iso3166.json");
            Assert.IsTrue(File.Exists(sourcePath), "bundled-identity-test-source");
            File.Copy(sourcePath, Path.Combine(bundledDirectory, "iso3166.json"));
            return fixtureRoot;
        }
        catch
        {
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }

            throw;
        }
    }
}
