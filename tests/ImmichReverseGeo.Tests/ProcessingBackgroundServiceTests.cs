using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ProcessingBackgroundServiceTests
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);
    [TestMethod]
    public async Task RunOnceAsync_WhenExactEligibilityCountIsZero_CompletesWithoutFurtherOperations()
    {
        var state = new ProcessingState();
        var countCompletion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var countCalls = 0;

        Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            countCalls++;
            return countCompletion.Task;
        }

        var operations = new ProcessingBackgroundService.ProcessingOperations(
            GetUnprocessedCountAsync,
            () => throw UnexpectedOperation("configuration read"),
            () => throw UnexpectedOperation("skipped-record loading"),
            (_, _, _) => throw UnexpectedOperation("batch retrieval"),
            (_, _, _, _, _) => throw UnexpectedOperation("administrative-area resolution"),
            (_, _, _, _) => throw UnexpectedOperation("airport lookup"),
            _ => throw UnexpectedOperation("skipped-record write"),
            (_, _, _) => throw UnexpectedOperation("location write"));

        var passTask = ProcessingBackgroundService.RunOnceAsync(
            NullLogger<ProcessingBackgroundService>.Instance,
            state,
            operations,
            CancellationToken.None);

        Assert.AreEqual(1, countCalls);
        Assert.IsFalse(passTask.IsCompleted, "The processing pass must await the exact-count operation.");

        countCompletion.SetResult(0);
        await passTask;

        Assert.AreEqual(1, countCalls);
        Assert.AreEqual(0, state.TotalUnprocessed);
        Assert.AreEqual(0, state.ProcessedThisRun);
        Assert.AreEqual(0, state.SkippedThisRun);
        Assert.AreEqual(0, state.ErrorsThisRun);
        Assert.IsFalse(state.IsRunning);
        Assert.IsNull(state.LastError);
        Assert.IsNotNull(state.LastRunStarted);
        Assert.IsNotNull(state.LastRunCompleted);
        AssertLogOrder(
            state,
            "Run started — nothing to process, all assets already have location data.",
            "Run complete. Processed=0 Skipped=0 Errors=0");
    }

    [TestMethod]
    public async Task TriggerRunAsync_TransitionsFromPendingToActiveAfterEligibilityEvaluation()
    {
        var fixture = new ProcessingFixture();
        var service = fixture.CreateService();
        var pass = fixture.QueuePass(PassOutcome.Success);

        await service.TriggerRunAsync();
        await pass.PassEntered.Task.WaitAsync(GateTimeout);

        Assert.IsTrue(fixture.State.IsRunning);
        Assert.IsNull(fixture.State.LastRunStarted);
        Assert.AreEqual(1, fixture.NotificationCount);

        pass.CountReleased.SetResult(1);
        await pass.Active.Task.WaitAsync(GateTimeout);
        await pass.BatchEntered.Task.WaitAsync(GateTimeout);

        Assert.IsTrue(fixture.State.IsRunning);
        Assert.IsNotNull(fixture.State.LastRunStarted);

        await CompletePassAsync(service, pass);
    }

    [TestMethod]
    public async Task TriggerRunAsync_SuccessfulActivePass_CompletesWithSummary()
    {
        var fixture = new ProcessingFixture();
        var service = fixture.CreateService();
        var pass = await StartActiveManualPassAsync(fixture, service, PassOutcome.Success);

        await CompletePassAsync(service, pass);

        AssertTerminalSuccess(fixture.State);
    }

    [TestMethod]
    public async Task CancelRun_AtTokenAwarePostStartBoundary_CompletesWithoutError()
    {
        var fixture = new ProcessingFixture();
        var service = fixture.CreateService();
        var pass = await StartActiveManualPassAsync(fixture, service, PassOutcome.Cancellation);

        await CompletePassAsync(service, pass);

        Assert.IsFalse(fixture.State.IsRunning);
        Assert.IsNotNull(fixture.State.LastRunCompleted);
        Assert.IsNull(fixture.State.LastError);
        Assert.AreEqual(0, fixture.State.ErrorsThisRun);
        AssertLogOrder(
            fixture.State,
            "Run cancelled.",
            "Run complete. Processed=0 Skipped=0 Errors=0");
    }

    [TestMethod]
    public async Task TriggerRunAsync_PostStartPassFailure_ExposesErrorBeforeSummary()
    {
        var fixture = new ProcessingFixture();
        var service = fixture.CreateService();
        var pass = await StartActiveManualPassAsync(fixture, service, PassOutcome.Failure);

        await CompletePassAsync(service, pass);

        Assert.IsFalse(fixture.State.IsRunning);
        Assert.IsNotNull(fixture.State.LastRunCompleted);
        Assert.AreEqual("Fatal: injected pass failure", fixture.State.LastError);
        Assert.AreEqual(1, fixture.State.ErrorsThisRun);
        AssertLogOrder(
            fixture.State,
            "[ERROR] Fatal: injected pass failure",
            "Run complete. Processed=0 Skipped=0 Errors=1");
    }

    [TestMethod]
    public async Task TriggerRunAsync_WhileRunOwnsExecution_SilentlyRejectsDuplicateManualTrigger()
    {
        var fixture = new ProcessingFixture();
        var service = fixture.CreateService();
        var pass = await StartActiveManualPassAsync(fixture, service, PassOutcome.Success);
        var started = fixture.State.LastRunStarted;
        var notificationCount = fixture.NotificationCount;
        var logCount = fixture.State.GetRecentLog().Count;

        await service.TriggerRunAsync();

        Assert.AreEqual(1, fixture.PassCount);
        Assert.AreEqual(notificationCount, fixture.NotificationCount);
        Assert.AreEqual(started, fixture.State.LastRunStarted);
        Assert.AreEqual(logCount, fixture.State.GetRecentLog().Count);

        await CompletePassAsync(service, pass);
    }

    [TestMethod]
    public async Task TryRunScheduledAsync_WhileRunOwnsExecution_LogsScheduledContentionOnly()
    {
        var fixture = new ProcessingFixture();
        var service = fixture.CreateService();
        var pass = await StartActiveManualPassAsync(fixture, service, PassOutcome.Success);
        var started = fixture.State.LastRunStarted;
        var notificationCount = fixture.NotificationCount;

        await service.TryRunScheduledAsync(CancellationToken.None);

        Assert.AreEqual(1, fixture.PassCount);
        Assert.AreEqual(notificationCount + 1, fixture.NotificationCount);
        Assert.AreEqual(started, fixture.State.LastRunStarted);
        Assert.AreEqual(
            1,
            CountMessage(fixture.State.GetRecentLog(), "Scheduled run skipped because a processing pass is already in progress."));

        await CompletePassAsync(service, pass);
    }

    [TestMethod]
    public async Task TryRunScheduledAsync_AcceptedAdmission_TransitionsFromPendingToActiveAndReleasesOwnership()
    {
        var fixture = new ProcessingFixture();
        var service = fixture.CreateService();
        var pass = fixture.QueuePass(PassOutcome.Success);

        var scheduledAdmission = service.TryRunScheduledAsync(CancellationToken.None);
        await pass.PassEntered.Task.WaitAsync(GateTimeout);

        Assert.IsTrue(fixture.State.IsRunning);
        Assert.IsNull(fixture.State.LastRunStarted);

        pass.CountReleased.SetResult(1);
        await pass.Active.Task.WaitAsync(GateTimeout);
        await pass.BatchEntered.Task.WaitAsync(GateTimeout);

        Assert.IsTrue(fixture.State.IsRunning);
        Assert.IsNotNull(fixture.State.LastRunStarted);

        pass.BatchReleased.SetResult(true);
        await scheduledAdmission;
        await pass.Terminal.Task.WaitAsync(GateTimeout);

        AssertTerminalSuccess(fixture.State);
    }

    [TestMethod]
    [DataRow(PassOutcome.Success)]
    [DataRow(PassOutcome.Cancellation)]
    [DataRow(PassOutcome.Failure)]
    public async Task ManualAdmission_AfterEachTerminalCleanup_CanStartDirectly(PassOutcome outcome)
    {
        var fixture = new ProcessingFixture();
        var service = fixture.CreateService();
        var first = await StartActiveManualPassAsync(fixture, service, outcome);
        await CompletePassAsync(service, first);

        var manual = fixture.QueuePass(PassOutcome.Success);
        await service.TriggerRunAsync();
        await ReachActiveAsync(fixture, manual);
        await CompletePassAsync(service, manual);

        Assert.AreEqual(2, fixture.PassCount);
        AssertTerminalSuccess(fixture.State);
    }

    [TestMethod]
    [DataRow(PassOutcome.Success)]
    [DataRow(PassOutcome.Cancellation)]
    [DataRow(PassOutcome.Failure)]
    public async Task ScheduledAdmission_AfterEachTerminalCleanup_CanStartDirectly(PassOutcome outcome)
    {
        var fixture = new ProcessingFixture();
        var service = fixture.CreateService();
        var first = await StartActiveManualPassAsync(fixture, service, outcome);
        await CompletePassAsync(service, first);

        var scheduled = fixture.QueuePass(PassOutcome.Success);
        var scheduledAdmission = service.TryRunScheduledAsync(CancellationToken.None);
        await ReachActiveAsync(fixture, scheduled);
        scheduled.BatchReleased.SetResult(true);
        await scheduledAdmission;
        await scheduled.Terminal.Task.WaitAsync(GateTimeout);

        Assert.AreEqual(2, fixture.PassCount);
        AssertTerminalSuccess(fixture.State);
    }

    [TestMethod]
    public async Task RunOnceAsync_ActiveGeodataCancellation_UsesRunCancellationWithoutAssetSideEffects()
    {
        var state = new ProcessingState();
        using var cts = new CancellationTokenSource();
        var skipped = 0;
        var writes = 0;
        var asset = new AssetRecord(Guid.NewGuid(), 1, 2, DateTime.UtcNow);
        var operations = new ProcessingBackgroundService.ProcessingOperations(
            _ => Task.FromResult(1L),
            () => Task.FromResult(new AppConfig { Processing = new ProcessingConfig { BatchDelayMs = 0 } }),
            () => Task.FromResult(new HashSet<Guid>()),
            (_, _, _) => Task.FromResult(new List<AssetRecord> { asset }),
            async (_, _, _, session, _) =>
            {
                await using var activity = await session.BeginActivityAsync("active geodata cancellation");
                Assert.AreEqual("active geodata cancellation", state.CurrentActivity);
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
            (_, _, _, _) => throw UnexpectedOperation("airport lookup"),
            _ =>
            {
                skipped++;
                return Task.CompletedTask;
            },
            (_, _, _) =>
            {
                writes++;
                return Task.CompletedTask;
            });

        await ProcessingBackgroundService.RunOnceAsync(
            NullLogger<ProcessingBackgroundService>.Instance,
            state,
            operations,
            cts.Token);

        Assert.AreEqual(0, skipped);
        Assert.AreEqual(0, writes);
        Assert.AreEqual(0, state.ErrorsThisRun);
        Assert.AreEqual(0, state.SkippedThisRun);
        Assert.IsFalse(state.IsRunning);
        Assert.IsNull(state.CurrentActivity);
        Assert.IsNull(state.LastError);
        Assert.IsFalse(state.GetRecentLog().Any(line => line.Contains("Fatal:", StringComparison.Ordinal)));
        Assert.IsNotNull(state.LastRunCompleted);
        AssertLogOrder(state, "Run cancelled.", "Run complete. Processed=0 Skipped=0 Errors=0");
    }

    [TestMethod]
    public async Task RunOnceAsync_UnrelatedGeodataCancellation_IsAnAssetFailureNotRunCancellation()
    {
        var state = new ProcessingState();
        var asset = new AssetRecord(Guid.NewGuid(), 1, 2, DateTime.UtcNow);
        var operations = new ProcessingBackgroundService.ProcessingOperations(
            _ => Task.FromResult(1L),
            () => Task.FromResult(new AppConfig { Processing = new ProcessingConfig { BatchDelayMs = 0 } }),
            () => Task.FromResult(new HashSet<Guid>()),
            (cursor, _, _) => Task.FromResult(cursor == AssetCursor.Initial ? new List<AssetRecord> { asset } : []),
            (_, _, _, _, _) => throw new OperationCanceledException("foreign cancellation"),
            (_, _, _, _) => throw UnexpectedOperation("airport lookup"),
            _ => throw UnexpectedOperation("skipped-record write"),
            (_, _, _) => throw UnexpectedOperation("location write"));

        await ProcessingBackgroundService.RunOnceAsync(
            NullLogger<ProcessingBackgroundService>.Instance,
            state,
            operations,
            CancellationToken.None);

        Assert.AreEqual(1, state.ErrorsThisRun);
        Assert.AreEqual(0, state.SkippedThisRun);
        Assert.IsFalse(state.GetRecentLog().Any(line => line.EndsWith("Run cancelled.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RunOnceAsync_GeodataOutOfMemoryEscapesAssetBoundaryAndProducesFailedRun()
    {
        var state = new ProcessingState();
        var asset = new AssetRecord(Guid.NewGuid(), 1, 2, DateTime.UtcNow);
        var operations = new ProcessingBackgroundService.ProcessingOperations(
            _ => Task.FromResult(1L),
            () => Task.FromResult(new AppConfig { Processing = new ProcessingConfig { BatchDelayMs = 0 } }),
            () => Task.FromResult(new HashSet<Guid>()),
            (cursor, _, _) => Task.FromResult(cursor == AssetCursor.Initial ? new List<AssetRecord> { asset } : []),
            (_, _, _, _, _) => throw new OutOfMemoryException("controlled geodata memory failure"),
            (_, _, _, _) => throw UnexpectedOperation("airport lookup"),
            _ => throw UnexpectedOperation("skipped-record write"),
            (_, _, _) => throw UnexpectedOperation("location write"));

        await ProcessingBackgroundService.RunOnceAsync(
            NullLogger<ProcessingBackgroundService>.Instance, state, operations, CancellationToken.None);

        Assert.AreEqual(1, state.ErrorsThisRun);
        Assert.AreEqual(0, state.SkippedThisRun);
        Assert.IsFalse(state.IsRunning);
        Assert.AreEqual("Fatal: controlled geodata memory failure", state.LastError);
        Assert.IsTrue(state.GetRecentLog().Any(line => line.EndsWith("Run complete. Processed=0 Skipped=0 Errors=1", StringComparison.Ordinal)));
    }

    private static async Task<PassPlan> StartActiveManualPassAsync(
        ProcessingFixture fixture,
        ProcessingBackgroundService service,
        PassOutcome outcome)
    {
        var pass = fixture.QueuePass(outcome);
        await service.TriggerRunAsync();
        await ReachActiveAsync(fixture, pass);
        return pass;
    }

    private static async Task ReachActiveAsync(ProcessingFixture fixture, PassPlan pass)
    {
        await pass.PassEntered.Task.WaitAsync(GateTimeout);
        pass.CountReleased.SetResult(1);
        await pass.Active.Task.WaitAsync(GateTimeout);
        await pass.BatchEntered.Task.WaitAsync(GateTimeout);
        Assert.IsTrue(fixture.State.IsRunning);
        Assert.IsNotNull(fixture.State.LastRunStarted);
    }

    private static async Task CompletePassAsync(ProcessingBackgroundService service, PassPlan pass)
    {
        switch (pass.Outcome)
        {
            case PassOutcome.Success:
            case PassOutcome.Failure:
                pass.BatchReleased.SetResult(true);
                break;
            case PassOutcome.Cancellation:
                service.CancelRun();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(pass));
        }

        await pass.Terminal.Task.WaitAsync(GateTimeout);
        await service.WaitForManualAdmissionAsync();
    }

    private static void AssertTerminalSuccess(ProcessingState state)
    {
        Assert.IsFalse(state.IsRunning);
        Assert.IsNotNull(state.LastRunCompleted);
        Assert.IsNull(state.LastError);
        AssertLogOrder(state, "Run complete. Processed=0 Skipped=0 Errors=0");
    }

    private static AssertFailedException UnexpectedOperation(string operation)
    {
        return new AssertFailedException($"Unexpected {operation} after a zero eligibility count.");
    }

    private static void AssertLogOrder(ProcessingState state, params string[] messages)
    {
        var previousIndex = -1;
        foreach (var message in messages)
        {
            var index = FindMessageIndex(state.GetRecentLog(), message);
            Assert.IsTrue(index >= 0, $"The message '{message}' was not logged.");
            Assert.IsTrue(index > previousIndex, $"The message '{message}' was logged out of order.");
            previousIndex = index;
        }
    }

    private static int CountMessage(IReadOnlyList<string> log, string message)
    {
        return log.Count(line => line.EndsWith(message, StringComparison.Ordinal));
    }

    private static int FindMessageIndex(IReadOnlyList<string> log, string message)
    {
        for (var index = 0; index < log.Count; index++)
        {
            if (log[index].EndsWith(message, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    public enum PassOutcome
    {
        Success,
        Cancellation,
        Failure
    }

    private sealed class ProcessingFixture
    {
        private readonly Queue<PassPlan> _plans = new();
        private PassPlan? _currentPlan;
        private int _notificationCount;

        public ProcessingFixture()
        {
            State.OnChanged += ObserveState;
        }

        public ProcessingState State { get; } = new();
        public int PassCount { get; private set; }
        public int NotificationCount => Volatile.Read(ref _notificationCount);

        public ProcessingBackgroundService CreateService()
        {
            return new ProcessingBackgroundService(
                NullLogger<ProcessingBackgroundService>.Instance,
                State,
                new ProcessingBackgroundService.ProcessingOperations(
                    GetUnprocessedCountAsync,
                    () => Task.FromResult(new AppConfig
                    {
                        Processing = new ProcessingConfig { BatchDelayMs = 0 }
                    }),
                    () => Task.FromResult(new HashSet<Guid>()),
                    GetUnprocessedBatchAsync,
                    (_, _, _, _, _) => throw UnexpectedOperation("administrative-area resolution"),
                    (_, _, _, _) => throw UnexpectedOperation("airport lookup"),
                    _ => throw UnexpectedOperation("skipped-record write"),
                    (_, _, _) => throw UnexpectedOperation("location write")));
        }

        public PassPlan QueuePass(PassOutcome outcome)
        {
            var plan = new PassPlan(outcome);
            _plans.Enqueue(plan);
            return plan;
        }

        private Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _currentPlan = _plans.Dequeue();
            PassCount++;
            _currentPlan.PassEntered.SetResult(true);
            return _currentPlan.CountReleased.Task;
        }

        private async Task<List<AssetRecord>> GetUnprocessedBatchAsync(
            AssetCursor cursor,
            int batchSize,
            CancellationToken cancellationToken)
        {
            var plan = _currentPlan!;
            plan.Active.SetResult(true);
            plan.BatchEntered.SetResult(true);
            await plan.BatchReleased.Task.WaitAsync(GateTimeout, cancellationToken);
            if (plan.Outcome == PassOutcome.Failure)
            {
                throw new InvalidOperationException("injected pass failure");
            }

            return [];
        }

        private void ObserveState()
        {
            Interlocked.Increment(ref _notificationCount);

            var log = State.GetRecentLog();
            if (_currentPlan is not null
                && !State.IsRunning
                && State.LastRunCompleted is not null
                && log.Count > 0
                && log[^1].Contains("Run complete.", StringComparison.Ordinal))
            {
                _currentPlan.Terminal.TrySetResult(true);
            }
        }
    }

    private sealed class PassPlan(PassOutcome outcome)
    {
        public PassOutcome Outcome { get; } = outcome;
        public TaskCompletionSource<bool> PassEntered { get; } = NewSignal();
        public TaskCompletionSource<long> CountReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Active { get; } = NewSignal();
        public TaskCompletionSource<bool> BatchEntered { get; } = NewSignal();
        public TaskCompletionSource<bool> BatchReleased { get; } = NewSignal();
        public TaskCompletionSource<bool> Terminal { get; } = NewSignal();

        private static TaskCompletionSource<bool> NewSignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
