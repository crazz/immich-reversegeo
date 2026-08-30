using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ProcessingBackgroundServiceDelegationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task TriggerRunAsync_Accepted_DelegatesExactArmedRequestReporterAndManualTokenOnce()
    {
        var fixture = new HostFixture();
        var plan = fixture.Executor.Enqueue();

        await fixture.Service.TriggerRunAsync();
        var invocation = await plan.Entered.Task.WaitAsync(Bound);

        Assert.AreEqual(1, fixture.Executor.CallCount);
        Assert.AreEqual(ProcessingRunTrigger.Manual, invocation.Request.Trigger);
        Assert.AreSame(fixture.Reporter, invocation.Reporter);
        Assert.IsTrue(invocation.Token.CanBeCanceled);
        fixture.Service.CancelRun();
        Assert.IsTrue(invocation.Token.IsCancellationRequested);

        plan.Release.SetResult();
        await fixture.Service.WaitForManualAdmissionAsync().WaitAsync(Bound);
        var result = await plan.Result.Task.WaitAsync(Bound);
        var session = await plan.Session.Task.WaitAsync(Bound);
        Assert.AreSame(invocation.Request, session.Request);
        Assert.AreSame(invocation.Request, result.Request);
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, result.Outcome);
        Assert.AreEqual(1, plan.SessionOpenCount);
        Assert.AreEqual(1, plan.ResultCount);
        Assert.IsFalse(fixture.State.IsRunning);
        Assert.IsNull(fixture.State.LastError);
    }

    [TestMethod]
    public async Task TryRunScheduledAsync_Accepted_DelegatesExactArmedRequestReporterAndHostTokenOnce()
    {
        var fixture = new HostFixture();
        var plan = fixture.Executor.Enqueue();
        using var stopping = new CancellationTokenSource();

        var admission = fixture.Service.TryRunScheduledAsync(stopping.Token);
        var invocation = await plan.Entered.Task.WaitAsync(Bound);

        Assert.AreEqual(1, fixture.Executor.CallCount);
        Assert.AreEqual(ProcessingRunTrigger.Scheduled, invocation.Request.Trigger);
        Assert.AreSame(fixture.Reporter, invocation.Reporter);
        Assert.AreEqual(stopping.Token, invocation.Token);

        plan.Release.SetResult();
        await admission.WaitAsync(Bound);
        var result = await plan.Result.Task.WaitAsync(Bound);
        var session = await plan.Session.Task.WaitAsync(Bound);
        Assert.AreSame(invocation.Request, session.Request);
        Assert.AreSame(invocation.Request, result.Request);
        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        Assert.AreEqual(1, plan.SessionOpenCount);
        Assert.AreEqual(1, plan.ResultCount);
        Assert.IsFalse(fixture.State.IsRunning);
        Assert.IsNull(fixture.State.LastError);
    }

    [TestMethod]
    public async Task Admission_WhileOwned_InvokesExecutorZeroTimesAndRetainsRecoverableLock()
    {
        var fixture = new HostFixture();
        var first = fixture.Executor.Enqueue();
        await fixture.Service.TriggerRunAsync();
        await first.Entered.Task.WaitAsync(Bound);

        var firstInvocation = await first.Entered.Task.WaitAsync(Bound);
        await fixture.Service.TriggerRunAsync();
        await fixture.Service.TryRunScheduledAsync(CancellationToken.None);
        Assert.AreEqual(1, fixture.Executor.CallCount);
        Assert.IsFalse(first.Session.Task.IsCompleted);
        Assert.IsFalse(first.Result.Task.IsCompleted);
        Assert.AreEqual(0, first.SessionOpenCount);
        Assert.AreEqual(0, first.ResultCount);
        Assert.IsNull(fixture.State.LastRunStarted);
        Assert.IsTrue(fixture.State.IsRunning);

        first.Release.SetResult();
        await fixture.Service.WaitForManualAdmissionAsync().WaitAsync(Bound);

        var recovery = fixture.Executor.Enqueue();
        await fixture.Service.TriggerRunAsync();
        var recoveredInvocation = await recovery.Entered.Task.WaitAsync(Bound);
        Assert.AreEqual(ProcessingRunTrigger.Manual, recoveredInvocation.Request.Trigger);
        Assert.AreNotSame(firstInvocation.Request, recoveredInvocation.Request);
        Assert.AreSame(fixture.Reporter, recoveredInvocation.Reporter);
        Assert.AreEqual(2, fixture.Executor.CallCount);
        recovery.Release.SetResult();
        await fixture.Service.WaitForManualAdmissionAsync().WaitAsync(Bound);
        var recoveryResult = await recovery.Result.Task.WaitAsync(Bound);
        Assert.AreSame(recoveredInvocation.Request, recoveryResult.Request);
        Assert.AreEqual(1, recovery.SessionOpenCount);
        Assert.AreEqual(1, recovery.ResultCount);
        Assert.IsFalse(fixture.State.IsRunning);
    }

    private sealed class HostFixture
    {
        public ProcessingState State { get; } = new();
        public ProcessingStateEventReporter Reporter { get; }
        public RecordingExecutor Executor { get; } = new();
        public ProcessingBackgroundService Service { get; }

        public HostFixture()
        {
            Reporter = new ProcessingStateEventReporter(State);
            Service = new ProcessingBackgroundService(NullLogger<ProcessingBackgroundService>.Instance, State, Reporter, Executor, new FixedConfiguration());
        }
    }

    private sealed class FixedConfiguration : IProcessingScheduleConfiguration
    {
        public Task<ProcessingScheduleSnapshot> GetSnapshotAsync() => Task.FromResult(new ProcessingScheduleSnapshot(false, "0 * * * *"));
    }

    private sealed class RecordingExecutor : IProcessingRunExecutor
    {
        private readonly Queue<Plan> _plans = new();
        public int CallCount { get; private set; }
        public Plan Enqueue()
        {
            var plan = new Plan();
            _plans.Enqueue(plan);
            return plan;
        }

        public async Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            CallCount++;
            var plan = _plans.Dequeue();
            plan.Entered.SetResult(new Invocation(request, reporter, cancellationToken));
            await plan.Release.Task.WaitAsync(Bound);
            var started = new DateTimeOffset(2026, 8, 30, 20, 6, 34, TimeSpan.Zero);
            var session = await reporter.OpenRunAsync(request, started, CancellationToken.None);
            Interlocked.Increment(ref plan.SessionOpenCount);
            plan.Session.TrySetResult(session);
            await session.DetermineEligibilityAsync(0, CancellationToken.None);
            var outcome = cancellationToken.IsCancellationRequested ? ProcessingRunOutcome.Cancelled : ProcessingRunOutcome.Completed;
            var result = new ProcessingRunResult(request, started, started, 0, 0, 0, 0, outcome, null);
            await session.FinishAsync(result);
            Interlocked.Increment(ref plan.ResultCount);
            plan.Result.TrySetResult(result);
            return result;
        }
    }

    private sealed class Plan
    {
        public TaskCompletionSource<Invocation> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IProcessingRunEventSession> Session { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ProcessingRunResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SessionOpenCount;
        public int ResultCount;
    }

    private sealed record Invocation(ProcessingRunRequest Request, IProcessingEventReporter Reporter, CancellationToken Token);
}
