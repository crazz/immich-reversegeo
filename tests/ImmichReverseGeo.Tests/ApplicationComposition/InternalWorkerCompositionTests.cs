using System.Text.Json;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
public sealed class InternalWorkerCompositionTests
{
    [TestMethod]
    public void InternalWorkerComposition_RegistersCompleteSingletonGraph()
    {
        var services = CreateWorkerServices("/composition/worker-descriptors");
        var expectedServices = new[]
        {
            typeof(StorageOptions),
            typeof(ConfigService),
            typeof(CountryCodeService),
            typeof(CityResolverProfileCatalogService),
            typeof(NpgsqlDataSource),
            typeof(AdministrativeAreaResolverService),
            typeof(OvertureDivisionCacheService),
            typeof(OverturePlacesService),
            typeof(OvertureDivisionsService),
            typeof(GadmDivisionCacheService),
            typeof(GadmDivisionsService),
            typeof(ImmichDbRepository),
            typeof(IProcessingAssetRepository),
            typeof(SkippedAssetsRepository),
            typeof(IProcessingSkippedStore),
            typeof(IProcessingRunConfiguration),
            typeof(IProcessingAdministrativeResolver),
            typeof(ProcessingInfrastructureLookup),
            typeof(IProcessingInfrastructureLookup),
            typeof(TimeProvider),
            typeof(ProcessingRunDelay),
            typeof(IProcessingRunDelay),
            typeof(ProcessingRunExecutor),
            typeof(IProcessingRunExecutor)
        };

        foreach (var serviceType in expectedServices)
        {
            var descriptors = services.Where(descriptor => descriptor.ServiceType == serviceType).ToArray();
            Assert.AreEqual(1, descriptors.Length, $"worker-{serviceType.Name}-count");
            Assert.AreEqual(ServiceLifetime.Singleton, descriptors[0].Lifetime, $"worker-{serviceType.Name}-lifetime");
        }
    }

    [TestMethod]
    public void InternalWorkerComposition_ExcludesApplicationOwnedWebBoundary()
    {
        var services = CreateWorkerServices("/composition/worker-forbidden");
        var forbiddenServices = new[]
        {
            typeof(WebApplicationBuilder),
            typeof(WebApplication),
            typeof(IPostConfigureOptions<RazorComponentsServiceOptions>),
            typeof(IConfigureOptions<CircuitOptions>),
            typeof(IDataProtectionProvider),
            typeof(IAntiforgery),
            typeof(ProcessingState),
            typeof(ProcessingStateEventReporter),
            typeof(IProcessingEventReporter),
            typeof(IProcessingScheduleConfiguration),
            typeof(ProcessingRunCoordinator),
            typeof(IManualProcessingRunCoordinator),
            typeof(IScheduledRunTrigger),
            typeof(ProcessingBackgroundService),
            typeof(IHostedService),
            typeof(WorkerProtocolCodec),
            typeof(WorkerProtocolMapper),
            typeof(WorkerProtocolControllerInputValidator),
            typeof(WorkerProtocolEventStreamValidator)
        };

        foreach (var serviceType in forbiddenServices)
        {
            Assert.AreEqual(0, services.Count(descriptor => descriptor.ServiceType == serviceType), $"worker-forbidden-{serviceType.Name}");
        }
    }

    [TestMethod]
    public void InternalWorkerComposition_ResolvesExecutorGraphAndAliases()
    {
        var fixtureRoot = CreateBundledIdentityFixture();
        try
        {
            var services = CreateWorkerServices(fixtureRoot);
            using var provider = services.BuildServiceProvider();
            var config = provider.GetRequiredService<ConfigService>();
            var repository = provider.GetRequiredService<ImmichDbRepository>();
            var skipped = provider.GetRequiredService<SkippedAssetsRepository>();
            var administrativeResolver = provider.GetRequiredService<AdministrativeAreaResolverService>();
            var infrastructure = provider.GetRequiredService<ProcessingInfrastructureLookup>();
            var delay = provider.GetRequiredService<ProcessingRunDelay>();
            var executor = provider.GetRequiredService<ProcessingRunExecutor>();
            var firstDataSource = provider.GetRequiredService<NpgsqlDataSource>();
            var secondDataSource = provider.GetRequiredService<NpgsqlDataSource>();

            Assert.AreSame(config, provider.GetRequiredService<IProcessingRunConfiguration>(), "worker-run-configuration-alias");
            Assert.AreSame(repository, provider.GetRequiredService<IProcessingAssetRepository>(), "worker-asset-repository-alias");
            Assert.AreSame(skipped, provider.GetRequiredService<IProcessingSkippedStore>(), "worker-skipped-store-alias");
            Assert.AreSame(administrativeResolver, provider.GetRequiredService<IProcessingAdministrativeResolver>(), "worker-administrative-resolver-alias");
            Assert.AreSame(infrastructure, provider.GetRequiredService<IProcessingInfrastructureLookup>(), "worker-infrastructure-alias");
            Assert.AreSame(delay, provider.GetRequiredService<IProcessingRunDelay>(), "worker-delay-alias");
            Assert.AreSame(executor, provider.GetRequiredService<IProcessingRunExecutor>(), "worker-executor-alias");
            Assert.AreSame(firstDataSource, secondDataSource, "worker-data-source-singleton");
        }
        finally
        {
            DeleteFixture(fixtureRoot);
        }
    }

    [TestMethod]
    public void InternalWorkerComposition_ProviderBuildDoesNotMaterializeCountryOrFilesystemStores()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var bundledDirectory = Path.Combine(fixtureRoot, "bundled-data");
            Directory.CreateDirectory(bundledDirectory);
            File.WriteAllText(Path.Combine(bundledDirectory, "iso3166.json"), "{");
            var services = CreateWorkerServices(fixtureRoot);

            using var provider = services.BuildServiceProvider();

            Assert.IsNotNull(provider, "worker-provider-build-with-poisoned-country-fixture");
            Assert.IsFalse(Directory.Exists(Path.Combine(fixtureRoot, "localdata")), "worker-data-directory-not-created");
            Assert.IsFalse(Directory.Exists(Path.Combine(fixtureRoot, "localdata", "dataprotection-keys")), "worker-data-protection-directory-not-created");
            Assert.IsFalse(File.Exists(Path.Combine(fixtureRoot, "localdata", "skipped.db")), "worker-skipped-store-not-initialized");
            Assert.ThrowsExactly<JsonException>(
                () => provider.GetRequiredService<CountryCodeService>(),
                "worker-country-resolution-reads-poisoned-fixture");
        }
        finally
        {
            DeleteFixture(fixtureRoot);
        }
    }

    private static ServiceCollection CreateWorkerServices(string contentRoot)
    {
        var services = new ServiceCollection();
        services.AddInternalWorkerComposition(ApplicationCompositionContext.Create(
            CompositionEnvironment.Development,
            contentRoot,
            null,
            null));
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
            Assert.IsTrue(File.Exists(sourcePath), "worker-bundled-identity-test-source");
            File.Copy(sourcePath, Path.Combine(bundledDirectory, "iso3166.json"));
            return fixtureRoot;
        }
        catch
        {
            DeleteFixture(fixtureRoot);
            throw;
        }
    }

    private static void DeleteFixture(string fixtureRoot)
    {
        if (Directory.Exists(fixtureRoot))
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }
}
