using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.ProcessingRunLocking;

[TestClass]
[TestCategory("Change32")]
public sealed class ProcessingRunDomainOperationSeamTests
{
    [TestMethod]
    public async Task ExecuteAsync_DefaultOperationInvokesProductionDomainOnce()
    {
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter().EnableCount(0);

        ProcessingRunResult result = await fixture.Executor.ExecuteAsync(
            fixture.Request,
            fixture.Reporter,
            CancellationToken.None).WaitAsync(ExecutorFixture.Bound);

        fixture.AssertTerminal(result);
        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        Assert.AreEqual(1, fixture.CountCalls);
        Assert.AreEqual(1, fixture.Reporter.Events.OfType<EligibilityDetermined>().Count());
    }

    [TestMethod]
    public async Task ExecuteAsync_BusyLock_SkipsDomainOperation()
    {
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter();
        var operation = new RecordingDomainOperation();
        WorkerProcessExitOutcomeAccumulator outcomes = new();
        ProcessingRunExecutor executor = CreateExecutor(
            fixture,
            new FixedRunLock(new ProcessingRunLockAcquisition.Busy()),
            outcomes,
            operation);

        ProcessingRunResult result = await executor.ExecuteAsync(
            fixture.Request,
            fixture.Reporter,
            CancellationToken.None).WaitAsync(ExecutorFixture.Bound);

        fixture.AssertTerminal(result);
        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.AreEqual(0, operation.Calls);
        Assert.AreEqual(0, fixture.CountCalls);
        Assert.AreSame(WorkerProcessExitFact.Busy(), outcomes.Fact);
    }

    [TestMethod]
    public async Task ExecuteAsync_ControlledDomainFault_UsesExistingFailedTerminalAndReleasesLock()
    {
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter();
        var lease = new RecordingLease();
        var operation = new RecordingDomainOperation(() =>
            Task.FromException(new InvalidOperationException("controlled domain fault")));
        WorkerProcessExitOutcomeAccumulator outcomes = new();
        ProcessingRunExecutor executor = CreateExecutor(
            fixture,
            new FixedRunLock(new ProcessingRunLockAcquisition.Acquired(lease)),
            outcomes,
            operation);

        ProcessingRunResult result = await executor.ExecuteAsync(
            fixture.Request,
            fixture.Reporter,
            CancellationToken.None).WaitAsync(ExecutorFixture.Bound);

        fixture.AssertTerminal(result);
        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.AreEqual("controlled domain fault", result.FailureMessage);
        Assert.AreEqual(1, operation.Calls);
        Assert.AreEqual(0, fixture.CountCalls);
        Assert.AreEqual(1, lease.ReleaseCalls);
        Assert.IsFalse(outcomes.HasFact);
        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task ExecuteAsync_AcquiredLock_ProvidesGuardedSessionAndOwnershipLinkedTokenToDomainOperation()
    {
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter().EnableCount(0);
        var lease = new RecordingLease();
        var operation = new OwnershipLossOperation(lease);
        WorkerProcessExitOutcomeAccumulator outcomes = new();
        ProcessingRunExecutor executor = CreateExecutor(
            fixture,
            new FixedRunLock(new ProcessingRunLockAcquisition.Acquired(lease)),
            outcomes,
            operation);

        ProcessingRunResult result = await executor.ExecuteAsync(
            fixture.Request,
            fixture.Reporter,
            CancellationToken.None).WaitAsync(ExecutorFixture.Bound);

        fixture.AssertTerminal(result);
        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.AreEqual("The database run lock was lost during processing.", result.FailureMessage);
        Assert.AreEqual(1, operation.Calls);
        Assert.IsTrue(operation.ObservedLinkedCancellation);
        Assert.AreEqual(1, fixture.CountCalls);
        Assert.AreEqual(1, fixture.Reporter.Events.OfType<RunStarted>().Count());
        Assert.AreEqual(1, fixture.Reporter.Events.OfType<EligibilityDetermined>().Count());
        Assert.AreEqual(1, fixture.Reporter.Events.OfType<LogEmitted>().Count());
        Assert.AreEqual(1, fixture.Reporter.Events.OfType<RunFinished>().Count());
        Assert.AreSame(WorkerProcessExitFact.ExecutionInfrastructure(), outcomes.Fact);
        Assert.AreEqual(1, lease.ReleaseCalls);
        await lease.DisposeAsync();
    }

    private static ProcessingRunExecutor CreateExecutor(
        ExecutorFixture fixture,
        IProcessingRunLock runLock,
        WorkerProcessExitOutcomeAccumulator outcomes,
        IProcessingRunDomainOperation domainOperation)
    {
        return new ProcessingRunExecutor(
            fixture.Logger,
            fixture,
            fixture,
            fixture,
            fixture,
            fixture,
            fixture,
            fixture.TimeProvider,
            runLock,
            outcomes,
            domainOperation);
    }

    private sealed class RecordingDomainOperation(Func<Task>? execute = null) : IProcessingRunDomainOperation
    {
        private readonly Func<Task> _execute = execute ?? (() => Task.CompletedTask);

        internal int Calls { get; private set; }

        public Task ExecuteAsync(
            IProcessingRunEventSession session,
            Func<Task> executeProductionDomainAsync,
            CancellationToken cancellationToken)
        {
            Calls++;
            return _execute();
        }
    }

    private sealed class FixedRunLock(ProcessingRunLockAcquisition acquisition) : IProcessingRunLock
    {
        public ValueTask<ProcessingRunLockAcquisition> AcquireAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(acquisition);
    }

    private sealed class OwnershipLossOperation(RecordingLease lease) : IProcessingRunDomainOperation
    {
        internal int Calls { get; private set; }

        internal bool ObservedLinkedCancellation { get; private set; }

        public async Task ExecuteAsync(
            IProcessingRunEventSession session,
            Func<Task> executeProductionDomainAsync,
            CancellationToken cancellationToken)
        {
            Calls++;
            await executeProductionDomainAsync().ConfigureAwait(false);
            await session.ReportLogAsync(
                ProcessingLogLevel.Information,
                "controlled ownership-loss domain operation",
                cancellationToken).ConfigureAwait(false);
            lease.LoseOwnership();
            ObservedLinkedCancellation = cancellationToken.IsCancellationRequested;
        }
    }

    private sealed class RecordingLease : IProcessingRunLockLease
    {
        private readonly CancellationTokenSource _ownershipLost = new();

        internal int ReleaseCalls { get; private set; }

        public CancellationToken OwnershipLost => _ownershipLost.Token;

        public bool IsOwnershipLost { get; private set; }

        public Task<ProcessingRunLockRelease> ReleaseAsync()
        {
            ReleaseCalls++;
            return Task.FromResult(new ProcessingRunLockRelease(InfrastructureFailure: false));
        }

        public ValueTask DisposeAsync()
        {
            _ownershipLost.Dispose();
            return ValueTask.CompletedTask;
        }

        internal void LoseOwnership()
        {
            IsOwnershipLost = true;
            _ownershipLost.Cancel();
        }
    }
}
