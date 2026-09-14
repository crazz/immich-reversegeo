using System.Collections.Concurrent;
using System.Threading.Channels;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Tests.LifecycleTelemetry;
using ImmichReverseGeo.Tests.CrossProcessRunExclusion;
using ImmichReverseGeo.Tests.WorkerProcessFixture;
using ImmichReverseGeo.Tests.ApplicationComposition;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.LifecycleTelemetry;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using WorkerInvocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

/// <summary>
/// Real Web composition; only command selection and external child behavior are controlled.
/// Admission, projection, classification, cadence, cancellation and release stay production-owned.
/// </summary>
internal sealed class ProcessFailureMatrixHost : IAsyncDisposable
{
    private readonly IServiceProvider _provider;
    private readonly IHost? _host;
    private readonly object _observationGate = new();
    private bool _hadOwner;
    private int _releases;
    private int _forbiddenHeavyResolutions;

    private ProcessFailureMatrixHost(string fault, TimeProvider? time, InternalWorkerProtocolVersion protocolVersion, bool startHost,
        MatrixExecutionControl? control)
    {
        Root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-matrix", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(Root);
        Logs = new RecordingLifecycleLogs();
        Launcher = new MatrixLauncher(fault, Logs, control, Root);
        try
        {
            if (startHost)
            {
                _host = new HostBuilder().ConfigureServices(services => ConfigureServices(services, time, protocolVersion)).Build();
                _provider = _host.Services;
            }
            else
            {
                var services = new ServiceCollection();
                ConfigureServices(services, time, protocolVersion);
                _provider = services.BuildServiceProvider(validateScopes: true);
            }
        }
        catch
        {
            Logs.Dispose();
            Directory.Delete(Root, recursive: true);
            throw;
        }
    }

    private void ConfigureServices(IServiceCollection services, TimeProvider? time, InternalWorkerProtocolVersion protocolVersion)
    {
        services.AddWebComposition(ApplicationCompositionContext.Create(CompositionEnvironment.Development,
            Root, Path.Combine(Root, "data"), Path.Combine(Root, "config")));
        if (Launcher.IsMemorySoak)
        {
            var inspection = new WebBoundaryInspection();
            inspection.Inspect(services, []);
            Assert.IsEmpty(inspection.Failures, "Soak must start from the block-55/56 production Web boundary.");
        }
        services.RemoveAll<IWorkerCommandInvocationBuilder>();
        services.AddSingleton<IWorkerCommandInvocationBuilder>(new MatrixCommandBuilder(protocolVersion));
        services.RemoveAll<IChildWorkerLauncher>();
        services.AddSingleton<IChildWorkerLauncher>(Launcher);
        services.RemoveAll<TimeProvider>();
        services.AddSingleton(time ?? TimeProvider.System);
        services.RemoveAll<AdministrativeAreaResolverService>();
        services.AddSingleton<AdministrativeAreaResolverService>(_ => RejectHeavy<AdministrativeAreaResolverService>());
        services.RemoveAll<ImmichReverseGeo.Overture.Services.OvertureDivisionsService>();
        services.AddSingleton<ImmichReverseGeo.Overture.Services.OvertureDivisionsService>(_ =>
            RejectHeavy<ImmichReverseGeo.Overture.Services.OvertureDivisionsService>());
        services.RemoveAll<ImmichReverseGeo.Gadm.Services.GadmDivisionsService>();
        services.AddSingleton<ImmichReverseGeo.Gadm.Services.GadmDivisionsService>(_ =>
            RejectHeavy<ImmichReverseGeo.Gadm.Services.GadmDivisionsService>());
        if (Launcher.IsMemorySoak)
        {
            foreach (var boundary in ControlPlaneDependencyPolicy.HeavyTypes)
            {
                services.RemoveAll(boundary.Key);
                services.AddSingleton(boundary.Key, _ => SoakSentinel.Forbid<object>("production Web", boundary.Value));
            }
        }
    }

    internal static async Task<ProcessFailureMatrixHost> CreateAsync(string fault, TimeProvider? time = null,
        InternalWorkerProtocolVersion protocolVersion = InternalWorkerProtocolVersion.V2, bool startHost = false,
        MatrixExecutionControl? control = null)
    {
        var host = new ProcessFailureMatrixHost(fault, time, protocolVersion, startHost, control);
        try
        {
            host.Initialize();
            if (host._host is not null)
            {
                await MatrixWait.ForAsync(host._host.StartAsync(), "generic-host/start-all-production-hosted-services");
            }
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private void Initialize()
    {
        Coordinator = (WorkerJobCoordinator)_provider.GetRequiredService<IWorkerJobAdmissionGate>();
        Coordinator.Changed += ObserveOwner;
        Processing = _provider.GetRequiredService<ProcessingRunCoordinator>();
        State = _provider.GetRequiredService<ProcessingState>();
        Reporter = _provider.GetRequiredService<ProcessingStateEventReporter>();
        Lookup = _provider.GetRequiredService<CoordinateLookupPageControllerFactory>().Create(static () => { });
        Cache = _provider.GetRequiredService<CacheMutationPageControllerFactory>()
            .Create(static () => Task.CompletedTask, static () => Task.CompletedTask);
    }

    internal string Root { get; }
    internal RecordingLifecycleLogs Logs { get; }
    internal MatrixLauncher Launcher { get; }
    internal WorkerJobCoordinator Coordinator { get; private set; } = null!;
    internal ProcessingRunCoordinator Processing { get; private set; } = null!;
    internal ProcessingState State { get; private set; } = null!;
    internal ProcessingStateEventReporter Reporter { get; private set; } = null!;
    internal CoordinateLookupPageController Lookup { get; private set; } = null!;
    internal CacheMutationPageController Cache { get; private set; } = null!;
    internal int Releases => Volatile.Read(ref _releases);
    internal int ForbiddenHeavyResolutions => Volatile.Read(ref _forbiddenHeavyResolutions);
    internal BoundaryRuntimeSentinel SoakSentinel { get; } = new(BoundaryRole.Standard);
    internal object ProbeSoakBoundary(Type serviceType) => _provider.GetRequiredService(serviceType);
    internal Task StopHostAsync() => (_host ?? throw new InvalidOperationException("This row did not start a host.")).StopAsync();

    internal async Task RunAsync(WorkerJobKind kind)
    {
        switch (kind)
        {
            case WorkerJobKind.ProcessAssets:
                Assert.AreEqual(ProcessingRunAdmissionResult.Accepted,
                    await MatrixWait.ForAsync(Processing.TriggerManualAsync(), "processing/admission"));
                await MatrixWait.ForAsync(Processing.WaitForActiveRunAsync(), "processing/owned-finality");
                break;
            case WorkerJobKind.CoordinateLookup:
                await MatrixWait.ForAsync(Lookup.SubmitAsync(new(47, 8, false, false, Launcher.IsMemorySoak)), "lookup/owned-finality");
                break;
            case WorkerJobKind.CacheMutation:
                await MatrixWait.ForAsync(Cache.RefreshAsync(CacheMutationSource.Gadm, "CHE"), "cache/owned-finality");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    internal async Task JoinTelemetryAsync(WorkerProcessFixtureLease lease)
    {
        var telemetry = lease.Session!.Telemetry
            ?? throw new AssertFailedException("The real launcher must bind lifecycle telemetry.");
        await MatrixWait.ForAsync(telemetry.CancellationObservation, "harness/cancellation-telemetry");
        await MatrixWait.ForAsync(telemetry.CoalescingObservation, "harness/coalescing-telemetry");
    }

    private T RejectHeavy<T>()
    {
        Interlocked.Increment(ref _forbiddenHeavyResolutions);
        throw new AssertFailedException($"Web attempted forbidden heavy resolution: {typeof(T).Name}.");
    }

    private void ObserveOwner()
    {
        lock (_observationGate)
        {
            bool active = Coordinator.ActiveOwner is not null;
            if (_hadOwner && !active)
            {
                Interlocked.Increment(ref _releases);
            }

            _hadOwner = active;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (Func<Task> cleanup in new Func<Task>[]
        {
            () => Launcher.DisposeAsync().AsTask(),
            () => Lookup is null ? Task.CompletedTask : Lookup.DisposeAsync().AsTask(),
            () => Cache is null ? Task.CompletedTask : Cache.DisposeAsync().AsTask(),
            () => ((IAsyncDisposable)(_host ?? (object)_provider)).DisposeAsync().AsTask()
        })
        {
            try
            {
                await MatrixWait.ForAsync(cleanup(), "host/unconditional-cleanup");
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }

        if (Coordinator is not null)
        {
            Coordinator.Changed -= ObserveOwner;
        }
        Logs.Dispose();
        if (failures.Count == 0)
        {
            Directory.Delete(Root, recursive: true);
        }
        else
        {
            throw new AggregateException("Process matrix host cleanup did not reach finality.", failures);
        }
    }

    internal sealed class MatrixLauncher(string fault, RecordingLifecycleLogs logs, MatrixExecutionControl? control, string root) : IChildWorkerLauncher, IAsyncDisposable
    {
        private readonly ConcurrentQueue<WorkerProcessFixtureLease> _leases = new();
        private readonly ConcurrentDictionary<Guid, MatrixEventTap> _taps = new();
        private readonly Channel<WorkerProcessFixtureLease> _launches = Channel.CreateUnbounded<WorkerProcessFixtureLease>();
        private readonly TaskCompletionSource<WorkerProcessFixtureLease> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal WorkerProcessFixtureLease[] Leases => _leases.ToArray();
        internal Task<WorkerProcessFixtureLease> Started => _started.Task;
        internal MatrixEventTap Tap(WorkerProcessFixtureLease lease) => _taps[lease.Request.RunId];
        internal string CacheStage { get; set; } = "success";
        internal bool IsMemorySoak => fault == "memory-soak";
        internal bool SoakCancellation { get; set; }
        internal string? SoakInputDirectory { get; set; }
        internal async Task ReleaseSoakLeaseAsync(WorkerProcessFixtureLease lease)
        {
            Assert.IsTrue(IsMemorySoak);
            Assert.IsTrue(lease.Session!.Settlement.IsCompletedSuccessfully);
            await lease.DisposeAsync();
            Assert.IsTrue(_leases.TryDequeue(out var owned));
            Assert.AreSame(lease, owned);
            Assert.IsTrue(_taps.TryRemove(lease.Request.RunId, out _));
            logs.DrainEntries();
        }
        internal Task<WorkerProcessFixtureLease> NextLaunchAsync() =>
            MatrixWait.ForAsync(_launches.Reader.ReadAsync().AsTask(), "matrix/next-registered-native-launch");

        public async ValueTask<ChildWorkerLaunchResult> LaunchAsync(WorkerInvocation invocation,
            WorkerJobDispatch dispatch, IWorkerJobEventSink eventSink, ChildWorkerLauncherOptions options,
            CancellationToken cancellationToken)
        {
            var request = dispatch is ProcessAssetsWorkerJobDispatch processing
                ? processing.Request.ProcessingRequest
                : new ProcessingRunRequest(dispatch.Context.JobId, ProcessingRunTrigger.Manual);
            var lease = new WorkerProcessFixtureLease
            {
                Request = request,
                LauncherOptions = options,
                LifecycleLogger = logs.CreateLogger(LifecycleEventCatalog.Category),
                StandardOutputGate = control?.Output,
                StandardErrorGate = control?.Error,
                SharedResourceRoot = fault == "cache-matrix" ? root : null
            };
            _leases.Enqueue(lease);
            var tap = new MatrixEventTap(eventSink, control);
            Assert.IsTrue(_taps.TryAdd(request.RunId, tap), "Every launch must own a new job identity.");
            try
            {
                if (IsMemorySoak)
                {
                    Directory.CreateDirectory(Path.Combine(lease.Root, "bundled-data"));
                    File.Copy(Path.Combine(root, "bundled-data", "iso3166.json"),
                        Path.Combine(lease.Root, "bundled-data", "iso3166.json"));
                    if (SoakInputDirectory is not null)
                    {
                        string input = Path.Combine(lease.Root, "soak-input");
                        Directory.CreateDirectory(input);
                        foreach (string name in new[] { "assets.db", "gadm.db", "source.gpkg" })
                        {
                            File.Copy(Path.Combine(SoakInputDirectory, name), Path.Combine(input, name));
                        }
                    }
                    var soakSession = dispatch.Context.JobKind == WorkerJobKind.CacheMutation
                        ? await lease.LaunchAsync("real-cache-matrix", dispatch, tap, invocation.ProtocolVersion, false,
                            "--cache-matrix-stage", CacheStage)
                        : await lease.LaunchAsync(SoakCancellation ? "real-processing-cancellation" : "real-memory-soak",
                            dispatch, tap, invocation.ProtocolVersion, false);
                    _started.TrySetResult(lease);
                    _launches.Writer.TryWrite(lease);
                    return new ChildWorkerLaunchResult.Started(soakSession);
                }
                if (fault == "spawn-failure")
                {
                    return await lease.LaunchMissingExecutableAsync(dispatch, tap, invocation.ProtocolVersion);
                }

                string[] faultOptions = fault == "missing-ready"
                    ? ["--matrix-fault", fault, "--matrix-job-id", request.RunId.ToString("D")]
                    : ["--matrix-fault", fault];
                var session = fault == "cache-matrix"
                    ? await lease.LaunchAsync("real-cache-matrix", dispatch, tap, invocation.ProtocolVersion, false,
                        "--cache-matrix-stage", CacheStage)
                    : fault is "real-coordinate-success" or "real-coordinate-domain-failure" or "real-coordinate-cancellation"
                        or "real-coordinate-startup-failure" or "real-processing-cancellation"
                    ? await lease.LaunchAsync(fault, dispatch, tap, invocation.ProtocolVersion, false)
                    : fault == "replaceable-pressure"
                    ? await lease.LaunchAsync("progress-burst", dispatch, tap, invocation.ProtocolVersion, true,
                        "--progress-count", "4000", "--barrier-every", "0")
                    : await lease.LaunchAsync("failure-matrix", dispatch, tap,
                        invocation.ProtocolVersion, true, faultOptions);
                _started.TrySetResult(lease);
                _launches.Writer.TryWrite(lease);
                return new ChildWorkerLaunchResult.Started(session);
            }
            catch (Exception failure)
            {
                _started.TrySetException(failure);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            control?.ReleaseAll();
            await WorkerProcessFixtureLease.ReapAsync(_leases.ToArray());
        }
    }

    private sealed class MatrixCommandBuilder(InternalWorkerProtocolVersion selectedVersion) : IWorkerCommandInvocationBuilder
    {
        public WorkerCommandInvocationResolution Build() => Build(InternalWorkerProtocolVersion.V1);

        public WorkerCommandInvocationResolution Build(InternalWorkerProtocolVersion protocolVersion) =>
            WorkerInvocation.Resolve(new WorkerCommandRuntimeFacts(
                WorkerInvocation.TrustedWebAssemblyIdentity,
                Path.Combine(CrossProcessAppHostLocator.ProductionWorkerAppHostDirectory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), WorkerTargetObservation.File,
                WorkerInvocation.TrustedWebAssemblyIdentity, Path.Combine(CrossProcessAppHostLocator.ProductionWorkerAppHostDirectory, "ImmichReverseGeo.Web.dll"), WorkerTargetObservation.File,
                CrossProcessAppHostLocator.ProductionWorkerAppHostDirectory, WorkerTargetObservation.Directory,
                OperatingSystem.IsWindows() ? WorkerPathSemantics.Windows : WorkerPathSemantics.Unix), selectedVersion);
    }
}
