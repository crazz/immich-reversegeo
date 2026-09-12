using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.ApplicationRole;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
[TestCategory("Change41")]
public sealed class StandardDeploymentModeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestMethod]
    [DataRow(null)]
    [DataRow("standard")]
    public void UnsetAndExplicitStandardSelectionEnterTheProductionStandardBuilder(string? selectedValue)
    {
        StandardHostFixture? fixture = null;
        var selectorReads = 0;
        var errors = new StringWriter();
        try
        {
            ApplicationRoleStartup.Begin(
                Array.Empty<string>(),
                name =>
                {
                    Assert.AreEqual(DeploymentModeResolver.EnvironmentVariableName, name);
                    selectorReads++;
                    return selectedValue;
                },
                errors,
                (mode, _) => fixture = StandardHostFixture.CreateBuilderOnly(includeRuntimeFiles: true, mode),
                _ => Assert.Fail("internal-worker continuation"),
                (_, _) => Assert.Fail("run-once continuation"),
                _ => Assert.Fail("private selection failure"),
                _ => Assert.Fail("deployment mode failure"));

            Assert.AreEqual(1, selectorReads, "mode-resolved-once");
            Assert.AreEqual(string.Empty, errors.ToString());
            Assert.IsNotNull(fixture, "resolved-standard-enters-production-builder");
            AssertStandardDescriptor(fixture.Builder.Services, typeof(ProcessingRunCoordinator), ServiceLifetime.Singleton);
            CollectionAssert.AreEqual(new[] { "DATA_DIR", "CONFIG_DIR" }, fixture.EnvironmentReads.ToArray(), "builder-does-not-reread-mode");
        }
        finally
        {
            fixture?.Dispose();
        }
    }

    [TestMethod]
    public void StandardComposition_RequiresResolvedStandardAndPreservesTheSingleChildOnlyWebGraph()
    {
        using var fixture = StandardHostFixture.CreateBuilderOnly(includeRuntimeFiles: true);
        IServiceCollection services = fixture.Builder.Services;

        AssertStandardDescriptor(services, typeof(ProcessingRunCoordinator), ServiceLifetime.Singleton);
        AssertStandardDescriptor(services, typeof(IManualProcessingRunCoordinator), ServiceLifetime.Singleton);
        AssertStandardDescriptor(services, typeof(IScheduledRunTrigger), ServiceLifetime.Singleton);
        AssertStandardDescriptor(services, typeof(ProcessingBackgroundService), ServiceLifetime.Singleton);
        AssertStandardDescriptor(services, typeof(IScheduledRunWorkGate), ServiceLifetime.Singleton);
        AssertStandardDescriptor(services, typeof(IChildProcessingRunBackend), ServiceLifetime.Scoped);
        AssertStandardDescriptor(services, typeof(IChildWorkerLauncher), ServiceLifetime.Singleton);
        AssertStandardDescriptor(services, typeof(ChildWorkerStartupValidator), ServiceLifetime.Singleton);
        AssertStandardDescriptor(services, typeof(IServer), ServiceLifetime.Singleton);
        Assert.IsTrue(services.Any(descriptor => descriptor.ServiceType == typeof(IPostConfigureOptions<RazorComponentsServiceOptions>)), "razor-components-present");
        Assert.IsTrue(services.Any(descriptor => descriptor.ServiceType == typeof(IConfigureOptions<CircuitOptions>)), "interactive-server-present");

        foreach (Type lookupOrDataDependency in new[]
        {
            typeof(ImmichDbRepository),
            typeof(SkippedAssetsRepository),
            typeof(ICacheInventory),
            typeof(CacheDeletionCommand),
            typeof(CacheMutationPageControllerFactory),
            typeof(ConfigService)
        })
        {
            Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == lookupOrDataDependency), lookupOrDataDependency.Name);
        }

        Assert.AreEqual(0, services.Count(descriptor => descriptor.ServiceType == typeof(IProcessingRunExecutor)), "no-authoritative-executor");
        Assert.AreEqual(0, services.Count(descriptor => descriptor.ServiceType == typeof(ProcessingRunExecutor)), "no-concrete-executor");
        Assert.AreEqual(0, services.Count(descriptor => descriptor.ServiceType.Name == "InProcessProcessingRunBackend"), "no-in-process-fallback");
        CollectionAssert.AreEqual(new[] { "DATA_DIR", "CONFIG_DIR" }, fixture.EnvironmentReads.ToArray(), "resolved-mode-is-not-read-again");

        var wrongServices = new ServiceCollection();
        var wrongContext = ApplicationCompositionContext.Create(
            CompositionEnvironment.Development,
            fixture.Root,
            fixture.DataDirectory,
            fixture.ConfigDirectory,
            DeploymentMode.WebOnly);
        Assert.ThrowsExactly<ArgumentException>(() => wrongServices.AddStandardWebComposition(wrongContext));
        Assert.HasCount(0, wrongServices, "wrong-mode-does-not-partially-compose");

        var workerServices = new ServiceCollection();
        workerServices.AddInternalWorkerComposition(ApplicationCompositionContext.Create(
            CompositionEnvironment.Development,
            fixture.Root,
            fixture.DataDirectory,
            fixture.ConfigDirectory));
        Assert.AreEqual(1, workerServices.Count(descriptor => descriptor.ServiceType == typeof(IProcessingRunExecutor)), "worker-owns-authoritative-executor");
        Assert.AreEqual(0, workerServices.Count(descriptor => descriptor.ServiceType == typeof(ProcessingRunCoordinator)), "worker-has-no-web-coordinator");
        Assert.AreEqual(0, workerServices.Count(descriptor => descriptor.ServiceType == typeof(IServer)), "worker-has-no-web-server");
    }

    [TestMethod]
    public async Task StandardHost_StartsMappedWebSurfaceAndStopsIdleWithoutLaunchingAWorker()
    {
        await using var fixture = StandardHostFixture.Create(includeRuntimeFiles: true);
        await fixture.Application.StartAsync().WaitAsync(Bound);
        await fixture.SchedulerInitialization.Entered.Task.WaitAsync(Bound);

        Assert.AreEqual(1, fixture.Server.StartCalls, "server-accept-reached");
        Assert.AreEqual(1, fixture.SchedulerInitialization.Calls, "real-scheduler-started-with-resource-init-boundary");
        Assert.AreEqual(0, fixture.ProcessFactory.StartCalls, "startup-does-not-launch-worker");
        var coordinator = fixture.Application.Services.GetRequiredService<ProcessingRunCoordinator>();
        var scheduler = fixture.Application.Services.GetRequiredService<ProcessingBackgroundService>();
        IHostedService[] hostedServices = fixture.Application.Services.GetServices<IHostedService>().ToArray();
        Assert.AreEqual(1, hostedServices.Count(service => ReferenceEquals(service, coordinator)), "one-hosted-coordinator-alias");
        Assert.AreEqual(1, hostedServices.Count(service => ReferenceEquals(service, scheduler)), "one-hosted-scheduler-alias");
        Assert.AreSame(coordinator, fixture.Application.Services.GetRequiredService<IManualProcessingRunCoordinator>(), "manual-alias-identity");
        Assert.AreSame(coordinator, fixture.Application.Services.GetRequiredService<IScheduledRunTrigger>(), "scheduled-alias-identity");
        string?[] routes = ((IEndpointRouteBuilder)fixture.Application).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();
        foreach (string route in new[] { "/", "/settings", "/lookup", "/data" })
        {
            CollectionAssert.Contains(routes, route, route + "-component-endpoint-is-mapped");
        }

        await fixture.Application.StopAsync().WaitAsync(Bound);

        Assert.AreEqual(1, fixture.Server.StopCalls, "server-stop-reached");
        Assert.AreEqual(0, fixture.ProcessFactory.StartCalls, "idle-shutdown-does-not-launch-worker");
        Assert.AreEqual(
            ProcessingRunAdmissionResult.Stopping,
            await fixture.Application.Services.GetRequiredService<ProcessingRunCoordinator>().TriggerManualAsync());
    }

    [TestMethod]
    public async Task StandardHost_UsesRealCoordinatorDetectorAndChildControlPlaneForManualScheduledAndContention()
    {
        await using var fixture = StandardHostFixture.Create(includeRuntimeFiles: true);
        await fixture.Application.StartAsync().WaitAsync(Bound);
        var coordinator = fixture.Application.Services.GetRequiredService<ProcessingRunCoordinator>();
        var scheduled = fixture.Application.Services.GetRequiredService<IScheduledRunTrigger>();

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await coordinator.TriggerManualAsync().WaitAsync(Bound));
        SimulatedChild manual = await fixture.ProcessFactory.NextAsync().WaitAsync(Bound);
        Assert.AreEqual(0, fixture.Detector.CallCount, "manual-bypasses-scheduled-detector");
        Assert.AreEqual(1, fixture.ChildBackendResolution.Calls, "manual-resolves-one-real-child-backend");
        AssertPrivateWorkerCommand(manual.Descriptor, fixture.RuntimeSource);

        fixture.Detector.Enqueue(true);
        Assert.AreEqual(
            ScheduledTriggerResult.RejectedAlreadyRunning,
            await scheduled.TriggerScheduledAsync(CancellationToken.None).WaitAsync(Bound),
            "scheduled-contention-uses-local-outcome");
        Assert.AreEqual(1, fixture.Detector.CallCount, "scheduled-contention-runs-positive-detector-once");
        Assert.AreEqual(1, fixture.ProcessFactory.StartCalls, "contention-starts-no-second-child");
        Assert.AreEqual(1, fixture.ChildBackendResolution.Calls, "contention-resolves-no-second-child-backend");
        await CompleteAsync(coordinator.ActiveRequest!, manual, ProcessingRunOutcome.Completed, 0);
        await coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

        fixture.Detector.Enqueue(false);
        Assert.AreEqual(
            ScheduledTriggerResult.AcceptedAfterTerminal,
            await scheduled.TriggerScheduledAsync(CancellationToken.None).WaitAsync(Bound),
            "empty-schedule-finalizes-locally");
        Assert.AreEqual(2, fixture.Detector.CallCount, "empty-schedule-detected-once");
        Assert.AreEqual(1, fixture.ProcessFactory.StartCalls, "empty-schedule-resolves-no-child");
        Assert.AreEqual(1, fixture.ChildBackendResolution.Calls, "empty-schedule-resolves-no-child-backend");

        fixture.Detector.Enqueue(true);
        Task<ScheduledTriggerResult> positive = scheduled.TriggerScheduledAsync(CancellationToken.None);
        SimulatedChild scheduledChild = await fixture.ProcessFactory.NextAsync().WaitAsync(Bound);
        AssertPrivateWorkerCommand(scheduledChild.Descriptor, fixture.RuntimeSource);
        await CompleteAsync(coordinator.ActiveRequest!, scheduledChild, ProcessingRunOutcome.Completed, 0);
        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await positive.WaitAsync(Bound));
        Assert.AreEqual(3, fixture.Detector.CallCount, "positive-schedule-detected-once");
        Assert.AreEqual(2, fixture.ProcessFactory.StartCalls, "positive-schedule-starts-one-child");
        Assert.AreEqual(2, fixture.ChildBackendResolution.Calls, "positive-schedule-resolves-one-child-backend");
        Assert.IsNull(fixture.Application.Services.GetService<IProcessingRunExecutor>());
    }

    [TestMethod]
    public async Task StandardHost_InvalidChildPrerequisiteFailsBeforeServerLaterServicesOrChildResolution()
    {
        var later = new LaterHostedService();
        await using var fixture = StandardHostFixture.Create(includeRuntimeFiles: false, later);

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => fixture.Application.StartAsync().WaitAsync(Bound));

        Assert.AreEqual(
            "The child worker runtime files are unavailable. Publish the complete Web application artifact and retry startup.",
            failure.Message);
        Assert.AreEqual(0, fixture.Server.StartCalls, "invalid-prerequisite-prevents-server-accept");
        Assert.AreEqual(0, later.StartCalls, "validator-runs-before-later-hosted-service");
        Assert.AreEqual(0, fixture.ProcessFactory.StartCalls, "invalid-prerequisite-starts-no-worker");
        Assert.AreEqual(0, fixture.ChildBackendResolution.Calls, "invalid-prerequisite-resolves-no-child-backend");
    }

    [TestMethod]
    public async Task StandardHost_DetectorPositiveShutdownBeforeDispatchClaimResolvesNoChild()
    {
        var dispatch = new HostDispatchClaimGate();
        await using var fixture = StandardHostFixture.Create(includeRuntimeFiles: true, observer: dispatch);
        await fixture.Application.StartAsync().WaitAsync(Bound);
        fixture.Detector.Enqueue(true);
        Task<ScheduledTriggerResult> scheduled = fixture.Application.Services
            .GetRequiredService<IScheduledRunTrigger>()
            .TriggerScheduledAsync(CancellationToken.None);

        try
        {
            await dispatch.Entered.Task.WaitAsync(Bound);
            fixture.Application.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            await dispatch.CancellationObserved.Task.WaitAsync(Bound);

            Assert.AreEqual(
                ProcessingRunAdmissionResult.Stopping,
                await fixture.Application.Services.GetRequiredService<ProcessingRunCoordinator>().TriggerManualAsync());
            Assert.AreEqual(0, fixture.ChildBackendResolution.Calls, "shutdown-before-claim-resolves-no-child-backend");
            Assert.AreEqual(0, fixture.ProcessFactory.StartCalls, "shutdown-before-claim-starts-no-process");
        }
        finally
        {
            dispatch.Release.TrySetResult();
        }

        await Assert.ThrowsAsync<OperationCanceledException>(() => scheduled.WaitAsync(Bound));
        await fixture.Application.StopAsync().WaitAsync(Bound);
        Assert.AreEqual(0, fixture.ChildBackendResolution.Calls, "completed-shutdown-race-resolves-no-late-child-backend");
        Assert.AreEqual(0, fixture.ProcessFactory.StartCalls, "completed-shutdown-race-starts-no-late-process");
        Assert.IsNull(fixture.Application.Services.GetRequiredService<ProcessingRunCoordinator>().ActiveRequest);
        Assert.IsFalse(fixture.Application.Services.GetRequiredService<ProcessingState>().IsRunning);
    }

    [TestMethod]
    public async Task StandardHost_ClaimedChildStartingDuringShutdownRemainsOwnedUntilPhysicalFinality()
    {
        await using var fixture = StandardHostFixture.Create(includeRuntimeFiles: true);
        await fixture.Application.StartAsync().WaitAsync(Bound);
        var coordinator = fixture.Application.Services.GetRequiredService<ProcessingRunCoordinator>();
        try
        {
            fixture.RuntimeCapture.ArmNext();
            Task<ProcessingRunAdmissionResult> manual = Task.Run(coordinator.TriggerManualAsync);
            await fixture.RuntimeCapture.Entered.Task.WaitAsync(Bound);
            Task stopping = fixture.Application.StopAsync();
            Assert.AreEqual(ProcessingRunAdmissionResult.Stopping, await coordinator.TriggerManualAsync());
            Assert.AreEqual(1, fixture.ChildBackendResolution.Calls, "dispatch-claim-precedes-runtime-capture");
            Assert.AreEqual(0, fixture.ProcessFactory.StartCalls, "physical-process-start-remains-gated");
            fixture.RuntimeCapture.Release.TrySetResult();
            Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await manual.WaitAsync(Bound));
            SimulatedChild child = await fixture.ProcessFactory.NextAsync().WaitAsync(Bound);
            await SendReadyAsync(child);
            await child.Input.SecondFlush.WaitAsync(Bound);
            Assert.IsFalse(stopping.IsCompleted, "host-owns-start-racing-process-until-exit");
            await CompleteAsync(coordinator.ActiveRequest!, child, ProcessingRunOutcome.Cancelled, 130);
            await stopping.WaitAsync(Bound);
            Assert.AreEqual(1, child.Process.DisposeCalls);
            Assert.IsNull(coordinator.ActiveRequest);
        }
        finally
        {
            fixture.RuntimeCapture.Release.TrySetResult();
            fixture.ProcessFactory.ExitAll();
        }
    }

    [TestMethod]
    public async Task StandardHost_ActiveChildShutdownClosesAdmissionAndWaitsForProcessAndStreams()
    {
        await using var fixture = StandardHostFixture.Create(includeRuntimeFiles: true);
        await fixture.Application.StartAsync().WaitAsync(Bound);
        var coordinator = fixture.Application.Services.GetRequiredService<ProcessingRunCoordinator>();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await coordinator.TriggerManualAsync().WaitAsync(Bound));
        SimulatedChild child = await fixture.ProcessFactory.NextAsync().WaitAsync(Bound);
        await SendReadyAsync(child);

        Task stopping = fixture.Application.StopAsync();
        await child.Input.SecondFlush.WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunAdmissionResult.Stopping, await coordinator.TriggerManualAsync().WaitAsync(Bound));
        Assert.IsFalse(stopping.IsCompleted, "host-stop-joins-live-owned-child");
        await CompleteAsync(coordinator.ActiveRequest!, child, ProcessingRunOutcome.Cancelled, 130);
        await stopping.WaitAsync(Bound);

        Assert.AreEqual(1, fixture.ProcessFactory.StartCalls, "shutdown-does-not-launch-replacement");
        Assert.AreEqual(1, child.Process.DisposeCalls, "owned-process-disposed-once");
        Assert.IsNull(coordinator.ActiveRequest, "exact-run-cleanup-finished-before-host-stop");
        Assert.AreEqual(1, fixture.Server.StopCalls, "server-stop-completes-after-child-finality");
    }

    private static async Task CompleteAsync(
        ProcessingRunRequest request,
        SimulatedChild child,
        ProcessingRunOutcome outcome,
        int exitCode)
    {
        await SendReadyAsync(child);
        var result = new ProcessingRunResult(
            request,
            SessionTestSupport.Start,
            SessionTestSupport.Start,
            0,
            0,
            0,
            0,
            outcome,
            null);
        child.Process.StandardOutputSource.Enqueue(
            ApplicationCompositionWorkerProtocol.ProcessingFrame(
                child.Descriptor,
                new RunStarted(request, SessionTestSupport.Start),
                2));
        child.Process.StandardOutputSource.Enqueue(
            ApplicationCompositionWorkerProtocol.ProcessingFrame(
                child.Descriptor,
                new EligibilityDetermined(request, 0),
                3));
        child.Process.StandardOutputSource.Enqueue(
            ApplicationCompositionWorkerProtocol.ProcessingFrame(
                child.Descriptor,
                new RunFinished(request, result),
                4));
        child.Process.Exit(exitCode);
    }

    private static async Task SendReadyAsync(SimulatedChild child)
    {
        if (child.Input.WriteCalls != 0)
        {
            return;
        }

        child.Process.StandardOutputSource.Enqueue(
            ApplicationCompositionWorkerProtocol.ReadyFrame(child.Descriptor));
        await child.Input.FirstWrite.WaitAsync(Bound);
    }

    private static void AssertPrivateWorkerCommand(
        ChildProcessStartDescriptor descriptor,
        ValidRuntimeSource runtimeSource)
    {
        Assert.AreEqual(runtimeSource.ProcessPath, descriptor.ExecutablePath, "child-uses-observed-runtime-host");
        Assert.AreEqual(runtimeSource.AssemblyPath, descriptor.Arguments[0], "child-uses-the-same-observed-Web-assembly");
        Assert.IsTrue(
            descriptor.Arguments.Contains(ApplicationRoleSelector.InternalWorkerSelector),
            "private-worker-selector");
        Assert.IsFalse(
            descriptor.Arguments.Any(argument => argument is "standard" or "web-only" or "run-once"),
            "child-command-does-not-select-a-public-mode");
        Assert.AreEqual(
            InternalWorkerProtocolVersion.V2,
            ApplicationCompositionWorkerProtocol.SelectedVersion(descriptor),
            "production-child-selects-v2");
    }

    private static void AssertStandardDescriptor(
        IServiceCollection services,
        Type serviceType,
        ServiceLifetime lifetime)
    {
        ServiceDescriptor[] descriptors = services.Where(descriptor => descriptor.ServiceType == serviceType).ToArray();
        Assert.HasCount(1, descriptors, serviceType.Name);
        Assert.AreEqual(lifetime, descriptors[0].Lifetime, serviceType.Name);
    }

    private sealed class StandardHostFixture : IAsyncDisposable, IDisposable
    {
        private bool _disposed;

        private StandardHostFixture(
            string root,
            WebApplicationBuilder builder,
            IReadOnlyList<string> environmentReads,
            RecordingServer? server,
            SchedulerInitializationBoundary? schedulerInitialization,
            QueueScheduledDetector? detector,
            ChildBackendResolutionBoundary? childBackendResolution,
            GatedRuntimeFactsCapture? runtimeCapture,
            ValidRuntimeSource? runtimeSource,
            SimulatedProcessFactory? processFactory,
            WebApplication? application)
        {
            Root = root;
            Builder = builder;
            EnvironmentReads = environmentReads;
            Server = server!;
            SchedulerInitialization = schedulerInitialization!;
            Detector = detector!;
            ChildBackendResolution = childBackendResolution!;
            RuntimeCapture = runtimeCapture!;
            RuntimeSource = runtimeSource!;
            ProcessFactory = processFactory!;
            Application = application!;
        }

        internal string Root { get; }
        internal string DataDirectory => Path.Combine(Root, "data");
        internal string ConfigDirectory => Path.Combine(Root, "config");
        internal WebApplicationBuilder Builder { get; }
        internal IReadOnlyList<string> EnvironmentReads { get; }
        internal RecordingServer Server { get; }
        internal SchedulerInitializationBoundary SchedulerInitialization { get; }
        internal QueueScheduledDetector Detector { get; }
        internal ChildBackendResolutionBoundary ChildBackendResolution { get; }
        internal GatedRuntimeFactsCapture RuntimeCapture { get; }
        internal ValidRuntimeSource RuntimeSource { get; }
        internal SimulatedProcessFactory ProcessFactory { get; }
        internal WebApplication Application { get; }

        internal static StandardHostFixture CreateBuilderOnly(
            bool includeRuntimeFiles,
            DeploymentMode? deploymentMode = null)
        {
            return CreateCore(includeRuntimeFiles, later: null, build: false, observer: null, deploymentMode ?? DeploymentMode.Standard);
        }

        internal static StandardHostFixture Create(
            bool includeRuntimeFiles,
            LaterHostedService? later = null,
            IProcessingRunCoordinatorObserver? observer = null)
        {
            return CreateCore(includeRuntimeFiles, later, build: true, observer, DeploymentMode.Standard);
        }

        private static StandardHostFixture CreateCore(
            bool includeRuntimeFiles,
            LaterHostedService? later,
            bool build,
            IProcessingRunCoordinatorObserver? observer,
            DeploymentMode deploymentMode)
        {
            string root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-change41-standard", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var reads = new List<string>();
            try
            {
                var builder = StandardWebApplication.CreateBuilder(
                    deploymentMode,
                    new[]
                    {
                        "--environment=Development",
                        $"--contentRoot={root}",
                        $"--applicationName={typeof(StandardWebApplication).Assembly.GetName().Name}"
                    },
                    name =>
                    {
                        reads.Add(name);
                        return name switch
                        {
                            "DATA_DIR" => Path.Combine(root, "data"),
                            "CONFIG_DIR" => Path.Combine(root, "config"),
                            _ => throw new AssertFailedException("Unexpected environment read: " + name)
                        };
                    });
                if (!build)
                {
                    return new StandardHostFixture(root, builder, reads, null, null, null, null, null, null, null, null);
                }

                var server = new RecordingServer();
                var schedulerInitialization = new SchedulerInitializationBoundary();
                var detector = new QueueScheduledDetector();
                var processFactory = new SimulatedProcessFactory();
                var childBackendResolution = new ChildBackendResolutionBoundary();
                var clock = new CancellationTestClock(SessionTestSupport.Start);
                var runtimeSource = ValidRuntimeSource.Create(root, includeRuntimeFiles);
                builder.Services.RemoveAll<IServer>();
                builder.Services.AddSingleton<IServer>(server);
                builder.Services.RemoveAll<IWorkerCommandRuntimeObservationSource>();
                builder.Services.AddSingleton<IWorkerCommandRuntimeObservationSource>(runtimeSource);
                builder.Services.RemoveAll<IWorkerCommandRuntimeFactsCapture>();
                var runtimeCapture = new GatedRuntimeFactsCapture(
                    new WorkerCommandRuntimeFactsCapture(runtimeSource));
                builder.Services.AddSingleton<IWorkerCommandRuntimeFactsCapture>(runtimeCapture);
                builder.Services.RemoveAll<IScheduledRunWorkGate>();
                builder.Services.AddSingleton<IScheduledRunWorkGate>(detector);
                builder.Services.RemoveAll<IChildProcessFactory>();
                builder.Services.AddSingleton<IChildProcessFactory>(processFactory);
                ServiceDescriptor backend = builder.Services.Single(
                    descriptor => descriptor.ServiceType == typeof(IChildProcessingRunBackend));
                builder.Services.Remove(backend);
                builder.Services.AddScoped(sp => childBackendResolution.Resolve(backend, sp));
                builder.Services.RemoveAll<TimeProvider>();
                builder.Services.AddSingleton<TimeProvider>(clock);
                builder.Services.RemoveAll<ProcessingBackgroundService>();
                builder.Services.AddSingleton(sp => new ProcessingBackgroundService(
                    sp.GetRequiredService<ILogger<ProcessingBackgroundService>>(),
                    sp.GetRequiredService<ProcessingState>(),
                    sp.GetRequiredService<IProcessingScheduleConfiguration>(),
                    schedulerInitialization.InitializeAsync,
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<IScheduledRunTrigger>()));
                if (later is not null)
                {
                    builder.Services.AddSingleton<IHostedService>(later);
                }
                if (observer is not null)
                {
                    builder.Services.AddSingleton<IProcessingRunCoordinatorObserver>(observer);
                }

                WebApplication application = WebApplicationComposition.Build(builder);
                return new StandardHostFixture(
                    root,
                    builder,
                    reads,
                    server,
                    schedulerInitialization,
                    detector,
                    childBackendResolution,
                    runtimeCapture,
                    runtimeSource,
                    processFactory,
                    application);
            }
            catch
            {
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (Application is not null)
            {
                ProcessFactory.ExitAll();
                await Application.DisposeAsync();
            }

            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class RecordingServer : IServer
    {
        private int _startCalls;
        private int _stopCalls;

        internal int StartCalls => Volatile.Read(ref _startCalls);
        internal int StopCalls => Volatile.Read(ref _stopCalls);
        public IFeatureCollection Features { get; } = CreateFeatures();

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull
        {
            ArgumentNullException.ThrowIfNull(application);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _startCalls);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stopCalls);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }

        private static IFeatureCollection CreateFeatures()
        {
            var features = new FeatureCollection();
            features.Set<IServerAddressesFeature>(new ServerAddressesFeature());
            return features;
        }
    }

    private sealed class SchedulerInitializationBoundary
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task InitializeAsync()
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class QueueScheduledDetector : IScheduledRunWorkGate
    {
        private readonly Queue<bool> _outcomes = new();
        private int _callCount;
        internal int CallCount => Volatile.Read(ref _callCount);

        internal void Enqueue(bool outcome)
        {
            lock (_outcomes)
            {
                _outcomes.Enqueue(outcome);
            }
        }

        public Task<bool> HasWorkAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            lock (_outcomes)
            {
                return Task.FromResult(_outcomes.Count > 0 && _outcomes.Dequeue());
            }
        }
    }

    private sealed class SimulatedProcessFactory : IChildProcessFactory
    {
        private readonly SemaphoreSlim _available = new(0);
        private readonly List<SimulatedChild> _children = [];
        private int _startCalls;

        internal int StartCalls => Volatile.Read(ref _startCalls);

        public ValueTask<IChildProcess?> StartAsync(
            ChildProcessStartDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _startCalls);
            var input = new SessionInputStream();
            var process = new SessionTestProcess(input, ChildProcessKillOutcome.Requested, exitOnKill: true);
            var child = new SimulatedChild(descriptor, input, process);
            lock (_children)
            {
                _children.Add(child);
            }

            _available.Release();
            return ValueTask.FromResult<IChildProcess?>(process);
        }

        internal async Task<SimulatedChild> NextAsync()
        {
            await _available.WaitAsync().ConfigureAwait(false);
            lock (_children)
            {
                return _children.Last(child => !child.Claimed).Claim();
            }
        }

        internal void ExitAll()
        {
            lock (_children)
            {
                foreach (SimulatedChild child in _children)
                {
                    child.Process.Exit(143);
                }
            }
        }
    }

    private sealed class ChildBackendResolutionBoundary
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);

        internal IChildProcessingRunBackend Resolve(ServiceDescriptor descriptor, IServiceProvider services)
        {
            Interlocked.Increment(ref _calls);
            return descriptor.ImplementationFactory?.Invoke(services) as IChildProcessingRunBackend
                ?? throw new InvalidOperationException("The production child backend descriptor did not retain its factory.");
        }
    }

    private sealed class GatedRuntimeFactsCapture(IWorkerCommandRuntimeFactsCapture inner)
        : IWorkerCommandRuntimeFactsCapture
    {
        private int _armNext;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ArmNext()
        {
            Volatile.Write(ref _armNext, 1);
        }

        public WorkerCommandRuntimeFacts Capture()
        {
            if (Interlocked.Exchange(ref _armNext, 0) != 0)
            {
                Entered.TrySetResult();
                Release.Task.GetAwaiter().GetResult();
            }

            return inner.Capture();
        }
    }

    private sealed class HostDispatchClaimGate : IProcessingRunCoordinatorObserver
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask BeforeChildDispatchClaimAsync(ProcessingRunRequest request)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }

        public void BeforeRequestCancellation(ProcessingRunRequest request)
        {
            CancellationObserved.TrySetResult();
        }
    }

    private sealed class SimulatedChild(
        ChildProcessStartDescriptor descriptor,
        SessionInputStream input,
        SessionTestProcess process)
    {
        internal ChildProcessStartDescriptor Descriptor { get; } = descriptor;
        internal SessionInputStream Input { get; } = input;
        internal SessionTestProcess Process { get; } = process;
        internal bool Claimed { get; private set; }

        internal SimulatedChild Claim()
        {
            Claimed = true;
            return this;
        }
    }

    private sealed class ValidRuntimeSource : IWorkerCommandRuntimeObservationSource
    {
        private const string DotnetHostPath = "/test-runtime/dotnet";
        private readonly string _root;
        private readonly string _assemblyPath;

        private ValidRuntimeSource(string root, string assemblyPath)
        {
            _root = root;
            _assemblyPath = assemblyPath;
        }

        internal string ProcessPath => DotnetHostPath;
        internal string AssemblyPath => _assemblyPath;

        internal static ValidRuntimeSource Create(string root, bool includeRuntimeFiles)
        {
            var runtimeRoot = Path.Combine(root, "runtime");
            Directory.CreateDirectory(runtimeRoot);
            var assemblyPath = Path.Combine(runtimeRoot, "ImmichReverseGeo.Web.dll");
            File.WriteAllText(assemblyPath, string.Empty);
            if (includeRuntimeFiles)
            {
                File.WriteAllText(Path.ChangeExtension(assemblyPath, ".runtimeconfig.json"), "{}");
                File.WriteAllText(Path.ChangeExtension(assemblyPath, ".deps.json"), "{}");
            }

            return new ValidRuntimeSource(runtimeRoot, assemblyPath);
        }

        public string? GetProcessPath() => DotnetHostPath;
        public WorkerCommandEntryAssemblyObservation GetEntryAssembly()
            => new("ImmichReverseGeo.Web", _assemblyPath);
        public string GetCurrentDirectory() => _root;
        public bool IsWindows() => false;
        public WorkerTargetObservation ObserveTarget(string path)
            => path == DotnetHostPath
                ? WorkerTargetObservation.File
                : Directory.Exists(path)
                ? WorkerTargetObservation.Directory
                : File.Exists(path)
                    ? WorkerTargetObservation.File
                    : WorkerTargetObservation.Missing;
    }

    private sealed class LaterHostedService : IHostedService
    {
        private int _startCalls;
        internal int StartCalls => Volatile.Read(ref _startCalls);

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCalls);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
