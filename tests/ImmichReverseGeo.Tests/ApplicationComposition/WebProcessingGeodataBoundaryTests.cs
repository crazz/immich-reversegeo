using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using ImmichReverseGeo.Web.WorkerEventStateBridge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
[TestCategory("Change39")]
[DoNotParallelize]
public sealed class WebProcessingGeodataBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ProductionProcessingFactoryGraph_RejectsNoForbiddenDependency()
    {
        using var fixture = WebBoundaryFixture.Create(0);

        var guard = new ProcessingFactoryGraph(fixture.ProductionDescriptors);
        string? failure = guard.FindForbiddenPath(
            typeof(IManualProcessingRunCoordinator),
            typeof(IScheduledRunTrigger),
            typeof(IScheduledRunWorkGate),
            typeof(ProcessingBackgroundService),
            typeof(IChildProcessingRunBackend));

        Assert.IsNull(failure, failure);
        Assert.AreEqual(0, guard.OpaqueFactoryCount, guard.OpaqueFactorySummary);
        Assert.IsTrue(
            guard.CapturedFactoryTypes.Contains(typeof(IChildProcessingRunBackend)),
            "The guard must execute the actual child-boundary factory rather than assume its dependencies.");
        Assert.IsTrue(
            guard.CapturedFactoryTypes.Contains(typeof(IScheduledRunWorkGate)),
            "The guard must execute the actual count-gate factory and its deferred repository callback.");
        Assert.IsFalse(guard.IsForbidden(typeof(CountryCodeService)), "country identity remains lightweight");
        Assert.IsFalse(guard.IsForbidden(typeof(CityResolverProfileCatalogService)), "resolver profiles remain lightweight");
    }

    [TestMethod]
    public async Task ProductionWebManualRoute_DelegatesOnceWithoutGeodataOrExecutorResolution()
    {
        var fixture = WebBoundaryFixture.Create(1);
        try
        {
            var manual = fixture.Provider.GetRequiredService<IManualProcessingRunCoordinator>();

            Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await manual.TriggerManualAsync(), "manual-admission");

            ChildInvocation invocation = fixture.Boundary.SingleInvocation();
            SettlementObservation settlement = fixture.Observer.SingleSettlement();
            Assert.AreEqual(ProcessingRunTrigger.Manual, invocation.Request.Trigger, "manual-trigger");
            Assert.AreSame(invocation.Request, settlement.Request, "manual-request-identity");
            Assert.AreEqual(invocation.Token, settlement.ActiveToken, "manual-coordinator-token");
            Assert.AreEqual(0, fixture.CountRepository.Calls, "manual-does-not-detect-work");
            Assert.AreEqual(0, fixture.Forbidden.TotalResolutionCount, "manual-forbidden-resolution");
            Assert.AreEqual(0, fixture.IndexObserver.Calls, "manual-country-index-load");
            AssertNoChildRuntimeEffect(fixture, "manual");

            await fixture.DisposeAsync();
            AssertPostProviderDisposal(fixture, "manual", expectedCountCalls: 0);
        }
        finally
        {
            if (!fixture.ProviderDisposed)
            {
                await fixture.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task ProductionManualRoute_DoesNotResolveTheScheduledRepositoryCounter()
    {
        await using var fixture = WebBoundaryFixture.Create(1, preserveProductionCounter: true);
        var manual = fixture.Provider.GetRequiredService<IManualProcessingRunCoordinator>();

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await manual.TriggerManualAsync(), "manual-admission");
        _ = fixture.Boundary.SingleInvocation();
        Assert.AreEqual(0, fixture.Forbidden.ResolutionCount("Immich database repository"), "manual-keeps-production-count-repository-lazy");
        Assert.AreEqual(0, fixture.CountRepository.Calls, "manual-does-not-replace-or-invoke-the-production-count-gate");
    }

    [TestMethod]
    public void ProductionProcessingGraph_ReportsAnIntentionalForbiddenConstructorEdge()
    {
        using var fixture = WebBoundaryFixture.Create(0);
        var services = new ServiceCollection();
        foreach (ServiceDescriptor descriptor in fixture.ProductionDescriptors)
        {
            services.Add(descriptor);
        }

        services.RemoveAll<IChildProcessingRunBackend>();
        services.AddScoped<IChildProcessingRunBackend, IntentionalForbiddenConstructorBackend>();
        var guard = new ProcessingFactoryGraph(services);

        string? failure = guard.FindForbiddenPath(typeof(IChildProcessingRunBackend));

        Assert.IsNotNull(failure, "the constructor edge must be rejected");
        StringAssert.Contains(failure, nameof(IChildProcessingRunBackend), "constructor-path-root");
        StringAssert.Contains(failure, nameof(AdministrativeAreaResolverService), "constructor-path-forbidden-type");
        StringAssert.Contains(failure, "administrative resolver", "constructor-path-category");
    }

    [TestMethod]
    public void ProductionProcessingGraph_RejectsRepositoryFromChildFactoryWhileAllowingTheScheduledDetectorChain()
    {
        using var fixture = WebBoundaryFixture.Create(0);
        var services = new ServiceCollection();
        foreach (ServiceDescriptor descriptor in fixture.ProductionDescriptors)
        {
            services.Add(descriptor);
        }

        services.RemoveAll<IChildProcessingRunBackend>();
        services.AddScoped<IChildProcessingRunBackend>(sp => new IntentionalRepositoryFactoryBackend(
            sp.GetRequiredService<ImmichDbRepository>()));

        var guard = new ProcessingFactoryGraph(services);
        string? failure = guard.FindForbiddenPath(typeof(IChildProcessingRunBackend));

        Assert.IsNotNull(failure, "a child factory must not acquire the repository");
        StringAssert.Contains(failure, nameof(IChildProcessingRunBackend), "repository-path-root");
        StringAssert.Contains(failure, nameof(ImmichDbRepository), "repository-path-forbidden-type");
        StringAssert.Contains(failure, "scheduled repository", "repository-path-category");

        var detectorGuard = new ProcessingFactoryGraph(fixture.ProductionDescriptors);
        Assert.IsNull(
            detectorGuard.FindForbiddenPath(typeof(IScheduledRunWorkGate)),
            "the exact delayed scheduled detector repository chain remains legal");
    }

    [TestMethod]
    public void ProcessingFactoryGraph_ReportsTheShortestCompetingForbiddenPath()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IndirectForbiddenPath>();
        services.AddSingleton<CompetingForbiddenPathsRoot>(sp => new CompetingForbiddenPathsRoot(
            sp.GetRequiredService<IndirectForbiddenPath>(),
            sp.GetRequiredService<OvertureDivisionsService>()));
        var guard = new ProcessingFactoryGraph(services);

        string? failure = guard.FindForbiddenPath(typeof(CompetingForbiddenPathsRoot));

        Assert.AreEqual(
            "CompetingForbiddenPathsRoot -> OvertureDivisionsService (forbidden country index)",
            failure,
            "the direct path must win over the earlier but longer constructor dependency");
    }

    [TestMethod]
    public async Task LightweightCountryAndProfileIdentity_AccessDoesNotLoadCountryGeometry()
    {
        await using var fixture = WebBoundaryFixture.Create(0);

        var countryCodes = fixture.Provider.GetRequiredService<CountryCodeService>();
        var profiles = fixture.Provider.GetRequiredService<CityResolverProfileCatalogService>();

        Assert.AreEqual("CHE", countryCodes.Alpha2ToIso3("CH"), "country-identity");
        Assert.IsNotNull(profiles.GetProfile(null, "CHE"), "resolver-profile-identity");
        Assert.AreEqual(0, fixture.IndexObserver.Calls, "identity-does-not-load-country-index");
        Assert.AreEqual(0, fixture.Forbidden.TotalResolutionCount, "identity-does-not-resolve-heavy-processing-services");
    }

    [TestMethod]
    public async Task ProductionWebScheduledPositiveRoute_UsesTheCountGateThenDelegatesOnce()
    {
        var fixture = WebBoundaryFixture.Create(1);
        try
        {
            var scheduled = fixture.Provider.GetRequiredService<IScheduledRunTrigger>();

            Assert.AreEqual(
                ScheduledTriggerResult.AcceptedAfterTerminal,
                await scheduled.TriggerScheduledAsync(CancellationToken.None),
                "scheduled-positive-admission");

            ChildInvocation invocation = fixture.Boundary.SingleInvocation();
            SettlementObservation settlement = fixture.Observer.SingleSettlement();
            Assert.AreEqual(ProcessingRunTrigger.Scheduled, invocation.Request.Trigger, "scheduled-trigger");
            Assert.AreSame(invocation.Request, settlement.Request, "scheduled-request-identity");
            Assert.AreEqual(invocation.Token, settlement.ActiveToken, "scheduled-coordinator-token");
            Assert.AreEqual(1, fixture.CountRepository.Calls, "scheduled-detector-count");
            CollectionAssert.AreEqual(new[] { "count", "child" }, fixture.Events.ToArray(), "detector-before-child-boundary");
            Assert.AreEqual(0, fixture.Forbidden.TotalResolutionCount, "scheduled-forbidden-resolution");
            Assert.AreEqual(0, fixture.IndexObserver.Calls, "scheduled-country-index-load");
            AssertNoChildRuntimeEffect(fixture, "scheduled-positive");

            await fixture.DisposeAsync();
            AssertPostProviderDisposal(fixture, "scheduled-positive", expectedCountCalls: 1);
        }
        finally
        {
            if (!fixture.ProviderDisposed)
            {
                await fixture.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task ProductionWebScheduledEmptyRoute_FinalizesLocallyWithoutGeodataOrChildResolution()
    {
        var fixture = WebBoundaryFixture.Create(0);
        try
        {
            var scheduled = fixture.Provider.GetRequiredService<IScheduledRunTrigger>();
            var coordinator = fixture.Provider.GetRequiredService<ProcessingRunCoordinator>();

            Assert.AreEqual(
                ScheduledTriggerResult.AcceptedAfterTerminal,
                await scheduled.TriggerScheduledAsync(CancellationToken.None),
                "scheduled-empty-admission");

            Assert.AreEqual(1, fixture.CountRepository.Calls, "scheduled-empty-detector-count");
            Assert.AreEqual(0, fixture.Boundary.InvocationCount, "scheduled-empty-child-count");
            CollectionAssert.AreEqual(new[] { "count" }, fixture.Events.ToArray(), "scheduled-empty-stays-local");
            ScheduledChildWorkerExecution.AcceptedEmptyScheduledRunAssertions.AssertLocalEmptyOutcome(
                fixture.State,
                coordinator,
                "composition-empty");
            Assert.AreEqual(0, fixture.Forbidden.TotalResolutionCount, "scheduled-empty-forbidden-resolution");
            Assert.AreEqual(0, fixture.IndexObserver.Calls, "scheduled-empty-country-index-load");
            AssertNoChildRuntimeEffect(fixture, "scheduled-empty");

            await fixture.DisposeAsync();
            AssertPostProviderDisposal(fixture, "scheduled-empty", expectedCountCalls: 1);
        }
        finally
        {
            if (!fixture.ProviderDisposed)
            {
                await fixture.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task IdentityFactoryThatAlsoLoadsCountryGeometry_ReportsForbiddenPathAndStopsBeforeSqlite()
    {
        await using var fixture = WebBoundaryFixture.Create(1, accidentalCountryIndexBackend: true);
        var guard = new ProcessingFactoryGraph(fixture.Services);
        string? failure = guard.FindForbiddenPath(typeof(IChildProcessingRunBackend));

        Assert.IsNotNull(failure, "the accidental factory must be classified as forbidden");
        StringAssert.Contains(failure, nameof(IChildProcessingRunBackend), "path-root");
        StringAssert.Contains(failure, nameof(OvertureDivisionsService), "path-forbidden-index-edge");
        StringAssert.Contains(failure, "country index", "path-category");
        CollectionAssert.Contains(
            guard.DirectDependencies(typeof(IChildProcessingRunBackend)).ToArray(),
            typeof(CountryCodeService),
            "the same factory also resolves legal country identity");

        var manual = fixture.Provider.GetRequiredService<IManualProcessingRunCoordinator>();
        var exception = await Assert.ThrowsExactlyAsync<CountryIndexLoadObservedException>(
            () => manual.TriggerManualAsync());

        Assert.AreEqual(1, fixture.IndexObserver.Calls, "index-observer-call");
        CollectionAssert.Contains(fixture.Events.ToArray(), "identity", "legal-identity-was-actually-accessed-before-the-forbidden-call");
        Assert.AreEqual(0, fixture.Forbidden.TotalResolutionCount, "the intentional backend owns its explicit test-only dependency");
    }

    private static void AssertNoChildRuntimeEffect(WebBoundaryFixture fixture, string route)
    {
        Assert.AreEqual(0, fixture.Forbidden.ResolutionCount("in-process executor"), route + "-executor");
        Assert.AreEqual(0, fixture.Forbidden.ResolutionCount("administrative resolver"), route + "-resolver");
        Assert.AreEqual(0, fixture.Forbidden.ResolutionCount("Overture divisions"), route + "-divisions");
        Assert.AreEqual(0, fixture.Forbidden.ResolutionCount("Overture places"), route + "-places");
        Assert.AreEqual(0, fixture.Forbidden.ResolutionCount("GADM divisions"), route + "-gadm-divisions");
        Assert.AreEqual(0, fixture.Forbidden.ResolutionCount("worker command builder"), route + "-command-builder");
        Assert.AreEqual(0, fixture.Forbidden.ResolutionCount("child launcher"), route + "-launcher");
        Assert.AreEqual(fixture.Boundary.InvocationCount, fixture.Boundary.DisposedBackendCount, route + "-child-scope-disposed");
    }

    private static void AssertPostProviderDisposal(WebBoundaryFixture fixture, string route, int expectedCountCalls)
    {
        Assert.IsTrue(fixture.ProviderDisposed, route + "-provider-disposed");
        Assert.AreEqual(expectedCountCalls, fixture.CountRepository.Calls, route + "-count-unchanged-after-provider-disposal");
        Assert.AreEqual(0, fixture.Forbidden.TotalResolutionCount, route + "-forbidden-resolution-after-provider-disposal");
        Assert.AreEqual(0, fixture.IndexObserver.Calls, route + "-country-index-load-after-provider-disposal");
        AssertNoChildRuntimeEffect(fixture, route + "-after-provider-disposal");
    }

    private sealed class WebBoundaryFixture : IAsyncDisposable, IDisposable
    {
        private readonly string _fixtureRoot;

        private WebBoundaryFixture(
            ServiceCollection services,
            ServiceProvider provider,
            string fixtureRoot,
            IReadOnlyList<ServiceDescriptor> productionDescriptors,
            RecordingCountRepository countRepository,
            RecordingChildBoundary boundary,
            ForbiddenResolutionSentinels forbidden,
            SettlementObserver observer,
            CountryIndexObserver indexObserver,
            ConcurrentQueue<string> events)
        {
            Services = services;
            Provider = provider;
            _fixtureRoot = fixtureRoot;
            ProductionDescriptors = productionDescriptors;
            CountRepository = countRepository;
            Boundary = boundary;
            Forbidden = forbidden;
            Observer = observer;
            IndexObserver = indexObserver;
            Events = events;
            State = provider.GetRequiredService<ProcessingState>();
            Reporter = provider.GetRequiredService<ProcessingStateEventReporter>();
        }

        internal ServiceCollection Services { get; }
        internal IReadOnlyList<ServiceDescriptor> ProductionDescriptors { get; }
        internal ServiceProvider Provider { get; }
        internal RecordingCountRepository CountRepository { get; }
        internal RecordingChildBoundary Boundary { get; }
        internal ForbiddenResolutionSentinels Forbidden { get; }
        internal SettlementObserver Observer { get; }
        internal CountryIndexObserver IndexObserver { get; }
        internal ConcurrentQueue<string> Events { get; }
        internal ProcessingState State { get; }
        internal ProcessingStateEventReporter Reporter { get; }
        internal bool ProviderDisposed { get; private set; }

        internal static WebBoundaryFixture Create(
            long count,
            bool accidentalCountryIndexBackend = false,
            bool preserveProductionCounter = false)
        {
            string fixtureRoot = Path.Combine(Path.GetTempPath(), "immich-reversegeo-change39-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtureRoot);
            string bundledDataDirectory = Path.Combine(fixtureRoot, "bundled-data");
            Directory.CreateDirectory(bundledDataDirectory);
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "data", "iso3166.json"),
                Path.Combine(bundledDataDirectory, "iso3166.json"));
            var services = new ServiceCollection();
            services.AddWebComposition(ApplicationCompositionContext.Create(
                CompositionEnvironment.Development,
                fixtureRoot,
                Path.Combine(fixtureRoot, "data"),
                Path.Combine(fixtureRoot, "config")));
            IReadOnlyList<ServiceDescriptor> productionDescriptors = services.ToArray();

            Assert.IsNotNull(
                services.Single(descriptor => descriptor.ServiceType == typeof(IScheduledRunWorkGate)).ImplementationFactory,
                "the production count-gate factory must remain inspectable before the test override");
            Assert.IsNotNull(
                services.Single(descriptor => descriptor.ServiceType == typeof(IChildProcessingRunBackend)).ImplementationFactory,
                "the production child-boundary factory must remain inspectable before the test override");

            var events = new ConcurrentQueue<string>();
            var countRepository = new RecordingCountRepository(count, events);
            var boundary = new RecordingChildBoundary(events);
            var forbidden = new ForbiddenResolutionSentinels();
            var observer = new SettlementObserver();
            var indexObserver = new CountryIndexObserver();

            if (!preserveProductionCounter)
            {
                services.RemoveAll<IScheduledRunWorkCounter>();
                services.AddSingleton<IScheduledRunWorkCounter>(countRepository);
            }
            services.AddSingleton(events);
            services.RemoveAll<IProcessingRunCoordinatorObserver>();
            services.AddSingleton<IProcessingRunCoordinatorObserver>(observer);
            services.RemoveAll<IChildProcessingRunBackend>();
            if (accidentalCountryIndexBackend)
            {
                services.AddScoped<IChildProcessingRunBackend>(sp => new AccidentalCountryIndexBackend(
                    sp.GetRequiredService<CountryCodeService>(),
                    sp.GetRequiredService<OvertureDivisionsService>(),
                    sp.GetRequiredService<ConcurrentQueue<string>>()));
                services.RemoveAll<OvertureDivisionsService>();
                services.AddSingleton(CreateObservedDivisionsService(fixtureRoot, indexObserver));
            }
            else
            {
                services.AddScoped<IChildProcessingRunBackend>(_ => new RecordingChildBackend(boundary));
                AddForbiddenSentinels(services, forbidden);
            }

            ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
            return new WebBoundaryFixture(
                services,
                provider,
                fixtureRoot,
                productionDescriptors,
                countRepository,
                boundary,
                forbidden,
                observer,
                indexObserver,
                events);
        }

        public void Dispose()
        {
            Provider.Dispose();
            ProviderDisposed = true;
            DeleteFixture();
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            ProviderDisposed = true;
            DeleteFixture();
        }

        private void DeleteFixture()
        {
            if (Directory.Exists(_fixtureRoot))
            {
                Directory.Delete(_fixtureRoot, recursive: true);
            }
        }

        private static OvertureDivisionsService CreateObservedDivisionsService(string fixtureRoot, CountryIndexObserver observer)
        {
            return new OvertureDivisionsService(
                NullLogger<OvertureDivisionsService>.Instance,
                (OverturePlacesService)RuntimeHelpers.GetUninitializedObject(typeof(OverturePlacesService)),
                Path.Combine(fixtureRoot, "data"),
                Path.Combine(fixtureRoot, "bundled-data"),
                _ => "CHE",
                new OvertureDivisionsTestHooks
                {
                    FileExists = _ => true,
                    BeforeBundledCountryIndexLoad = observer.ThrowBeforeFileAccess
                });
        }

        private static void AddForbiddenSentinels(IServiceCollection services, ForbiddenResolutionSentinels forbidden)
        {
            forbidden.Add<IProcessingRunExecutor>(services, "in-process executor");
            forbidden.Add<ProcessingRunExecutor>(services, "in-process executor");
            forbidden.Add<IProcessingAdministrativeResolver>(services, "administrative resolver");
            forbidden.Add<AdministrativeAreaResolverService>(services, "administrative resolver");
            forbidden.Add<IProcessingInfrastructureLookup>(services, "airport infrastructure lookup");
            forbidden.Add<ProcessingInfrastructureLookup>(services, "airport infrastructure lookup");
            forbidden.Add<OvertureDivisionsService>(services, "Overture divisions");
            forbidden.Add<OvertureDivisionCacheService>(services, "Overture division cache");
            forbidden.Add<OverturePlacesService>(services, "Overture places");
            forbidden.Add<GadmDivisionsService>(services, "GADM divisions");
            forbidden.Add<GadmDivisionCacheService>(services, "GADM division cache");
            forbidden.Add<ImmichDbRepository>(services, "Immich database repository");
            forbidden.Add<IWorkerCommandInvocationBuilder>(services, "worker command builder");
            forbidden.Add<IChildWorkerLauncher>(services, "child launcher");
            forbidden.Add<IChildProcessFactory>(services, "child process factory");
            forbidden.Add<WorkerEventStateBridgeFactory>(services, "worker event bridge");
        }
    }

    private sealed class RecordingCountRepository(long count, ConcurrentQueue<string> events) : IScheduledRunWorkCounter
    {
        internal int Calls { get; private set; }

        public Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            events.Enqueue("count");
            return Task.FromResult(count);
        }
    }

    private sealed class RecordingChildBoundary(ConcurrentQueue<string> events)
    {
        private readonly ConcurrentQueue<ChildInvocation> _invocations = [];

        internal int InvocationCount => _invocations.Count;
        internal int DisposedBackendCount { get; private set; }

        internal void RecordBackendDisposal()
        {
            DisposedBackendCount++;
        }

        internal ChildInvocation SingleInvocation()
        {
            Assert.AreEqual(1, InvocationCount, "child-boundary-call-count");
            return _invocations.Single();
        }

        internal async Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            _invocations.Enqueue(new ChildInvocation(request, cancellationToken));
            events.Enqueue("child");
            var session = await reporter.OpenRunAsync(request, Now, CancellationToken.None);
            await session.DetermineEligibilityAsync(0, CancellationToken.None);
            var result = new ProcessingRunResult(request, Now, Now, 0, 0, 0, 0, ProcessingRunOutcome.Completed, null);
            await session.FinishAsync(result);
            return result;
        }
    }

    private sealed class RecordingChildBackend(RecordingChildBoundary boundary) : IChildProcessingRunBackend, IAsyncDisposable
    {
        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            return boundary.ExecuteAsync(request, reporter, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            boundary.RecordBackendDisposal();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AccidentalCountryIndexBackend(
        CountryCodeService countryCodes,
        OvertureDivisionsService divisions,
        ConcurrentQueue<string> events) : IChildProcessingRunBackend
    {
        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = reporter;
            _ = countryCodes.Alpha2ToIso3("CH");
            events.Enqueue("identity");
            _ = divisions.FindBundledCountryAsync(47.0, 8.0, cancellationToken);
            throw new AssertFailedException("The country-index observer should have stopped the accidental child backend first.");
        }
    }

    private sealed class IntentionalForbiddenConstructorBackend(
        AdministrativeAreaResolverService resolver) : IChildProcessingRunBackend
    {
        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            _ = resolver;
            _ = request;
            _ = reporter;
            _ = cancellationToken;
            throw new InvalidOperationException("This test-only constructor violation must remain structural only.");
        }
    }

    private sealed class IntentionalRepositoryFactoryBackend(ImmichDbRepository repository) : IChildProcessingRunBackend
    {
        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            _ = repository;
            _ = request;
            _ = reporter;
            _ = cancellationToken;
            throw new InvalidOperationException("This test-only factory violation must remain structural only.");
        }
    }

    private sealed class CompetingForbiddenPathsRoot(
        IndirectForbiddenPath indirect,
        OvertureDivisionsService direct)
    {
        internal void KeepConstructorEdges()
        {
            _ = indirect;
            _ = direct;
        }
    }

    private sealed class IndirectForbiddenPath(AdministrativeAreaResolverService resolver)
    {
        internal void KeepConstructorEdge()
        {
            _ = resolver;
        }
    }

    private sealed class SettlementObserver : IProcessingRunCoordinatorObserver
    {
        private readonly ConcurrentQueue<SettlementObservation> _settlements = [];

        public ValueTask BeforeChildSettlementAsync(ProcessingRunRequest request, CancellationToken activeToken)
        {
            _settlements.Enqueue(new SettlementObservation(request, activeToken));
            return ValueTask.CompletedTask;
        }

        internal SettlementObservation SingleSettlement()
        {
            Assert.AreEqual(1, _settlements.Count, "child-settlement-count");
            return _settlements.Single();
        }
    }

    private sealed class CountryIndexObserver
    {
        internal int Calls { get; private set; }

        internal void ThrowBeforeFileAccess()
        {
            Calls++;
            throw new CountryIndexLoadObservedException();
        }
    }

    private sealed class CountryIndexLoadObservedException() : Exception("country-index-load-observed");

    private sealed class ForbiddenResolutionSentinels
    {
        private readonly ConcurrentDictionary<string, int> _counts = [];

        internal int TotalResolutionCount => _counts.Values.Sum();

        internal int ResolutionCount(string name) => _counts.GetValueOrDefault(name);

        internal void Add<T>(IServiceCollection services, string name)
            where T : class
        {
            services.RemoveAll<T>();
            services.AddSingleton<T>(_ => Fail<T>(name));
        }

        private T Fail<T>(string name)
            where T : class
        {
            _counts.AddOrUpdate(name, 1, (_, value) => value + 1);
            throw new AssertFailedException("Web processing must not resolve " + name + ".");
        }
    }

    private sealed record ChildInvocation(ProcessingRunRequest Request, CancellationToken Token);

    private sealed record SettlementObservation(ProcessingRunRequest Request, CancellationToken ActiveToken);

    private sealed class ProcessingFactoryGraph
    {
        private static readonly IReadOnlyDictionary<Type, string> Forbidden = new Dictionary<Type, string>
        {
            [typeof(IProcessingRunExecutor)] = "in-process executor",
            [typeof(ProcessingRunExecutor)] = "in-process executor",
            [typeof(IProcessingAdministrativeResolver)] = "administrative resolver",
            [typeof(AdministrativeAreaResolverService)] = "administrative resolver",
            [typeof(OvertureDivisionsService)] = "country index",
            [typeof(OvertureDivisionCacheService)] = "Overture division cache",
            [typeof(OverturePlacesService)] = "airport infrastructure",
            [typeof(IProcessingInfrastructureLookup)] = "airport infrastructure",
            [typeof(ProcessingInfrastructureLookup)] = "airport infrastructure",
            [typeof(GadmDivisionsService)] = "GADM divisions",
            [typeof(GadmDivisionCacheService)] = "GADM division cache",
            [typeof(ImmichDbRepository)] = "scheduled repository"
        };

        private readonly IReadOnlyDictionary<Type, ServiceDescriptor> _descriptors;
        private readonly Dictionary<Type, IReadOnlyList<Type>> _edges = [];
        private readonly List<string> _opaqueFactories = [];

        internal ProcessingFactoryGraph(IEnumerable<ServiceDescriptor> descriptors)
        {
            _descriptors = descriptors
                .GroupBy(descriptor => descriptor.ServiceType)
                .ToDictionary(group => group.Key, group => group.Last());
        }

        internal int OpaqueFactoryCount => _opaqueFactories.Count;

        internal string OpaqueFactorySummary => string.Join(Environment.NewLine, _opaqueFactories);

        internal IReadOnlyCollection<Type> CapturedFactoryTypes => _edges
            .Where(pair => _descriptors.TryGetValue(pair.Key, out ServiceDescriptor? descriptor)
                           && descriptor.ImplementationFactory is not null)
            .Select(pair => pair.Key)
            .ToArray();

        internal bool IsForbidden(Type type) => Forbidden.ContainsKey(type);

        internal IReadOnlyCollection<Type> DirectDependencies(Type serviceType)
        {
            return DependenciesFor(serviceType);
        }

        internal string? FindForbiddenPath(params Type[] roots)
        {
            var paths = new Queue<IReadOnlyList<Type>>();
            foreach (Type root in roots)
            {
                paths.Enqueue([root]);
            }

            while (paths.TryDequeue(out IReadOnlyList<Type>? path))
            {
                Type current = path[^1];
                if (Forbidden.TryGetValue(current, out string? category)
                    && !IsAllowedScheduledRepositoryPath(path))
                {
                    return string.Join(" -> ", path.Select(type => type.Name)) + " (forbidden " + category + ")";
                }

                foreach (Type dependency in DependenciesFor(current))
                {
                    if (path.Contains(dependency))
                    {
                        continue;
                    }

                    paths.Enqueue(path.Append(dependency).ToArray());
                }
            }

            return _opaqueFactories.Count == 0
                ? null
                : "Opaque production factory: " + _opaqueFactories[0];
        }

        private static bool IsAllowedScheduledRepositoryPath(IReadOnlyList<Type> path)
        {
            return path.Count >= 4
                && path[^4] == typeof(IScheduledRunWorkGate)
                && path[^3] == typeof(IScheduledRunWorkCounter)
                && path[^2] == typeof(RepositoryScheduledRunWorkCounter)
                && path[^1] == typeof(ImmichDbRepository);
        }

        private IReadOnlyList<Type> DependenciesFor(Type serviceType)
        {
            if (_edges.TryGetValue(serviceType, out IReadOnlyList<Type>? cached))
            {
                return cached;
            }

            if (!_descriptors.TryGetValue(serviceType, out ServiceDescriptor? descriptor))
            {
                return _edges[serviceType] = [];
            }

            if (descriptor.ImplementationType is Type implementationType)
            {
                ConstructorInfo? constructor = implementationType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .OrderByDescending(candidate => candidate.GetParameters().Length)
                    .FirstOrDefault();
                return _edges[serviceType] = constructor?.GetParameters().Select(parameter => parameter.ParameterType).ToArray() ?? [];
            }

            if (descriptor.ImplementationFactory is not Func<IServiceProvider, object> factory)
            {
                return _edges[serviceType] = [];
            }

            var recorder = new FactoryDependencyRecorder();
            object? instance = null;
            try
            {
                instance = factory(recorder);
                if (instance is CountBackedScheduledRunWorkGate gate)
                {
                    try
                    {
                        gate.HasWorkAsync(CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (NullReferenceException)
                    {
                        // The recorder supplied an uninitialized repository only to observe the deferred production callback.
                    }
                }
                else if (instance is RepositoryScheduledRunWorkCounter counter)
                {
                    try
                    {
                        counter.GetUnprocessedCountAsync(CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (NullReferenceException)
                    {
                        // The recorder supplied an uninitialized repository only to observe the deferred production callback.
                    }
                }
            }
            catch (Exception exception)
            {
                _opaqueFactories.Add(serviceType.Name + ": " + exception);
            }

            Type[] requests = recorder.Requests.ToArray();
            if (instance is RepositoryScheduledRunWorkCounter)
            {
                _edges[typeof(RepositoryScheduledRunWorkCounter)] = requests;
                return _edges[serviceType] = [typeof(RepositoryScheduledRunWorkCounter)];
            }

            return _edges[serviceType] = requests;
        }

        private sealed class FactoryDependencyRecorder : IServiceProvider
        {
            private readonly Dictionary<Type, object?> _instances = [];
            private readonly ConcurrentQueue<Type> _requests = [];

            internal IReadOnlyCollection<Type> Requests => _requests.ToArray();

            public object? GetService(Type serviceType)
            {
                _requests.Enqueue(serviceType);
                return _instances.TryGetValue(serviceType, out object? instance)
                    ? instance
                    : _instances[serviceType] = CreateStub(serviceType);
            }

            private static object? CreateStub(Type serviceType)
            {
                if (serviceType == typeof(TimeProvider))
                {
                    return TimeProvider.System;
                }

                if (serviceType == typeof(IServiceScopeFactory))
                {
                    return new GraphScopeFactory();
                }

                if (serviceType == typeof(IOptions<HostOptions>))
                {
                    return Options.Create(new HostOptions());
                }

                if (serviceType == typeof(IHostApplicationLifetime)
                    || serviceType == typeof(IProcessingRunCoordinatorObserver))
                {
                    return null;
                }

                if (serviceType.IsGenericType
                    && serviceType.GetGenericTypeDefinition() == typeof(ILogger<>))
                {
                    Type loggerType = typeof(NullLogger<>).MakeGenericType(serviceType.GenericTypeArguments[0]);
                    return loggerType.GetField(nameof(NullLogger<object>.Instance), BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
                }

                if (serviceType.IsValueType)
                {
                    return Activator.CreateInstance(serviceType);
                }

                if (serviceType.IsInterface)
                {
                    MethodInfo create = typeof(DispatchProxy).GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Single(method => method.Name == nameof(DispatchProxy.Create) && method.IsGenericMethodDefinition);
                    return create.MakeGenericMethod(serviceType, typeof(GraphDispatchProxy)).Invoke(null, null);
                }

                return RuntimeHelpers.GetUninitializedObject(serviceType);
            }
        }

        private sealed class GraphScopeFactory : IServiceScopeFactory
        {
            public IServiceScope CreateScope()
            {
                throw new InvalidOperationException("The graph recorder must not create a runtime scope.");
            }
        }

        public class GraphDispatchProxy : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                Type returnType = targetMethod?.ReturnType ?? typeof(void);
                if (returnType == typeof(void))
                {
                    return null;
                }

                if (returnType == typeof(Task))
                {
                    return Task.CompletedTask;
                }

                return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
            }
        }
    }
}
