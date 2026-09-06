using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using Microsoft.Extensions.Logging.Abstractions;
using WorkerInvocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;
using WebWorkerCommandInvocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.CrossProcessRunExclusion;

internal sealed class ProcessCoordinatorFixture
{
    private readonly DispatchingExecutor _executor;
    private ParentWorker? _currentWorker;
    private int _generation;

    internal ProcessCoordinatorFixture()
    {
        State = new ProcessingState();
        Reporter = new ProcessingStateEventReporter(State);
        _executor = new DispatchingExecutor();
        Coordinator = new ProcessingRunCoordinator(
            State,
            Reporter,
            new TemporaryProcessingBackendSelection(ProcessingBackendKind.InProcess),
            ProcessingRunBackendTestScopeFactory.Create(_executor),
            NullLogger<ProcessingRunCoordinator>.Instance,
            Guid.NewGuid,
            new LifecycleObserver(this),
            applicationLifetime: null,
            timeProvider: TimeProvider.System);
        var builder = new FixedInvocationBuilder();
        ControlPlane = new WorkerRunControlPlane(builder, new ParentLauncher(this), Reporter, TimeProvider.System);
        _executor.Configure(ControlPlane, Coordinator, this);
    }

    internal ProcessingState State { get; }
    internal ProcessingStateEventReporter Reporter { get; }
    internal ProcessingRunCoordinator Coordinator { get; }
    internal WorkerRunControlPlane ControlPlane { get; }
    internal int Generation => _generation;

    internal void Prepare(ParentWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        if (Coordinator.ActiveRequest is not null)
        {
            throw new InvalidOperationException("The Change32 coordinator still owns an active request.");
        }

        _currentWorker = worker;
        _generation++;
    }

    private ParentWorker CurrentWorker => _currentWorker
        ?? throw new InvalidOperationException("The Change32 coordinator has no prepared worker.");

    private sealed class DispatchingExecutor : IProcessingRunExecutor
    {
        private WorkerRunControlPlane? _controlPlane;
        private ProcessingRunCoordinator? _coordinator;
        private ProcessCoordinatorFixture? _fixture;

        internal void Configure(WorkerRunControlPlane controlPlane, ProcessingRunCoordinator coordinator, ProcessCoordinatorFixture fixture)
        {
            _controlPlane = controlPlane;
            _coordinator = coordinator;
            _fixture = fixture;
        }

        public Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            ProcessCoordinatorFixture fixture = _fixture
                ?? throw new InvalidOperationException("The Change32 dispatching executor has not been configured.");
            WorkerRunControlPlane controlPlane = _controlPlane
                ?? throw new InvalidOperationException("The Change32 control plane has not been configured.");
            ProcessingRunCoordinator coordinator = _coordinator
                ?? throw new InvalidOperationException("The Change32 coordinator has not been configured.");
            fixture.CurrentWorker.BindRequest(request);
            return controlPlane.ExecuteAsync(coordinator, request);
        }
    }

    private sealed class ParentLauncher(ProcessCoordinatorFixture fixture) : IChildWorkerLauncher
    {
        public ValueTask<ChildWorkerLaunchResult> LaunchAsync(WorkerInvocation invocation, ProcessingRunRequest request, IWorkerProtocolEventSink eventSink, ChildWorkerLauncherOptions options, CancellationToken cancellationToken) =>
            fixture.CurrentWorker.LaunchAsync(invocation, request, eventSink, options, cancellationToken);
    }

    private sealed class LifecycleObserver(ProcessCoordinatorFixture fixture) : IProcessingRunCoordinatorObserver
    {
        public ValueTask BeforeDetachAsync(ProcessingRunRequest request, CancellationToken activeToken)
        {
            fixture.CurrentWorker.ObserveBeforeDetach(request);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedInvocationBuilder : IWorkerCommandInvocationBuilder
    {
        public WorkerCommandInvocationResolution Build()
        {
            var facts = new WorkerCommandRuntimeFacts(
                WebWorkerCommandInvocation.TrustedWebAssemblyIdentity,
                "/fixture/dotnet",
                WorkerTargetObservation.File,
                WebWorkerCommandInvocation.TrustedWebAssemblyIdentity,
                "/fixture/ImmichReverseGeo.Web.dll",
                WorkerTargetObservation.File,
                "/fixture",
                WorkerTargetObservation.Directory,
                WorkerPathSemantics.Unix);
            return WebWorkerCommandInvocation.Resolve(facts);
        }
    }
}
