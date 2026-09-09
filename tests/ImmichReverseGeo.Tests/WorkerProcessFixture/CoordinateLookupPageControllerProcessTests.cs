using System.Collections.Concurrent;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkerInvocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.WorkerProcessFixture;

[TestClass]
[TestCategory("Change49")]
public sealed class CoordinateLookupPageControllerProcessTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ActualWorker_ProductionWebCompositionLeavesActiveProcessingStateAndAssetWritesUntouched(
        bool webOnly)
    {
        await using var fixture = new WorkerProcessFixtureLease();
        var events = new RecordingSink();
        await using var composition = ComposedLookupFixture.Create(
            fixture,
            "real-coordinate-success",
            events,
            webOnly);
        ProcessingState processing = composition.ProcessingState;
        processing.StartRun(9);
        processing.IncrementProcessed();
        processing.SetActivity("Processing an Immich asset");
        ProcessingSnapshot before = Snapshot(processing);

        await using CoordinateLookupPageController controller = composition.Controller;
        await controller.SubmitAsync(Submission()).WaitAsync(WorkerProcessFixtureLease.Watchdog);

        Assert.AreEqual(
            CoordinateLookupPagePhase.Completed,
            controller.State.Phase,
            composition.LaunchFailure?.ToString() ?? controller.State.Error);
        Assert.IsTrue(Guid.TryParseExact(controller.State.JobId, "D", out Guid jobId));
        Assert.AreEqual(controller.State.JobId, jobId.ToString("D"));
        Assert.IsTrue(events.Events
            .Where(static message => message.JobId is not null)
            .All(message => message.JobId == jobId));
        Assert.AreEqual("Fixture Airport", controller.State.Result!.FinalLocation.City!.Value);
        Assert.IsTrue(events.Events.Any(static message =>
            message.Payload is CoordinateLookupProgressPayload));
        Assert.AreEqual(before, Snapshot(processing));
        Assert.AreEqual(0, composition.ForbiddenResolutions);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "persistence-accessed.marker")));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Root, "source-called.marker")));
    }

    [TestMethod]
    public async Task ActualWorker_CompletedSourceDegradationRetainsIndependentResult()
    {
        await using var fixture = new WorkerProcessFixtureLease();
        var events = new RecordingSink();
        await using CoordinateLookupPageController controller = CreateController(
            fixture,
            "real-coordinate-degraded",
            events);

        await controller.SubmitAsync(Submission()).WaitAsync(WorkerProcessFixtureLease.Watchdog);

        Assert.AreEqual(CoordinateLookupPagePhase.Completed, controller.State.Phase);
        CoordinateLookupResult result = controller.State.Result!;
        Assert.AreEqual(CoordinateLookupSourceState.Unavailable, result.OvertureDivisions.State);
        Assert.IsNotNull(result.OvertureDivisions.Error);
        Assert.AreEqual("United States", result.FinalLocation.Country!.Value);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "persistence-accessed.marker")));
        Assert.AreEqual(1, events.Events.Count(static message =>
            message.Payload is WorkerJobTerminalPayload));
    }

    [TestMethod]
    public async Task ActualWorker_NoTerminalOutputFaultUsesClosedFailureAndReleasesAdmission()
    {
        await using var fixture = new WorkerProcessFixtureLease();
        var events = new RecordingSink();
        await using var admission = new TemporaryCoordinateLookupAdmissionGate();
        await using var controller = CreateController(
            fixture,
            "real-coordinate-output-failure",
            events,
            admission);

        await controller.SubmitAsync(Submission()).WaitAsync(WorkerProcessFixtureLease.Watchdog);

        Assert.AreEqual(CoordinateLookupPagePhase.Failed, controller.State.Phase);
        Assert.IsNull(controller.State.Result);
        StringAssert.Contains(controller.State.Error!, "lookup-inconsistentexit");
        Assert.AreEqual(0, events.Events.Count(static message =>
            message.Payload is WorkerJobTerminalPayload));
        Assert.AreEqual(
            "coordinate-job-started",
            File.ReadAllText(Path.Combine(fixture.Root, "output-fault-injected.marker")));
        Assert.AreEqual(
            "reached",
            File.ReadAllText(Path.Combine(
                fixture.Root,
                "input-post-execute-read-pending.marker")));
        Assert.AreEqual(
            "reached",
            File.ReadAllText(Path.Combine(
                fixture.Root,
                "input-post-execute-read-cancelled.marker")));
        Assert.AreEqual(
            "reached",
            File.ReadAllText(Path.Combine(
                fixture.Root,
                "input-post-execute-read-finished.marker")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "persistence-accessed.marker")));

        var reuse = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(
            admission.TryAdmit(new CoordinateLookupWorkerJobDispatch(
                Guid.NewGuid(),
                Request())));
        await reuse.Lease.DisposeAsync();
    }

    [TestMethod]
    public async Task ActualWorker_StartupFailureUsesClosedFailureAndStartsNoFallback()
    {
        await using var fixture = new WorkerProcessFixtureLease();
        var events = new RecordingSink();
        await using var admission = new TemporaryCoordinateLookupAdmissionGate();
        await using var controller = CreateController(
            fixture,
            "real-coordinate-startup-failure",
            events,
            admission);

        await controller.SubmitAsync(Submission()).WaitAsync(WorkerProcessFixtureLease.Watchdog);

        Assert.AreEqual(CoordinateLookupPagePhase.Failed, controller.State.Phase);
        Assert.IsNull(controller.State.Result);
        StringAssert.StartsWith(
            controller.State.Error!,
            "The lookup worker did not start successfully. (lookup-");
        Assert.AreEqual(0, events.Events.Count(static message =>
            message.Payload is WorkerJobTerminalPayload));
        Assert.AreEqual(
            "reached",
            File.ReadAllText(Path.Combine(
                fixture.Root,
                "startup-fault-injected.marker")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "source-called.marker")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "persistence-accessed.marker")));

        var reuse = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(
            admission.TryAdmit(new CoordinateLookupWorkerJobDispatch(
                Guid.NewGuid(),
                Request())));
        await reuse.Lease.DisposeAsync();
    }

    [TestMethod]
    public async Task ActualWorker_CancelTargetsActiveIdentityAndEndsAuthoritativelyCancelled()
    {
        await using var fixture = new WorkerProcessFixtureLease();
        var events = new RecordingSink();
        await using var composition = ComposedLookupFixture.Create(
            fixture,
            "real-coordinate-cancellation",
            events,
            webOnly: false);
        ProcessingState processing = composition.ProcessingState;
        processing.StartRun(9);
        processing.IncrementProcessed();
        processing.SetActivity("Processing an Immich asset");
        ProcessingSnapshot before = Snapshot(processing);
        await using CoordinateLookupPageController controller = composition.Controller;
        Task run = controller.SubmitAsync(Submission());

        await events.ActivityStarted.WaitAsync(WorkerProcessFixtureLease.Watchdog);
        Assert.IsTrue(controller.State.CanCancel);
        Task cancel = controller.CancelAsync();
        Assert.AreEqual(CoordinateLookupPagePhase.CancelRequested, controller.State.Phase);
        Assert.IsFalse(controller.State.CanCancel);
        await Task.WhenAll(run, cancel).WaitAsync(WorkerProcessFixtureLease.Watchdog);

        Assert.AreEqual(CoordinateLookupPagePhase.Cancelled, controller.State.Phase);
        Assert.IsTrue(Guid.TryParseExact(controller.State.JobId, "D", out Guid jobId));
        Assert.AreEqual(controller.State.JobId, jobId.ToString("D"));
        Assert.AreEqual(1, events.Events.Count(static message =>
            message.Payload is WorkerJobTerminalPayload terminal
                && terminal.Outcome == WorkerJobTerminalOutcome.Cancelled));
        Assert.IsTrue(events.Events.Any(static message =>
            message.Payload is CoordinateLookupProgressPayload));
        Assert.IsTrue(events.Events.Any(static message =>
            message.Payload is WorkerJobActivityStartedPayload));
        Assert.AreEqual(before, Snapshot(processing));
        Assert.AreEqual(0, composition.ForbiddenResolutions);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "persistence-accessed.marker")));
    }

    private static CoordinateLookupPageController CreateController(
        WorkerProcessFixtureLease fixture,
        string scenario,
        RecordingSink events,
        IWorkerJobAdmissionGate? admission = null)
    {
        admission ??= new TemporaryCoordinateLookupAdmissionGate();
        var client = new CoordinateLookupWorkerClient(
            new V2InvocationBuilder(),
            new FixtureLauncher(fixture, scenario, events),
            TimeProvider.System);
        return new CoordinateLookupPageController(
            admission,
            client,
            new SettingsProvider(),
            () => fixture.Request.RunId,
            () => { });
    }

    private static CoordinateLookupSubmission Submission() =>
        new(47.6062, -122.3321, true, true, true);

    private static CoordinateLookupRequest Request() =>
        new(
            47.6062,
            -122.3321,
            true,
            true,
            true,
            new CoordinateLookupCityResolverOverrides(null, []));

    private static ProcessingSnapshot Snapshot(ProcessingState state) =>
        new(
            state.IsRunning,
            state.TotalUnprocessed,
            state.ProcessedThisRun,
            state.ErrorsThisRun,
            state.SkippedThisRun,
            state.CurrentActivity,
            state.LastError,
            string.Join("\n", state.GetRecentLog()));

    private sealed record ProcessingSnapshot(
        bool IsRunning,
        long TotalUnprocessed,
        long Processed,
        long Errors,
        long Skipped,
        string? Activity,
        string? LastError,
        string Log);

    private sealed class SettingsProvider : ICoordinateLookupSettingsSnapshotProvider
    {
        public ValueTask<CoordinateLookupCityResolverOverrides> GetAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new CoordinateLookupCityResolverOverrides(null, []));
        }
    }

    private sealed class V2InvocationBuilder : IWorkerCommandInvocationBuilder
    {
        public WorkerCommandInvocationResolution Build() => Build(
            InternalWorkerProtocolVersion.V2);

        public WorkerCommandInvocationResolution Build(
            InternalWorkerProtocolVersion protocolVersion)
        {
            var facts = new WorkerCommandRuntimeFacts(
                WorkerInvocation.TrustedWebAssemblyIdentity,
                "/fixture/dotnet",
                WorkerTargetObservation.File,
                WorkerInvocation.TrustedWebAssemblyIdentity,
                "/fixture/ImmichReverseGeo.Web.dll",
                WorkerTargetObservation.File,
                "/fixture",
                WorkerTargetObservation.Directory,
                WorkerPathSemantics.Unix);
            return WorkerInvocation.Resolve(facts, protocolVersion);
        }
    }

    private sealed class FixtureLauncher(
        WorkerProcessFixtureLease fixture,
        string scenario,
        RecordingSink recording) : IChildWorkerLauncher
    {
        internal Exception? Failure { get; private set; }

        public async ValueTask<ChildWorkerLaunchResult> LaunchAsync(
            WorkerInvocation invocation,
            WorkerJobDispatch dispatch,
            IWorkerJobEventSink eventSink,
            ChildWorkerLauncherOptions options,
            CancellationToken cancellationToken)
        {
            try
            {
                ChildWorkerSession session = await fixture.LaunchCoordinateAsync(
                    scenario,
                    Assert.IsInstanceOfType<CoordinateLookupWorkerJobDispatch>(dispatch),
                    new ForwardingSink(eventSink, recording),
                    capture: true);
                return new ChildWorkerLaunchResult.Started(session);
            }
            catch (Exception exception)
            {
                Failure = exception;
                throw;
            }
        }
    }

    private sealed class ForwardingSink(
        IWorkerJobEventSink controller,
        IWorkerJobEventSink recording) : IWorkerJobEventSink
    {
        public async ValueTask AcceptAsync(
            WorkerJobOutputMessage message,
            CancellationToken cancellationToken)
        {
            await controller.AcceptAsync(message, cancellationToken);
            await recording.AcceptAsync(message, cancellationToken);
        }
    }

    private sealed class RecordingSink : IWorkerJobEventSink
    {
        private readonly ConcurrentQueue<WorkerJobOutputMessage> _events = new();
        private readonly TaskCompletionSource _activityStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal WorkerJobOutputMessage[] Events => _events.ToArray();
        internal Task ActivityStarted => _activityStarted.Task;

        public ValueTask AcceptAsync(
            WorkerJobOutputMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Enqueue(message);
            if (message.Payload is WorkerJobActivityStartedPayload)
            {
                _activityStarted.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ComposedLookupFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ForbiddenResolutionSentinel _forbidden;
        private readonly FixtureLauncher _launcher;

        private ComposedLookupFixture(
            ServiceProvider provider,
            CoordinateLookupPageController controller,
            ForbiddenResolutionSentinel forbidden,
            FixtureLauncher launcher)
        {
            _provider = provider;
            Controller = controller;
            _forbidden = forbidden;
            _launcher = launcher;
            ProcessingState = provider.GetRequiredService<ProcessingState>();
        }

        internal CoordinateLookupPageController Controller { get; }
        internal ProcessingState ProcessingState { get; }
        internal int ForbiddenResolutions => _forbidden.Count;
        internal Exception? LaunchFailure => _launcher.Failure;

        internal static ComposedLookupFixture Create(
            WorkerProcessFixtureLease process,
            string scenario,
            RecordingSink events,
            bool webOnly)
        {
            string root = Path.Combine(process.Root, webOnly ? "web-only" : "standard");
            Directory.CreateDirectory(root);
            var services = new ServiceCollection();
            var context = ApplicationCompositionContext.Create(
                CompositionEnvironment.Development,
                root,
                Path.Combine(root, "data"),
                Path.Combine(root, "config"),
                webOnly ? DeploymentMode.WebOnly : DeploymentMode.Standard);
            if (webOnly)
            {
                services.AddWebOnlyWebComposition(context);
            }
            else
            {
                services.AddStandardWebComposition(context);
            }

            var forbidden = new ForbiddenResolutionSentinel();
            ReplaceForbidden<IProcessingAssetRepository>(services, forbidden);
            ReplaceForbidden<ImmichDbRepository>(services, forbidden);
            ReplaceForbidden<OvertureDivisionsService>(services, forbidden);
            ReplaceForbidden<OvertureDivisionCacheService>(services, forbidden);
            ReplaceForbidden<OverturePlacesService>(services, forbidden);
            ReplaceForbidden<GadmDivisionsService>(services, forbidden);
            ReplaceForbidden<GadmDivisionCacheService>(services, forbidden);

            var launcher = new FixtureLauncher(process, scenario, events);
            services.RemoveAll<IChildWorkerLauncher>();
            services.AddSingleton<IChildWorkerLauncher>(launcher);
            services.RemoveAll<IWorkerCommandRuntimeObservationSource>();
            services.AddSingleton<IWorkerCommandRuntimeObservationSource>(
                new FixtureRuntimeObservationSource());
            services.RemoveAll<ICoordinateLookupSettingsSnapshotProvider>();
            services.AddSingleton<ICoordinateLookupSettingsSnapshotProvider>(
                new SettingsProvider());

            ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
            Assert.IsInstanceOfType<CoordinateLookupWorkerClient>(
                provider.GetRequiredService<ICoordinateLookupWorkerClient>());
            CoordinateLookupPageController controller = provider
                .GetRequiredService<CoordinateLookupPageControllerFactory>()
                .Create(() => { });
            return new ComposedLookupFixture(provider, controller, forbidden, launcher);
        }

        public async ValueTask DisposeAsync()
        {
            await Controller.DisposeAsync();
            await _provider.DisposeAsync();
        }

        private static void ReplaceForbidden<T>(
            IServiceCollection services,
            ForbiddenResolutionSentinel forbidden)
            where T : class
        {
            services.RemoveAll<T>();
            services.AddSingleton<T>(_ => forbidden.Resolve<T>());
        }
    }

    private sealed class ForbiddenResolutionSentinel
    {
        private int _count;
        internal int Count => Volatile.Read(ref _count);

        internal T Resolve<T>() where T : class
        {
            Interlocked.Increment(ref _count);
            throw new InvalidOperationException(
                $"Lookup resolved forbidden Web-host dependency {typeof(T).Name}.");
        }
    }

    private sealed class FixtureRuntimeObservationSource :
        IWorkerCommandRuntimeObservationSource
    {
        public string GetProcessPath() => "/fixture/dotnet";

        public WorkerCommandEntryAssemblyObservation GetEntryAssembly() =>
            new(
                WorkerInvocation.TrustedWebAssemblyIdentity,
                "/fixture/ImmichReverseGeo.Web.dll");

        public string GetCurrentDirectory() => "/fixture";

        public bool IsWindows() => false;

        public WorkerTargetObservation ObserveTarget(string path) =>
            path == "/fixture"
                ? WorkerTargetObservation.Directory
                : WorkerTargetObservation.File;
    }
}
