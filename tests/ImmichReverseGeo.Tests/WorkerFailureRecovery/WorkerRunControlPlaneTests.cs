using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using Microsoft.Extensions.Logging.Abstractions;
using WorkerCommandInvocationType = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.WorkerFailureRecovery;

[TestClass]
[TestCategory("Change30")]
public class WorkerRunControlPlaneTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task ExecuteAsync_BuilderFailureOrThrowFinalizesOnceWithoutLaunching()
    {
        foreach (var builder in new IWorkerCommandInvocationBuilder[]
        {
            new FixedBuilder(WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.ProcessPathUnavailable)),
            new ThrowingBuilder()
        })
        {
            var fixture = new Fixture(builder, new RecordingLauncher(), Guid.NewGuid());

            Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

            var request = fixture.Executor.LastRequest!;
            var receipt = fixture.Reporter.GetFinalizationReceipt(request);
            Assert.IsNotNull(receipt, "builder-failure-commits-receipt");
            Assert.AreEqual(ProcessingRunOutcome.Failed, receipt.Result.Outcome, "builder-failure-outcome");
            Assert.AreEqual(WorkerRunDiagnostics.Describe(WorkerRunFailureCategory.CommandResolution, WorkerRunTransportPhase.Resolving), receipt.Result.FailureMessage, "builder-failure-safe-message");
            Assert.AreEqual(1, fixture.BuilderCallCount, "builder-called-once");
            Assert.AreEqual(0, fixture.Launcher.CallCount, "builder-failure-never-launches");
            Assert.IsFalse(fixture.State.IsRunning, "builder-failure-ui-inactive");
            Assert.IsNull(fixture.Coordinator.ActiveRequest, "builder-failure-handle-released");
            Assert.AreEqual(1, fixture.Cancellations.Created.Count, "builder-failure-one-handle");
            Assert.AreEqual(1, fixture.Cancellations.Created[0].DisposeCount, "builder-failure-handle-disposed-once");
            Assert.AreEqual(1, CountLog(fixture.State, "Fatal: Worker command unavailable."), "builder-failure-one-fatal");
            Assert.AreEqual(1, CountLog(fixture.State, "Run complete."), "builder-failure-one-summary");
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_StartFailureHasNoSessionAndCleansEachHandleBeforeRetrigger()
    {
        var firstId = Guid.Parse("40404040-4040-4040-4040-404040404040");
        var secondId = Guid.Parse("50505050-5050-5050-5050-505050505050");
        var launcher = new RecordingLauncher();
        var fixture = new Fixture(new FixedBuilder(ValidResolution()), launcher, firstId, secondId);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        var firstRequest = fixture.Executor.LastRequest!;
        var firstReceipt = fixture.Reporter.GetFinalizationReceipt(firstRequest);

        Assert.IsNotNull(firstReceipt, "start-failure-first-receipt");
        Assert.AreEqual(ProcessingRunOutcome.Failed, firstReceipt.Result.Outcome, "start-failure-first-outcome");
        Assert.AreEqual(1, launcher.CallCount, "start-failure-launch-once");
        Assert.AreEqual(0, launcher.StartedSessionCount, "start-failure-no-session");
        Assert.AreEqual(1, fixture.Cancellations.Created[0].DisposeCount, "start-failure-first-handle-disposed");

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        var secondRequest = fixture.Executor.LastRequest!;

        Assert.AreEqual(secondId, secondRequest.RunId, "retrigger-uses-new-id");
        Assert.AreNotSame(firstRequest, secondRequest, "retrigger-uses-new-request");
        Assert.AreEqual(2, launcher.CallCount, "retrigger-launches-once-per-handle");
        Assert.AreEqual(0, launcher.StartedSessionCount, "retrigger-still-has-no-session");
        Assert.IsFalse(fixture.State.IsRunning, "start-failure-ui-inactive");
        Assert.IsNull(fixture.Coordinator.ActiveRequest, "start-failure-handle-released");
        Assert.AreEqual(2, fixture.Cancellations.Created.Count, "retrigger-two-handles");
        Assert.IsTrue(fixture.Cancellations.Created.All(item => item.DisposeCount == 1), "every-handle-disposed-exactly-once");
    }

    [TestMethod]
    public async Task ExecuteAsync_DuplicateOrStaleCallsAreRejectedBeforeBuildingOrLaunching()
    {
        var builder = new GatedFailureBuilder();
        var fixture = new Fixture(builder, new RecordingLauncher(), Guid.NewGuid());
        var admitted = Task.Run(fixture.Coordinator.TriggerManualAsync);
        await builder.Entered.Task.WaitAsync(Bound);
        var request = fixture.Executor.LastRequest!;

        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.ControlPlane.ExecuteAsync(fixture.Coordinator, request));
            Assert.AreEqual(1, builder.CallCount, "duplicate-never-builds");
            Assert.AreEqual(0, fixture.Launcher.CallCount, "duplicate-never-launches");
        }
        finally
        {
            builder.Release.TrySetResult();
        }

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await admitted.WaitAsync(Bound));
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        var callsAfterFirstFinalization = builder.CallCount;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.ControlPlane.ExecuteAsync(fixture.Coordinator, request));
        Assert.AreEqual(callsAfterFirstFinalization, builder.CallCount, "stale-never-builds");
        Assert.AreEqual(0, fixture.Launcher.CallCount, "stale-never-launches");
    }

    private static int CountLog(ProcessingState state, string value) => state.GetRecentLog().Count(line => line.Contains(value, StringComparison.Ordinal));

    private static WorkerCommandInvocationResolution ValidResolution()
    {
        var facts = new WorkerCommandRuntimeFacts(
            WorkerCommandInvocationType.TrustedWebAssemblyIdentity,
            "/fixture/dotnet",
            WorkerTargetObservation.File,
            WorkerCommandInvocationType.TrustedWebAssemblyIdentity,
            "/fixture/ImmichReverseGeo.Web.dll",
            WorkerTargetObservation.File,
            "/fixture",
            WorkerTargetObservation.Directory,
            WorkerPathSemantics.Unix);
        var resolution = WorkerCommandInvocationType.Resolve(facts);
        Assert.IsInstanceOfType<WorkerCommandInvocationResolution.Success>(resolution, "valid-resolver-fixture");
        return resolution;
    }

    private sealed class Fixture
    {
        private readonly Queue<Guid> _ids;

        internal Fixture(IWorkerCommandInvocationBuilder builder, RecordingLauncher launcher, params Guid[] ids)
        {
            _ids = new Queue<Guid>(ids);
            State = new ProcessingState();
            Reporter = new ProcessingStateEventReporter(State);
            Cancellations = new TrackingCancellationFactory();
            Launcher = launcher;
            Builder = builder;
            Executor = new DispatchingExecutor();
            Coordinator = new ProcessingRunCoordinator(
                State,
                Reporter,
                Executor,
                NullLogger<ProcessingRunCoordinator>.Instance,
                NextId,
                Cancellations,
                null,
                timeProvider: TimeProvider.System);
            ControlPlane = new WorkerRunControlPlane(Builder, Launcher, Reporter, TimeProvider.System);
            Executor.Configure(ControlPlane, Coordinator);
        }

        internal ProcessingState State { get; }
        internal ProcessingStateEventReporter Reporter { get; }
        internal TrackingCancellationFactory Cancellations { get; }
        internal IWorkerCommandInvocationBuilder Builder { get; }
        internal int BuilderCallCount => Builder switch
        {
            FixedBuilder fixedBuilder => fixedBuilder.CallCount,
            ThrowingBuilder throwingBuilder => throwingBuilder.CallCount,
            GatedFailureBuilder gatedBuilder => gatedBuilder.CallCount,
            _ => throw new InvalidOperationException("The test builder must expose its count.")
        };
        internal RecordingLauncher Launcher { get; }
        internal DispatchingExecutor Executor { get; }
        internal ProcessingRunCoordinator Coordinator { get; }
        internal WorkerRunControlPlane ControlPlane { get; }

        private Guid NextId() => _ids.Dequeue();
    }

    private sealed class DispatchingExecutor : IProcessingRunExecutor
    {
        private WorkerRunControlPlane? _controlPlane;
        private ProcessingRunCoordinator? _coordinator;

        internal ProcessingRunRequest? LastRequest { get; private set; }
        internal int CallCount { get; private set; }

        internal void Configure(WorkerRunControlPlane controlPlane, ProcessingRunCoordinator coordinator)
        {
            _controlPlane = controlPlane;
            _coordinator = coordinator;
        }

        public Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return _controlPlane!.ExecuteAsync(_coordinator!, request);
        }
    }

    private sealed class FixedBuilder(WorkerCommandInvocationResolution result) : IWorkerCommandInvocationBuilder
    {
        internal int CallCount { get; private set; }

        public WorkerCommandInvocationResolution Build()
        {
            CallCount++;
            return result;
        }
    }

    private sealed class ThrowingBuilder : IWorkerCommandInvocationBuilder
    {
        internal int CallCount { get; private set; }

        public WorkerCommandInvocationResolution Build()
        {
            CallCount++;
            throw new InvalidOperationException("builder failure");
        }
    }

    private sealed class GatedFailureBuilder : IWorkerCommandInvocationBuilder
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CallCount { get; private set; }

        public WorkerCommandInvocationResolution Build()
        {
            CallCount++;
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.ProcessPathUnavailable);
        }
    }

    private sealed class RecordingLauncher : IChildWorkerLauncher
    {
        internal int CallCount { get; private set; }
        internal int StartedSessionCount { get; private set; }

        public ValueTask<ChildWorkerLaunchResult> LaunchAsync(
            WorkerCommandInvocationType invocation,
            ProcessingRunRequest request,
            IWorkerProtocolEventSink eventSink,
            ChildWorkerLauncherOptions options,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult<ChildWorkerLaunchResult>(new ChildWorkerLaunchResult.StartFailed(ChildWorkerStartFailureCategory.ProcessStartFailed));
        }
    }

    private sealed class TrackingCancellationFactory : IProcessingRunCancellationFactory
    {
        internal List<TrackingCancellation> Created { get; } = [];

        public IProcessingRunCancellation Create(ProcessingRunRequest request, CancellationToken linkedToken)
        {
            var cancellation = new TrackingCancellation(linkedToken);
            Created.Add(cancellation);
            return cancellation;
        }
    }

    private sealed class TrackingCancellation : IProcessingRunCancellation
    {
        private readonly CancellationTokenSource _source;

        internal TrackingCancellation(CancellationToken linkedToken)
        {
            _source = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
        }

        internal int DisposeCount { get; private set; }
        public CancellationToken Token => _source.Token;

        public void Cancel() => _source.Cancel();

        public void Dispose()
        {
            DisposeCount++;
            _source.Dispose();
        }
    }
}
