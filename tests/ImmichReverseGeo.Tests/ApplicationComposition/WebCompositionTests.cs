using System.Text.Json;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
public sealed class WebCompositionTests
{
    [TestMethod]
    public void WebComposition_RegistersPageDependenciesAndPublicWebContracts()
    {
        var fixtureRoot = CreateBundledIdentityFixture();
        try
        {
            var services = CreateWebServices(
                fixtureRoot,
                Path.Combine(fixtureRoot, "data"),
                Path.Combine(fixtureRoot, "config"));

            var pageDependencies = new[]
            {
                typeof(ProcessingState),
                typeof(IManualProcessingRunCoordinator),
                typeof(ImmichDbRepository),
                typeof(SkippedAssetsRepository),
                typeof(GadmDivisionsService),
                typeof(GadmDivisionCacheService),
                typeof(OverturePlacesService),
                typeof(OvertureDivisionCacheService),
                typeof(OvertureDivisionsService),
                typeof(ConfigService),
                typeof(CityResolverProfileCatalogService),
                typeof(CountryCodeService)
            };

            foreach (var serviceType in pageDependencies)
            {
                Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == serviceType), $"page-{serviceType.Name}-count");
            }

            Assert.IsTrue(services.Any(descriptor =>
                descriptor.ServiceType == typeof(IPostConfigureOptions<RazorComponentsServiceOptions>)),
                "razor-components-options-contract");
            Assert.IsTrue(services.Any(descriptor =>
                descriptor.ServiceType == typeof(IConfigureOptions<CircuitOptions>)),
                "interactive-server-options-contract");
            Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == typeof(IDataProtectionProvider)), "data-protection-provider-count");

            using var provider = services.BuildServiceProvider();
            Assert.AreEqual("ImmichReverseGeo", provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator, "data-protection-application-name");
        }
        finally
        {
            DeleteFixture(fixtureRoot);
        }
    }

    [TestMethod]
    public void WebComposition_RegistersAntiforgeryAndConfigRootedKeyRepository()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var configDirectory = Path.Combine(fixtureRoot, "explicit-config");

        try
        {
            var services = CreateWebServices(fixtureRoot, Path.Combine(fixtureRoot, "data"), configDirectory);

            Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == typeof(IAntiforgery)), "web-antiforgery-count");

            using var provider = services.BuildServiceProvider();
            Assert.IsNotNull(provider.GetRequiredService<IAntiforgery>(), "web-antiforgery-resolution");
            var keyManagement = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
            var repository = keyManagement.XmlRepository as FileSystemXmlRepository;

            Assert.IsNotNull(repository, "file-system-key-repository");
            Assert.AreEqual(
                "dataprotection-keys",
                Path.GetRelativePath(configDirectory, repository.Directory.FullName),
                "key-repository-config-relative-path");
        }
        finally
        {
            DeleteFixture(fixtureRoot);
        }
    }

    [TestMethod]
    public void WebComposition_RegistersOneApplicationControlPlaneDescriptorPerOwner()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var services = CreateWebServices(fixtureRoot, Path.Combine(fixtureRoot, "data"), Path.Combine(fixtureRoot, "config"));
            var expectedServices = new[]
            {
                typeof(ProcessingState),
                typeof(ProcessingStateEventReporter),
                typeof(IProcessingEventReporter),
                typeof(IProcessingScheduleConfiguration),
                typeof(ProcessingRunCoordinator),
                typeof(IManualProcessingRunCoordinator),
                typeof(IScheduledRunTrigger),
                typeof(ProcessingBackgroundService),
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

            foreach (var serviceType in expectedServices)
            {
                var descriptors = services.Where(descriptor => descriptor.ServiceType == serviceType).ToArray();
                Assert.AreEqual(1, descriptors.Length, $"web-{serviceType.Name}-count");
                Assert.AreEqual(ServiceLifetime.Singleton, descriptors[0].Lifetime, $"web-{serviceType.Name}-lifetime");
            }

        }
        finally
        {
            DeleteFixture(fixtureRoot);
        }
    }

    [TestMethod]
    public void WebComposition_PreservesControlPlaneAndHeavyAliasIdentity()
    {
        var fixtureRoot = CreateBundledIdentityFixture();
        try
        {
            var services = CreateWebServices(
                fixtureRoot,
                Path.Combine(fixtureRoot, "data"),
                Path.Combine(fixtureRoot, "config"));
            using var provider = services.BuildServiceProvider();

            var reporter = provider.GetRequiredService<ProcessingStateEventReporter>();
            var config = provider.GetRequiredService<ConfigService>();
            var coordinator = provider.GetRequiredService<ProcessingRunCoordinator>();
            var background = provider.GetRequiredService<ProcessingBackgroundService>();
            var executor = provider.GetRequiredService<ProcessingRunExecutor>();
            var repository = provider.GetRequiredService<ImmichDbRepository>();
            var overtureCache = provider.GetRequiredService<OvertureDivisionCacheService>();
            var hostedDescriptors = services
                .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                .ToArray();
            var hostedServices = provider.GetServices<IHostedService>().ToArray();
            var applicationHostedEntries = hostedServices
                .Select((service, index) => new { Service = service, Descriptor = hostedDescriptors[index] })
                .Where(entry => entry.Service is ProcessingRunCoordinator or ProcessingBackgroundService)
                .ToArray();

            Assert.AreSame(reporter, provider.GetRequiredService<IProcessingEventReporter>(), "reporter-alias");
            Assert.AreSame(config, provider.GetRequiredService<IProcessingScheduleConfiguration>(), "schedule-configuration-alias");
            Assert.AreSame(config, provider.GetRequiredService<IProcessingRunConfiguration>(), "run-configuration-alias");
            Assert.AreSame(coordinator, provider.GetRequiredService<IManualProcessingRunCoordinator>(), "manual-coordinator-alias");
            Assert.AreSame(coordinator, provider.GetRequiredService<IScheduledRunTrigger>(), "scheduled-trigger-alias");
            Assert.AreEqual(hostedDescriptors.Length, hostedServices.Length, "complete-hosted-sequence-length");
            Assert.AreEqual(2, applicationHostedEntries.Length, "application-hosted-service-count");
            Assert.IsTrue(applicationHostedEntries.All(entry => entry.Descriptor.Lifetime == ServiceLifetime.Singleton), "application-hosted-lifetime");
            Assert.AreSame(coordinator, applicationHostedEntries[0].Service, "coordinator-hosted-alias");
            Assert.AreSame(background, applicationHostedEntries[1].Service, "background-hosted-alias");
            Assert.AreNotSame(applicationHostedEntries[0].Service, applicationHostedEntries[1].Service, "distinct-hosted-owners");
            Assert.AreSame(executor, provider.GetRequiredService<IProcessingRunExecutor>(), "executor-alias");
            Assert.AreSame(repository, provider.GetRequiredService<IProcessingAssetRepository>(), "repository-alias");
            Assert.AreSame(overtureCache, provider.GetRequiredService<OvertureDivisionCacheService>(), "cache-singleton-identity");
        }
        finally
        {
            DeleteFixture(fixtureRoot);
        }
    }

    [TestMethod]
    public void WebComposition_ExcludesInternalWorkerHostOnlyServices()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var services = CreateWebServices(fixtureRoot, Path.Combine(fixtureRoot, "data"), Path.Combine(fixtureRoot, "config"));
            var workerOnlyTypes = new[]
            {
                typeof(ImmichReverseGeo.Web.WorkerHost.SkippedAssetsWorkerStartupInitializer),
                typeof(ImmichReverseGeo.Web.WorkerHost.IWorkerStartupInitializer),
                typeof(ImmichReverseGeo.Web.WorkerHost.WorkerStdinTransportConfigured),
                typeof(ImmichReverseGeo.Web.WorkerHost.IWorkerTransportAvailability),
                typeof(ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop.IWorkerStandardInputStreamFactory),
                typeof(ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop.WorkerStdinRequestSource),
                typeof(ImmichReverseGeo.Web.WorkerHost.IInitialProcessingRunAcquirer),
                typeof(ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop.WorkerStdinAcceptedRunFinality),
                typeof(ImmichReverseGeo.Web.WorkerHost.IWorkerAcceptedRunFinality),
                typeof(ImmichReverseGeo.Web.WorkerHost.TransitionalWorkerPreRequestFinality),
                typeof(ImmichReverseGeo.Web.WorkerHost.IWorkerPreRequestFinality),
                typeof(ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput.IWorkerNdjsonOutputStreamFactory),
                typeof(ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput.WorkerNdjsonEmitter),
                typeof(ImmichReverseGeo.Web.WorkerHost.IWorkerReadinessPublisher),
                typeof(ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput.WorkerNdjsonProcessingEventReporter),
                typeof(ImmichReverseGeo.Web.WorkerHost.InternalWorkerLifecycleService)
            };
            foreach (var workerOnlyType in workerOnlyTypes)
            {
                Assert.AreEqual(0, services.Count(descriptor => descriptor.ServiceType == workerOnlyType), "web-excludes-" + workerOnlyType.Name);
            }
        }
        finally
        {
            DeleteFixture(fixtureRoot);
        }
    }

    [TestMethod]
    public void WebComposition_RegistersConcreteChildWorkerOwnersAndStableAliasesWithoutEagerWork()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var services = CreateWebServices(fixtureRoot, Path.Combine(fixtureRoot, "data"), Path.Combine(fixtureRoot, "config"));
            AssertChildWorkerConcreteDescriptor(
                services,
                typeof(ImmichReverseGeo.Web.ChildWorkerLaunching.SystemChildProcessFactory));
            AssertChildWorkerConcreteDescriptor(
                services,
                typeof(ImmichReverseGeo.Web.ChildWorkerLaunching.ChildWorkerLauncher));
            AssertChildWorkerAliasDescriptor(
                services,
                typeof(ImmichReverseGeo.Web.ChildWorkerLaunching.IChildProcessFactory));
            AssertChildWorkerAliasDescriptor(
                services,
                typeof(ImmichReverseGeo.Web.ChildWorkerLaunching.IChildWorkerLauncher));

            using var provider = services.BuildServiceProvider();
            var concreteFactory = provider.GetRequiredService<ImmichReverseGeo.Web.ChildWorkerLaunching.SystemChildProcessFactory>();
            var launcher = provider.GetRequiredService<ImmichReverseGeo.Web.ChildWorkerLaunching.ChildWorkerLauncher>();
            Assert.AreSame(
                concreteFactory,
                provider.GetRequiredService<ImmichReverseGeo.Web.ChildWorkerLaunching.IChildProcessFactory>(),
                "child-worker-factory-alias");
            Assert.AreSame(
                launcher,
                provider.GetRequiredService<ImmichReverseGeo.Web.ChildWorkerLaunching.IChildWorkerLauncher>(),
                "child-worker-launcher-alias");
        }
        finally
        {
            DeleteFixture(fixtureRoot);
        }
    }

    [TestMethod]
    public void WebComposition_CommandBuilderOwnerAliasesShareIdentityWithoutCapture()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var services = CreateWebServices(fixtureRoot, Path.Combine(fixtureRoot, "data"), Path.Combine(fixtureRoot, "config"));
            using var provider = services.BuildServiceProvider();
            Assert.AreSame(
                provider.GetRequiredService<WorkerCommandAmbientRuntimeObservationSource>(),
                provider.GetRequiredService<IWorkerCommandRuntimeObservationSource>(),
                "worker-command-source-alias");
            Assert.AreSame(
                provider.GetRequiredService<WorkerCommandRuntimeFactsCapture>(),
                provider.GetRequiredService<IWorkerCommandRuntimeFactsCapture>(),
                "worker-command-capture-alias");
            Assert.AreSame(
                provider.GetRequiredService<WorkerCommandInvocationBuilder>(),
                provider.GetRequiredService<IWorkerCommandInvocationBuilder>(),
                "worker-command-builder-alias");
        }
        finally
        {
            DeleteFixture(fixtureRoot);
        }
    }

    [TestMethod]
    public void WebComposition_RegistersLazyWebOnlyWorkerCommandBuilderGraph()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var source = new CountingWorkerCommandSource();
            var services = CreateWebServices(fixtureRoot, Path.Combine(fixtureRoot, "data"), Path.Combine(fixtureRoot, "config"));
            services.RemoveAll<IWorkerCommandRuntimeObservationSource>();
            services.AddSingleton<IWorkerCommandRuntimeObservationSource>(source);

            var expected = new[]
            {
                typeof(WorkerCommandAmbientRuntimeObservationSource),
                typeof(IWorkerCommandRuntimeObservationSource),
                typeof(WorkerCommandRuntimeFactsCapture),
                typeof(IWorkerCommandRuntimeFactsCapture),
                typeof(WorkerCommandInvocationBuilder),
                typeof(IWorkerCommandInvocationBuilder)
            };
            foreach (var serviceType in expected)
            {
                var descriptors = services.Where(descriptor => descriptor.ServiceType == serviceType).ToArray();
                Assert.AreEqual(1, descriptors.Length, "worker-command-" + serviceType.Name + "-count");
                Assert.AreEqual(ServiceLifetime.Singleton, descriptors[0].Lifetime, "worker-command-" + serviceType.Name + "-lifetime");
            }

            using var provider = services.BuildServiceProvider();
            Assert.AreEqual(0, source.TotalCalls, "provider-build-does-not-capture");
            Assert.AreSame(source, provider.GetRequiredService<IWorkerCommandRuntimeObservationSource>(), "replacement-source-identity");
            var captureConcrete = provider.GetRequiredService<WorkerCommandRuntimeFactsCapture>();
            var captureContract = provider.GetRequiredService<IWorkerCommandRuntimeFactsCapture>();
            Assert.AreSame(captureConcrete, captureContract, "replacement-capture-alias");
            var concrete = provider.GetRequiredService<WorkerCommandInvocationBuilder>();
            var contract = provider.GetRequiredService<IWorkerCommandInvocationBuilder>();
            Assert.AreSame(concrete, contract, "worker-command-builder-alias");
            Assert.AreEqual(0, source.TotalCalls, "resolution-does-not-capture");
            _ = contract.Build();
            Assert.AreEqual(7, source.TotalCalls, "first-builder-call-captures-once");
            _ = contract.Build();
            Assert.AreEqual(14, source.TotalCalls, "second-builder-call-captures-once-again");
        }
        finally
        {
            DeleteFixture(fixtureRoot);
        }
    }

    [TestMethod]
    public void WebComposition_CreatesOnlyConfigDataProtectionDirectory()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(fixtureRoot, "data-volume");
        var configDirectory = Path.Combine(fixtureRoot, "config-volume");

        try
        {
            CreateWebServices(fixtureRoot, dataDirectory, configDirectory);

            Assert.IsTrue(Directory.Exists(Path.Combine(configDirectory, "dataprotection-keys")), "data-protection-config-directory");
            Assert.IsFalse(Directory.Exists(dataDirectory), "data-directory-not-created-by-web-composition");
        }
        finally
        {
            DeleteFixture(fixtureRoot);
        }
    }

    [TestMethod]
    public void WebComposition_ProviderBuildDoesNotMaterializeCountryOrSkippedStore()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var bundledDirectory = Path.Combine(fixtureRoot, "bundled-data");
            Directory.CreateDirectory(bundledDirectory);
            File.WriteAllText(Path.Combine(bundledDirectory, "iso3166.json"), "{");
            var services = CreateWebServices(fixtureRoot, Path.Combine(fixtureRoot, "data"), Path.Combine(fixtureRoot, "config"));

            using var provider = services.BuildServiceProvider();

            Assert.IsNotNull(provider, "provider-build-with-poisoned-country-fixture");
            Assert.IsFalse(File.Exists(Path.Combine(fixtureRoot, "data", "skipped.db")), "skipped-store-not-initialized");
            Assert.ThrowsExactly<JsonException>(
                () => provider.GetRequiredService<CountryCodeService>(),
                "country-resolution-reads-poisoned-fixture");
        }
        finally
        {
            DeleteFixture(fixtureRoot);
        }
    }

    private sealed class CountingWorkerCommandSource : IWorkerCommandRuntimeObservationSource
    {
        internal int TotalCalls { get; private set; }

        public string? GetProcessPath()
        {
            TotalCalls++;
            return "/usr/share/dotnet/dotnet";
        }

        public WorkerCommandEntryAssemblyObservation GetEntryAssembly()
        {
            TotalCalls++;
            return new WorkerCommandEntryAssemblyObservation("ImmichReverseGeo.Web", "/app/ImmichReverseGeo.Web.dll");
        }

        public string GetCurrentDirectory()
        {
            TotalCalls++;
            return "/app";
        }

        public bool IsWindows()
        {
            TotalCalls++;
            return false;
        }

        public WorkerTargetObservation ObserveTarget(string path)
        {
            TotalCalls++;
            return path == "/app" ? WorkerTargetObservation.Directory : WorkerTargetObservation.File;
        }
    }

    private static void AssertChildWorkerConcreteDescriptor(IServiceCollection services, Type concreteType)
    {
        var descriptors = services.Where(descriptor => descriptor.ServiceType == concreteType).ToArray();
        Assert.AreEqual(1, descriptors.Length, "child-worker-" + concreteType.Name + "-concrete-count");
        Assert.AreEqual(ServiceLifetime.Singleton, descriptors[0].Lifetime, "child-worker-" + concreteType.Name + "-concrete-lifetime");
        Assert.IsTrue(
            descriptors[0].ImplementationType == concreteType || descriptors[0].ImplementationFactory is not null,
            "child-worker-" + concreteType.Name + "-concrete-activation");
    }

    private static void AssertChildWorkerAliasDescriptor(IServiceCollection services, Type serviceType)
    {
        var descriptors = services.Where(descriptor => descriptor.ServiceType == serviceType).ToArray();
        Assert.AreEqual(1, descriptors.Length, "child-worker-" + serviceType.Name + "-alias-count");
        Assert.AreEqual(ServiceLifetime.Singleton, descriptors[0].Lifetime, "child-worker-" + serviceType.Name + "-alias-lifetime");
        Assert.IsNotNull(descriptors[0].ImplementationFactory, "child-worker-" + serviceType.Name + "-alias-factory");
    }

    private static ServiceCollection CreateWebServices(string contentRoot, string dataDirectory, string configDirectory)
    {
        var services = new ServiceCollection();
        services.AddWebComposition(ApplicationCompositionContext.Create(
            CompositionEnvironment.Development,
            contentRoot,
            dataDirectory,
            configDirectory));
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
