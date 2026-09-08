using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.ApplicationRole;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
[TestCategory("Change42")]
public sealed class WebOnlyDeploymentModeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task ExactSelection_ComposesTheSharedWebAndManualGraphWithoutAutomaticScheduling()
    {
        WebOnlyHostFixture? fixture = null;
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
                    return "web-only";
                },
                errors,
                (mode, _) => fixture = WebOnlyHostFixture.CreateBuilderOnly(mode),
                _ => Assert.Fail("internal-worker continuation"),
                (_, _) => Assert.Fail("run-once continuation"),
                _ => Assert.Fail("private selection failure"),
                _ => Assert.Fail("deployment mode failure"));

            Assert.AreEqual(1, selectorReads, "mode-resolved-once");
            Assert.AreEqual(string.Empty, errors.ToString());
            Assert.IsNotNull(fixture);
            IServiceCollection services = fixture.Builder.Services;

            foreach (Type forbidden in new[]
            {
                typeof(ProcessingBackgroundService),
                typeof(IScheduledRunTrigger),
                typeof(IProcessingScheduleConfiguration),
                typeof(IScheduledRunWorkCounter),
                typeof(IScheduledRunWorkGate),
                typeof(IProcessingRunExecutor),
                typeof(ProcessingRunExecutor)
            })
            {
                Assert.AreEqual(0, services.Count(descriptor => descriptor.ServiceType == forbidden), forbidden.Name);
            }

            Assert.AreEqual(0, services.Count(descriptor => descriptor.ServiceType.Name == "InProcessProcessingRunBackend"));
            AssertDescriptor(services, typeof(ProcessingRunCoordinator), ServiceLifetime.Singleton);
            AssertDescriptor(services, typeof(IManualProcessingRunCoordinator), ServiceLifetime.Singleton);
            AssertDescriptor(services, typeof(IChildProcessingRunBackend), ServiceLifetime.Scoped);
            AssertDescriptor(services, typeof(IChildWorkerLauncher), ServiceLifetime.Singleton);
            AssertDescriptor(services, typeof(ChildWorkerStartupValidator), ServiceLifetime.Singleton);
            AssertDescriptor(services, typeof(IServer), ServiceLifetime.Singleton);
            Assert.IsTrue(services.Any(descriptor => descriptor.ServiceType == typeof(IPostConfigureOptions<RazorComponentsServiceOptions>)));
            Assert.IsTrue(services.Any(descriptor => descriptor.ServiceType == typeof(IConfigureOptions<CircuitOptions>)));

            foreach (Type currentPhaseDependency in new[]
            {
                typeof(ImmichDbRepository),
                typeof(SkippedAssetsRepository),
                typeof(OvertureDivisionsService),
                typeof(OverturePlacesService),
                typeof(GadmDivisionsService),
                typeof(ConfigService)
            })
            {
                AssertDescriptor(services, currentPhaseDependency, ServiceLifetime.Singleton);
            }

            var context = (ApplicationCompositionContext?)services
                .Single(descriptor => descriptor.ServiceType == typeof(ApplicationCompositionContext))
                .ImplementationInstance;
            Assert.IsNotNull(context);
            Assert.AreSame(DeploymentMode.WebOnly, context.DeploymentMode);
            CollectionAssert.AreEqual(new[] { "DATA_DIR", "CONFIG_DIR" }, fixture.EnvironmentReads.ToArray());

            var wrong = new ServiceCollection();
            var wrongContext = ApplicationCompositionContext.Create(
                CompositionEnvironment.Development,
                fixture.Root,
                fixture.DataDirectory,
                fixture.ConfigDirectory,
                DeploymentMode.Standard);
            Assert.ThrowsExactly<ArgumentException>(() => wrong.AddWebOnlyWebComposition(wrongContext));
            Assert.HasCount(0, wrong, "wrong-mode-does-not-partially-compose");
        }
        finally
        {
            if (fixture is not null)
            {
                await fixture.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task EnabledDueSchedule_StartsNoSchedulingWorkAndLeavesPersistedValuesUntouched()
    {
        var saved = new AppConfig
        {
            Schedule = new ScheduleConfig { Enabled = true, Cron = "* * * * *" }
        };
        await using var fixture = await WebOnlyHostFixture.CreateAsync(includeRuntimeFiles: true, saved);
        string before = await File.ReadAllTextAsync(fixture.SettingsPath);

        await fixture.Application.StartAsync().WaitAsync(Bound);

        AppConfig loaded = await fixture.Application.Services.GetRequiredService<ConfigService>().GetConfigAsync();
        Assert.IsTrue(loaded.Schedule.Enabled, "real-provider-loads-saved-enabled");
        Assert.AreEqual("* * * * *", loaded.Schedule.Cron, "real-provider-loads-saved-cron");
        Assert.AreEqual(0L, fixture.Clock.TimerGeneration, "web-only-creates-no-scheduler-timer");
        Assert.AreEqual(0, fixture.Clock.ActiveTimerCount, "web-only-has-no-active-scheduler-timer");
        fixture.Clock.Advance(TimeSpan.FromDays(2));

        Assert.AreEqual(1, fixture.Server.StartCalls, "web-server-accepts-after-validator");
        Assert.AreEqual(0, fixture.ProcessFactory.StartCalls, "due-schedule-starts-no-child");
        Assert.AreEqual(0, fixture.ChildBackendResolution.Calls, "due-schedule-resolves-no-child-backend");
        Assert.IsFalse(fixture.Application.Services.GetRequiredService<ProcessingState>().IsRunning, "no-scheduled-pending");
        Assert.IsFalse(fixture.Application.Services.GetRequiredService<ProcessingState>().RecentLog.Any(
            line => line.Contains("schedul", StringComparison.OrdinalIgnoreCase)), "no-schedule-status");
        Assert.AreEqual(before, await File.ReadAllTextAsync(fixture.SettingsPath), "startup-does-not-persist-or-normalize-schedule");
        AssertMappedRoutes(fixture.Application);

        var coordinator = fixture.Application.Services.GetRequiredService<ProcessingRunCoordinator>();
        var unsupported = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ((IScheduledRunTrigger)coordinator).TriggerScheduledAsync(CancellationToken.None));
        Assert.AreEqual("Scheduled processing is not available in this deployment mode.", unsupported.Message);
        Assert.IsFalse(fixture.Application.Services.GetRequiredService<ProcessingState>().IsRunning, "defensive-rejection-precedes-pending");
        Assert.AreEqual(0, fixture.ChildBackendResolution.Calls, "defensive-rejection-precedes-resolution");

        IHostedService[] hosted = fixture.Application.Services.GetServices<IHostedService>().ToArray();
        Assert.AreEqual(1, hosted.Count(service => ReferenceEquals(service, coordinator)), "one-lifecycle-owner");
        Assert.AreEqual(0, hosted.Count(service => service is ProcessingBackgroundService), "no-hosted-scheduler-alias");

        await fixture.Application.StopAsync().WaitAsync(Bound);
        Assert.AreEqual(0L, fixture.Clock.TimerGeneration, "advance-and-stop-create-no-scheduler-timer");
        Assert.AreEqual(0, fixture.Clock.ActiveTimerCount, "advance-and-stop-leave-no-scheduler-timer");
        Assert.AreEqual(1, fixture.Server.StopCalls);
        Assert.AreEqual(ProcessingRunAdmissionResult.Stopping, await coordinator.TriggerManualAsync());
        Assert.AreEqual(before, await File.ReadAllTextAsync(fixture.SettingsPath), "shutdown-does-not-persist-or-normalize-schedule");
    }

    [TestMethod]
    [DataRow(false, "not a cron")]
    [DataRow(true, "")]
    public async Task DisabledOrInvalidSchedule_StartsNoRetryLoopAndPreservesExactSettings(bool enabled, string cron)
    {
        var saved = new AppConfig
        {
            Schedule = new ScheduleConfig { Enabled = enabled, Cron = cron }
        };
        await using var fixture = await WebOnlyHostFixture.CreateAsync(includeRuntimeFiles: true, saved);
        string before = await File.ReadAllTextAsync(fixture.SettingsPath);

        await fixture.Application.StartAsync().WaitAsync(Bound);
        AppConfig loaded = await fixture.Application.Services.GetRequiredService<ConfigService>().GetConfigAsync();
        Assert.AreEqual(enabled, loaded.Schedule.Enabled, "real-provider-loads-saved-enabled");
        Assert.AreEqual(cron, loaded.Schedule.Cron, "real-provider-loads-saved-cron");
        Assert.AreEqual(0L, fixture.Clock.TimerGeneration, "web-only-creates-no-retry-timer");
        Assert.AreEqual(0, fixture.Clock.ActiveTimerCount, "web-only-has-no-retry-timer");
        fixture.Clock.Advance(TimeSpan.FromDays(2));
        await fixture.Application.StopAsync().WaitAsync(Bound);

        Assert.AreEqual(0, fixture.ProcessFactory.StartCalls);
        Assert.AreEqual(0, fixture.ChildBackendResolution.Calls);
        Assert.IsFalse(fixture.Application.Services.GetRequiredService<ProcessingState>().IsRunning);
        Assert.AreEqual(0L, fixture.Clock.TimerGeneration, "advance-and-stop-create-no-retry-timer");
        Assert.AreEqual(0, fixture.Clock.ActiveTimerCount, "advance-and-stop-leave-no-retry-timer");
        Assert.AreEqual(before, await File.ReadAllTextAsync(fixture.SettingsPath));
    }

    [TestMethod]
    public async Task ManualAdmission_UsesTheRealChildControlPlaneAndPreservesBusyAndFinalFailure()
    {
        await using var fixture = await WebOnlyHostFixture.CreateAsync(includeRuntimeFiles: true);
        await fixture.Application.StartAsync().WaitAsync(Bound);
        var coordinator = fixture.Application.Services.GetRequiredService<ProcessingRunCoordinator>();
        var manual = fixture.Application.Services.GetRequiredService<IManualProcessingRunCoordinator>();
        var state = fixture.Application.Services.GetRequiredService<ProcessingState>();

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await manual.TriggerManualAsync().WaitAsync(Bound));
        SimulatedChild first = await fixture.ProcessFactory.NextAsync().WaitAsync(Bound);
        Assert.AreEqual(1, fixture.ChildBackendResolution.Calls);
        ProcessingRunRequest firstRequest = coordinator.ActiveRequest!;
        Assert.AreEqual(ProcessingRunTrigger.Manual, firstRequest.Trigger);
        Assert.IsTrue(state.IsRunning, "manual-pending-visible");
        Assert.AreEqual(ProcessingRunAdmissionResult.AlreadyRunning, await manual.TriggerManualAsync().WaitAsync(Bound));
        Assert.AreEqual(1, fixture.ProcessFactory.StartCalls, "busy-does-not-launch-a-second-child");
        AssertPrivateWorkerCommand(first.Descriptor, fixture.RuntimeSource);
        await SendReadyAsync(first);
        Task activeState = ObserveStateAsync(
            state,
            () => state.LastRunStarted is not null && state.TotalUnprocessed == 3);
        first.Process.StandardOutputSource.Enqueue(
            ApplicationCompositionWorkerProtocol.ProcessingFrame(
                first.Descriptor,
                new RunStarted(firstRequest, SessionTestSupport.Start),
                2));
        first.Process.StandardOutputSource.Enqueue(
            ApplicationCompositionWorkerProtocol.ProcessingFrame(
                first.Descriptor,
                new EligibilityDetermined(firstRequest, 3),
                3));
        await activeState.WaitAsync(Bound);
        Assert.IsTrue(state.IsRunning, "manual-active-visible");
        Assert.AreEqual(3L, state.TotalUnprocessed, "manual-active-progress-visible");
        var completed = new ProcessingRunResult(
            firstRequest,
            SessionTestSupport.Start,
            SessionTestSupport.Start,
            3,
            3,
            0,
            0,
            ProcessingRunOutcome.Completed,
            null);
        first.Process.StandardOutputSource.Enqueue(
            ApplicationCompositionWorkerProtocol.ProcessingFrame(
                first.Descriptor,
                new ProgressChanged(firstRequest, new ProcessingProgress(1, 1, 0, 0)),
                4));
        first.Process.StandardOutputSource.Enqueue(
            ApplicationCompositionWorkerProtocol.ProcessingFrame(
                first.Descriptor,
                new ProgressChanged(firstRequest, new ProcessingProgress(2, 2, 0, 0)),
                5));
        first.Process.StandardOutputSource.Enqueue(
            ApplicationCompositionWorkerProtocol.ProcessingFrame(
                first.Descriptor,
                new ProgressChanged(firstRequest, new ProcessingProgress(3, 3, 0, 0)),
                6));
        first.Process.StandardOutputSource.Enqueue(
            ApplicationCompositionWorkerProtocol.ProcessingFrame(
                first.Descriptor,
                new RunFinished(firstRequest, completed),
                7));
        first.Process.Exit(0);
        await coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        Assert.IsFalse(state.IsRunning, "manual-terminal-visible");
        Assert.IsNotNull(state.LastRunCompleted, "manual-success-terminal-visible");
        Assert.IsNull(state.LastError, "manual-success-has-no-error: " + state.LastError);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await manual.TriggerManualAsync().WaitAsync(Bound));
        SimulatedChild failed = await fixture.ProcessFactory.NextAsync().WaitAsync(Bound);
        await SendReadyAsync(failed);
        failed.Process.Exit(5);
        await coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

        Assert.AreEqual(2, fixture.ProcessFactory.StartCalls, "failure-is-final-without-retry-or-replacement");
        Assert.AreEqual(2, fixture.ChildBackendResolution.Calls);
        Assert.IsNotNull(state.LastError);
        Assert.IsNull(fixture.Application.Services.GetService<IProcessingRunExecutor>());
    }

    [TestMethod]
    public async Task InvalidChildPrerequisite_FailsBeforeServerLaterServiceOrProcessingResolution()
    {
        var later = new LaterHostedService();
        await using var fixture = await WebOnlyHostFixture.CreateAsync(includeRuntimeFiles: false, later: later);

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => fixture.Application.StartAsync().WaitAsync(Bound));

        Assert.AreEqual(
            "The child worker runtime files are unavailable. Publish the complete Web application artifact and retry startup.",
            failure.Message);
        Assert.AreEqual(0, fixture.Server.StartCalls);
        Assert.AreEqual(0, later.StartCalls);
        Assert.AreEqual(0, fixture.ProcessFactory.StartCalls);
        Assert.AreEqual(0, fixture.ChildBackendResolution.Calls);
    }

    [TestMethod]
    public async Task ManualChildStartingDuringShutdown_RemainsOwnedWithoutSchedulerUntilPhysicalFinality()
    {
        await using var fixture = await WebOnlyHostFixture.CreateAsync(includeRuntimeFiles: true);
        await fixture.Application.StartAsync().WaitAsync(Bound);
        var coordinator = fixture.Application.Services.GetRequiredService<ProcessingRunCoordinator>();
        try
        {
            fixture.RuntimeCapture.ArmNext();
            Task<ProcessingRunAdmissionResult> manual = Task.Run(coordinator.TriggerManualAsync);
            await fixture.RuntimeCapture.Entered.Task.WaitAsync(Bound);
            ProcessingRunRequest ownedRequest = coordinator.ActiveRequest!;
            Task stopping = fixture.Application.StopAsync();
            Assert.AreEqual(ProcessingRunAdmissionResult.Stopping, await coordinator.TriggerManualAsync());
            Assert.AreEqual(1, fixture.ChildBackendResolution.Calls, "dispatch-claim-precedes-runtime-capture");
            Assert.AreEqual(0, fixture.ProcessFactory.StartCalls, "physical-start-remains-gated");

            fixture.RuntimeCapture.Release.TrySetResult();
            Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await manual.WaitAsync(Bound));
            SimulatedChild child = await fixture.ProcessFactory.NextAsync().WaitAsync(Bound);
            await SendReadyAsync(child);
            await child.Input.SecondFlush.WaitAsync(Bound);
            AssertExecuteAndCancel(child, ownedRequest);
            Assert.IsFalse(stopping.IsCompleted, "host-owns-start-racing-child-until-process-stream-finality");

            await CompleteAsync(coordinator.ActiveRequest!, child, ProcessingRunOutcome.Cancelled, 130);
            await stopping.WaitAsync(Bound);
            Assert.AreEqual(1, child.Process.DisposeCalls);
            Assert.AreEqual(1, child.Input.DisposeCalls, "shutdown-disposes-owned-input-once");
            Assert.AreEqual(1, child.Process.StandardOutputSource.DisposeCalls, "shutdown-disposes-owned-output-once");
            Assert.AreEqual(1, child.Process.StandardErrorSource.DisposeCalls, "shutdown-disposes-owned-error-once");
            Assert.AreEqual(1, fixture.ProcessFactory.StartCalls, "shutdown-launches-no-replacement");
            Assert.IsNull(coordinator.ActiveRequest);
            var state = fixture.Application.Services.GetRequiredService<ProcessingState>();
            Assert.IsFalse(state.IsRunning);
            Assert.IsNotNull(state.LastRunCompleted, "manual-cancellation-terminal-visible");
            Assert.IsNull(state.LastError, "manual-cancellation-is-not-reported-as-failure: " + state.LastError);
            Assert.AreEqual(1, fixture.Server.StopCalls);
        }
        finally
        {
            fixture.RuntimeCapture.Release.TrySetResult();
            fixture.ProcessFactory.ExitAll();
        }
    }

    private static void AssertMappedRoutes(WebApplication application)
    {
        string?[] routes = ((IEndpointRouteBuilder)application).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();
        foreach (string route in new[] { "/", "/settings", "/lookup", "/data", "/data/geoboundaries", "/data/reset-geo-data" })
        {
            CollectionAssert.Contains(routes, route, route);
        }
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

    private static Task ObserveStateAsync(ProcessingState state, Func<bool> condition)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? observer = null;
        observer = () =>
        {
            if (!condition())
            {
                return;
            }

            state.OnChanged -= observer;
            completion.TrySetResult();
        };
        state.OnChanged += observer;
        observer();
        return completion.Task;
    }

    private static void AssertPrivateWorkerCommand(
        ChildProcessStartDescriptor descriptor,
        ValidRuntimeSource runtimeSource)
    {
        Assert.AreEqual(runtimeSource.ProcessPath, descriptor.ExecutablePath);
        Assert.AreEqual(runtimeSource.AssemblyPath, descriptor.Arguments[0]);
        Assert.IsTrue(descriptor.Arguments.Contains(ApplicationRoleSelector.InternalWorkerSelector));
        Assert.IsFalse(descriptor.Arguments.Any(argument => argument is "standard" or "web-only" or "run-once"));
        Assert.AreEqual(
            InternalWorkerProtocolVersion.V2,
            ApplicationCompositionWorkerProtocol.SelectedVersion(descriptor),
            "production-child-selects-v2");
    }

    private static void AssertExecuteAndCancel(SimulatedChild child, ProcessingRunRequest ownedRequest)
    {
        IReadOnlyList<byte[]> frames = child.Input.Frames;
        Assert.HasCount(2, frames, "one-execute-and-one-cancel-frame");

        if (ApplicationCompositionWorkerProtocol.SelectedVersion(child.Descriptor)
            == InternalWorkerProtocolVersion.V2)
        {
            WorkerJobControllerParseResult executeV2 =
                WorkerJobProtocolCodec.ParseControllerInput(frames[0]);
            Assert.IsTrue(executeV2.IsSuccess, executeV2.Failure?.Diagnostic);
            Assert.AreEqual(WorkerJobProtocolV2.ExecuteType, executeV2.Message!.Type);
            Assert.AreEqual(ownedRequest.RunId, executeV2.Message.JobId);
            Assert.AreEqual(WorkerJobKind.ProcessAssets, executeV2.Message.JobKind);
            Assert.AreEqual(
                ownedRequest,
                Assert.IsInstanceOfType<ProcessAssetsExecutePayload>(
                    executeV2.Message.Payload).Request.ProcessingRequest);

            WorkerJobControllerParseResult cancelV2 =
                WorkerJobProtocolCodec.ParseControllerInput(frames[1]);
            Assert.IsTrue(cancelV2.IsSuccess, cancelV2.Failure?.Diagnostic);
            Assert.AreEqual(WorkerJobProtocolV2.CancelType, cancelV2.Message!.Type);
            Assert.AreEqual(ownedRequest.RunId, cancelV2.Message.JobId);
            Assert.AreEqual(WorkerJobKind.ProcessAssets, cancelV2.Message.JobKind);
            Assert.IsInstanceOfType<WorkerJobCancelPayload>(cancelV2.Message.Payload);
            return;
        }

        WorkerProtocolControllerParseResult execute = WorkerProtocolCodec.ParseControllerInput(frames[0]);
        Assert.IsTrue(execute.IsSuccess, execute.Failure?.Diagnostic);
        Assert.AreEqual(WorkerProtocolV1.ExecuteType, execute.Message!.Type);
        Assert.AreEqual(ownedRequest.RunId, execute.Message.RunId, "execute-correlates-the-owned-run");
        Assert.AreEqual(ownedRequest, ((ExecuteRequestPayload)execute.Message.Payload).Request, "execute-preserves-the-owned-request");

        WorkerProtocolControllerParseResult cancel = WorkerProtocolCodec.ParseControllerInput(frames[1]);
        Assert.IsTrue(cancel.IsSuccess, cancel.Failure?.Diagnostic);
        Assert.AreEqual(WorkerProtocolV1.CancelType, cancel.Message!.Type);
        Assert.AreEqual(ownedRequest.RunId, cancel.Message.RunId, "cancel-correlates-the-owned-run");
        Assert.IsInstanceOfType<CancelControlPayload>(cancel.Message.Payload, "cancel-uses-the-canonical-payload");
    }

    private static void AssertDescriptor(IServiceCollection services, Type type, ServiceLifetime lifetime)
    {
        ServiceDescriptor[] descriptors = services.Where(descriptor => descriptor.ServiceType == type).ToArray();
        Assert.HasCount(1, descriptors, type.Name);
        Assert.AreEqual(lifetime, descriptors[0].Lifetime, type.Name);
    }

    private sealed class WebOnlyHostFixture : IAsyncDisposable
    {
        private bool _disposed;

        private WebOnlyHostFixture(
            string root,
            WebApplicationBuilder builder,
            IReadOnlyList<string> environmentReads,
            RecordingServer? server,
            ChildBackendResolutionBoundary? childBackendResolution,
            GatedRuntimeFactsCapture? runtimeCapture,
            ValidRuntimeSource? runtimeSource,
            SimulatedProcessFactory? processFactory,
            CancellationTestClock? clock,
            WebApplication? application)
        {
            Root = root;
            Builder = builder;
            EnvironmentReads = environmentReads;
            Server = server!;
            ChildBackendResolution = childBackendResolution!;
            RuntimeCapture = runtimeCapture!;
            RuntimeSource = runtimeSource!;
            ProcessFactory = processFactory!;
            Clock = clock!;
            Application = application!;
        }

        internal string Root { get; }
        internal string DataDirectory => Path.Combine(Root, "data");
        internal string ConfigDirectory => Path.Combine(Root, "config");
        internal string SettingsPath => Path.Combine(ConfigDirectory, "settings.json");
        internal WebApplicationBuilder Builder { get; }
        internal IReadOnlyList<string> EnvironmentReads { get; }
        internal RecordingServer Server { get; }
        internal ChildBackendResolutionBoundary ChildBackendResolution { get; }
        internal GatedRuntimeFactsCapture RuntimeCapture { get; }
        internal ValidRuntimeSource RuntimeSource { get; }
        internal SimulatedProcessFactory ProcessFactory { get; }
        internal CancellationTestClock Clock { get; }
        internal WebApplication Application { get; }

        internal static WebOnlyHostFixture CreateBuilderOnly(DeploymentMode mode)
        {
            return CreateBuilderCore(mode, build: false, includeRuntimeFiles: true, later: null);
        }

        internal static async Task<WebOnlyHostFixture> CreateAsync(
            bool includeRuntimeFiles,
            AppConfig? initial = null,
            LaterHostedService? later = null)
        {
            string root = CreateRoot();
            if (initial is not null)
            {
                var config = new ConfigService(NullLogger<ConfigService>.Instance, Path.Combine(root, "config"));
                await config.SaveConfigAsync(initial);
            }

            return CreateBuilderCore(DeploymentMode.WebOnly, build: true, includeRuntimeFiles, later, root);
        }

        private static WebOnlyHostFixture CreateBuilderCore(
            DeploymentMode mode,
            bool build,
            bool includeRuntimeFiles,
            LaterHostedService? later,
            string? root = null)
        {
            root ??= CreateRoot();
            var reads = new List<string>();
            try
            {
                var builder = WebOnlyWebApplication.CreateBuilder(
                    mode,
                    new[]
                    {
                        "--environment=Development",
                        $"--contentRoot={root}",
                        $"--applicationName={typeof(WebOnlyWebApplication).Assembly.GetName().Name}"
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
                    return new WebOnlyHostFixture(root, builder, reads, null, null, null, null, null, null, null);
                }

                var server = new RecordingServer();
                var processFactory = new SimulatedProcessFactory();
                var childBackendResolution = new ChildBackendResolutionBoundary();
                var runtimeSource = ValidRuntimeSource.Create(root, includeRuntimeFiles);
                builder.Services.RemoveAll<IServer>();
                builder.Services.AddSingleton<IServer>(server);
                builder.Services.RemoveAll<IWorkerCommandRuntimeObservationSource>();
                builder.Services.AddSingleton<IWorkerCommandRuntimeObservationSource>(runtimeSource);
                builder.Services.RemoveAll<IWorkerCommandRuntimeFactsCapture>();
                var runtimeCapture = new GatedRuntimeFactsCapture(new WorkerCommandRuntimeFactsCapture(runtimeSource));
                builder.Services.AddSingleton<IWorkerCommandRuntimeFactsCapture>(runtimeCapture);
                builder.Services.RemoveAll<IChildProcessFactory>();
                builder.Services.AddSingleton<IChildProcessFactory>(processFactory);
                ServiceDescriptor backend = builder.Services.Single(
                    descriptor => descriptor.ServiceType == typeof(IChildProcessingRunBackend));
                builder.Services.Remove(backend);
                builder.Services.AddScoped(sp => childBackendResolution.Resolve(backend, sp));
                builder.Services.RemoveAll<TimeProvider>();
                var clock = new CancellationTestClock(SessionTestSupport.Start);
                builder.Services.AddSingleton<TimeProvider>(clock);
                if (later is not null)
                {
                    builder.Services.AddSingleton<IHostedService>(later);
                }

                WebApplication application = WebApplicationComposition.Build(builder);
                return new WebOnlyHostFixture(
                    root,
                    builder,
                    reads,
                    server,
                    childBackendResolution,
                    runtimeCapture,
                    runtimeSource,
                    processFactory,
                    clock,
                    application);
            }
            catch
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
                throw;
            }
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

        private static string CreateRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-change42-web-only", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
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
            string runtimeRoot = Path.Combine(root, "runtime");
            Directory.CreateDirectory(runtimeRoot);
            string assemblyPath = Path.Combine(runtimeRoot, "ImmichReverseGeo.Web.dll");
            File.WriteAllText(assemblyPath, string.Empty);
            if (includeRuntimeFiles)
            {
                File.WriteAllText(Path.ChangeExtension(assemblyPath, ".runtimeconfig.json"), "{}");
                File.WriteAllText(Path.ChangeExtension(assemblyPath, ".deps.json"), "{}");
            }

            return new ValidRuntimeSource(root, assemblyPath);
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
