using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.WorkerBackendSelection;

[TestClass]
[TestCategory("Change33")]
public sealed class LifecycleProcessingBackendTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task InProcessBackend_TerminalOutcomesUseSharedLifecycleWithoutResolvingChildControlPlane()
    {
        foreach (var outcome in new[]
        {
            ProcessingRunOutcome.Completed,
            ProcessingRunOutcome.Cancelled,
            ProcessingRunOutcome.Failed
        })
        {
            using var fixture = InProcessFixture.Create(new TerminalExecutor(outcome));
            var executor = Assert.IsInstanceOfType<TerminalExecutor>(fixture.Executor);

            Assert.AreEqual(
                ProcessingRunAdmissionResult.Accepted,
                await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
                outcome + "-admission");
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

            Assert.AreEqual(1, executor.CallCount, outcome + "-one-inprocess-execution");
            Assert.AreEqual(0, fixture.ChildControlPlaneResolutions, outcome + "-no-child-controlplane-resolution");
            Assert.AreEqual(outcome, executor.Result!.Outcome, outcome + "-returned-result");
            Assert.IsFalse(fixture.State.IsRunning, outcome + "-returns-idle");
            Assert.IsNull(fixture.State.CurrentActivity, outcome + "-closes-activities");
            Assert.AreEqual(1, CountLog(fixture.State, "Run complete."), outcome + "-one-terminal-state-mutation");
        }
    }

    [TestMethod]
    public async Task InProcessBackend_CoordinatorCancellationUsesTheExecutorTokenWithoutChildFallback()
    {
        using var fixture = InProcessFixture.Create(new CooperativeCancellationExecutor());
        var executor = Assert.IsInstanceOfType<CooperativeCancellationExecutor>(fixture.Executor);

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "inprocess-cancel-admission");
        await executor.Entered.Task.WaitAsync(Bound);
        Task stop = fixture.Coordinator.StopActiveRun()!;
        try
        {
            await executor.CancellationObserved.Task.WaitAsync(Bound);
            await stop.WaitAsync(Bound);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

            Assert.AreEqual(1, executor.CallCount, "inprocess-cancel-one-execution");
            Assert.AreEqual(0, fixture.ChildControlPlaneResolutions, "inprocess-cancel-no-child-resolution");
            Assert.AreEqual(ProcessingRunOutcome.Cancelled, executor.Result!.Outcome, "inprocess-cancel-result");
            Assert.IsFalse(fixture.State.IsRunning, "inprocess-cancel-idle");
        }
        finally
        {
            Task? cleanup = fixture.Coordinator.StopActiveRun();
            if (cleanup is not null)
            {
                await cleanup.WaitAsync(Bound);
            }
        }
    }

    [TestMethod]
    public async Task InProcessBackend_ExecutorFailureCleansTheExactHandleWithoutResolvingChildOrRetrying()
    {
        using var fixture = InProcessFixture.Create(new ThrowingExecutor());
        var executor = Assert.IsInstanceOfType<ThrowingExecutor>(fixture.Executor);

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "inprocess-failure-admission");
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

        Assert.AreEqual(1, executor.CallCount, "inprocess-failure-one-execution");
        Assert.AreEqual(0, fixture.ChildControlPlaneResolutions, "inprocess-failure-no-child-resolution");
        Assert.IsNull(fixture.Coordinator.ActiveRequest, "inprocess-failure-exact-handle-released");
        Assert.IsFalse(fixture.State.IsRunning, "inprocess-failure-idle");
        Assert.AreEqual("Fatal: Synthetic executor failure.", fixture.State.LastError, "inprocess-failure-fatal-projection");
        Assert.AreEqual(1, CountLog(fixture.State, "Run complete."), "inprocess-failure-one-abandonment");
    }

    private static int CountLog(ProcessingState state, string text)
    {
        return state.RecentLog.Count(line => line.Contains(text, StringComparison.Ordinal));
    }

    private sealed class InProcessFixture : IDisposable
    {
        private readonly ServiceProvider _provider;

        private InProcessFixture(
            ServiceProvider provider,
            IProcessingRunExecutor executor,
            Func<int> childControlPlaneResolutions)
        {
            _provider = provider;
            Executor = executor;
            _childControlPlaneResolutions = childControlPlaneResolutions;
            Coordinator = provider.GetRequiredService<ProcessingRunCoordinator>();
            State = provider.GetRequiredService<ProcessingState>();
        }

        private readonly Func<int> _childControlPlaneResolutions;

        internal IProcessingRunExecutor Executor { get; }
        internal int ChildControlPlaneResolutions => _childControlPlaneResolutions();
        internal ProcessingRunCoordinator Coordinator { get; }
        internal ProcessingState State { get; }

        internal static InProcessFixture Create(IProcessingRunExecutor executor)
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ProcessingRunCoordinator>>(
                NullLogger<ProcessingRunCoordinator>.Instance);
            services.AddProcessingControlPlaneServices(ProcessingBackendKind.InProcess);
            services.AddSingleton(executor);
            services.AddSingleton<IProcessingRunExecutor>(executor);
            var childControlPlaneResolutions = 0;
            services.AddSingleton<WorkerRunControlPlane>(_ =>
            {
                childControlPlaneResolutions++;
                throw new InvalidOperationException("The in-process selection must not resolve child control-plane services.");
            });
            var provider = services.BuildServiceProvider(validateScopes: true);
            return new InProcessFixture(provider, executor, () => childControlPlaneResolutions);
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            _provider.Dispose();
        }
    }

    private abstract class RecordingExecutor : IProcessingRunExecutor
    {
        internal int CallCount { get; private set; }
        internal ProcessingRunResult? Result { get; private protected set; }

        public async Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return await ExecuteCoreAsync(request, reporter, cancellationToken).ConfigureAwait(false);
        }

        protected abstract Task<ProcessingRunResult> ExecuteCoreAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken);

        protected async Task<ProcessingRunResult> FinishAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            ProcessingRunOutcome outcome)
        {
            IProcessingRunEventSession session = await reporter.OpenRunAsync(request, Now, CancellationToken.None).ConfigureAwait(false);
            await session.DetermineEligibilityAsync(0, CancellationToken.None).ConfigureAwait(false);
            var result = new ProcessingRunResult(
                request,
                Now,
                Now,
                0,
                0,
                0,
                0,
                outcome,
                outcome == ProcessingRunOutcome.Failed ? "synthetic executor failure" : null);
            await session.FinishAsync(result).ConfigureAwait(false);
            Result = result;
            return result;
        }
    }

    private sealed class TerminalExecutor(ProcessingRunOutcome outcome) : RecordingExecutor
    {
        protected override Task<ProcessingRunResult> ExecuteCoreAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return FinishAsync(request, reporter, outcome);
        }
    }

    private sealed class CooperativeCancellationExecutor : RecordingExecutor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<ProcessingRunResult> ExecuteCoreAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("The active coordinator token must cancel the in-process run.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                return await FinishAsync(request, reporter, ProcessingRunOutcome.Cancelled).ConfigureAwait(false);
            }
        }
    }

    private sealed class ThrowingExecutor : RecordingExecutor
    {
        protected override Task<ProcessingRunResult> ExecuteCoreAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = reporter;
            _ = cancellationToken;
            return Task.FromException<ProcessingRunResult>(new InvalidOperationException("Synthetic executor failure."));
        }
    }
}
