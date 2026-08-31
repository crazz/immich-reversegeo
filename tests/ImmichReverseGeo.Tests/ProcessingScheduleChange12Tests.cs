using System.Collections.Concurrent;
using System.Reflection;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public sealed class ProcessingScheduleChange12Tests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Start = new(2026, 8, 30, 12, 34, 0, TimeSpan.Zero);

    [TestMethod]
    public void Calculate_ReturnsExactImmutablePlanShapesAndDurations()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(1), ((DisabledRetry)ProcessingScheduleCalculator.Calculate(false, "ignored", Start)).RetryAfter);
        Assert.AreEqual(TimeSpan.FromMinutes(5), ((InvalidRetry)ProcessingScheduleCalculator.Calculate(true, "invalid", Start)).RetryAfter);
        var due = (Due)ProcessingScheduleCalculator.Calculate(true, "0 * * * *", Start);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 30, 13, 0, 0, TimeSpan.Zero), due.DueAtUtc);
        Assert.AreEqual(TimeSpan.Zero, due.DueAtUtc.Offset);
        Assert.IsTrue(typeof(ProcessingSchedulePlan).Assembly.GetTypes()
            .Where(type => type == typeof(DisabledRetry) || type == typeof(InvalidRetry) || type == typeof(Due))
            .SelectMany(type => type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            .All(field => field.IsInitOnly));
    }

    [TestMethod]
    [DataRow("0 * * * *", "2026-08-30T13:00:00+00:00")]
    [DataRow("30 2 * * *", "2026-08-31T02:30:00+00:00")]
    [DataRow("15 4 * * 1", "2026-08-31T04:15:00+00:00")]
    [DataRow("*/15 9-10 * * 1-5", "2026-08-31T09:00:00+00:00")]
    public void Calculate_FixedUtcExpressions_UseStandardStrictlyFutureUtcSemantics(string cron, string expected)
    {
        var due = (Due)ProcessingScheduleCalculator.Calculate(true, cron, Start);
        Assert.AreEqual(DateTimeOffset.Parse(expected), due.DueAtUtc);
        Assert.AreEqual(TimeSpan.Zero, due.DueAtUtc.Offset);
    }

    [TestMethod]
    public void Calculate_HourlyDailyWeeklyCustomAndInvalidMatrix_IsUtcAndHostZoneIndependent()
    {
        var cases = new[]
        {
            ("0 * * * *", new DateTimeOffset(2026, 8, 30, 13, 0, 0, TimeSpan.Zero)),
            ("30 2 * * *", new DateTimeOffset(2026, 8, 31, 2, 30, 0, TimeSpan.Zero)),
            ("15 4 * * 1", new DateTimeOffset(2026, 8, 31, 4, 15, 0, TimeSpan.Zero)),
            ("*/15 9-10 * * 1-5", new DateTimeOffset(2026, 8, 31, 9, 0, 0, TimeSpan.Zero))
        };
        foreach (var (cron, expected) in cases)
        {
            var due = (Due)ProcessingScheduleCalculator.Calculate(true, cron, Start);
            Assert.AreEqual(expected, due.DueAtUtc);
            Assert.AreEqual(TimeSpan.Zero, due.DueAtUtc.Offset);
        }
        Assert.IsInstanceOfType<InvalidRetry>(ProcessingScheduleCalculator.Calculate(true, "0 0 0 * * *", Start));
        var matching = new DateTimeOffset(2026, 8, 30, 13, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 30, 14, 0, 0, TimeSpan.Zero), ((Due)ProcessingScheduleCalculator.Calculate(true, "0 * * * *", matching)).DueAtUtc);
    }

    [TestMethod]
    public void Calculate_NonZeroOffsetInput_IsRejectedAndUtcInputIgnoresLocalDst()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ProcessingScheduleCalculator.Calculate(true, "0 * * * *", Start.ToOffset(TimeSpan.FromHours(2))));
        var winter = (Due)ProcessingScheduleCalculator.Calculate(true, "0 2 * * *", new DateTimeOffset(2026, 3, 29, 1, 59, 0, TimeSpan.Zero));
        Assert.AreEqual(new DateTimeOffset(2026, 3, 29, 2, 0, 0, TimeSpan.Zero), winter.DueAtUtc);
        Assert.AreEqual(TimeSpan.Zero, winter.DueAtUtc.Offset);
    }

    [TestMethod]
    public void Calculate_ExpressionWithoutNextOccurrence_ReturnsFiveMinuteInvalidRetry()
    {
        var plan = ProcessingScheduleCalculator.Calculate(true, "0 0 29 2 *", new DateTimeOffset(9997, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.IsInstanceOfType<InvalidRetry>(plan);
        Assert.AreEqual(TimeSpan.FromMinutes(5), ((InvalidRetry)plan).RetryAfter);
    }

    [TestMethod]
    public async Task ExecuteAsync_DisabledAndInvalidRetryWaitExactDurationsBeforeFreshRead()
    {
        await AssertRetryAsync(false, "0 * * * *", TimeSpan.FromMinutes(1));
        await AssertRetryAsync(true, "not cron", TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    public async Task ExecuteAsync_ShutdownDuringDisabledOrInvalidRetryProducesNoTriggerOrError()
    {
        await AssertRetryCancellationAsync(false, "ignored");
        await AssertRetryCancellationAsync(true, "bad cron");
    }

    [TestMethod]
    public async Task ExecuteAsync_PositiveDue_LogsExactUtcLineThenWaitsAndTriggersOnce()
    {
        var fixture = LoopFixture.Create(Start, Enabled("0 * * * *"));
        await fixture.StartAsync();
        await fixture.Time.TimerCreated(0).WaitAsync(Bound);
        CollectionAssert.AreEqual(new[]
        {
            "Service started. Waiting for next scheduled run.",
            "Next run scheduled at 2026-08-30 13:00:00Z"
        }, Messages(fixture.State));
        Assert.AreEqual(0, fixture.Executor.CallCount);

        fixture.Time.Advance(TimeSpan.FromMinutes(25));
        Assert.AreEqual(0, fixture.Executor.CallCount);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        var invocation = await fixture.Executor.Entered.Task.WaitAsync(Bound);
        Assert.AreEqual(ProcessingRunTrigger.Scheduled, invocation.Request.Trigger);
        Assert.AreEqual(1, fixture.Executor.CallCount);
        fixture.Executor.Release.TrySetResult();
        await fixture.Config.Read(1).WaitAsync(Bound);
        Assert.AreEqual(1, fixture.Executor.CallCount);
        await fixture.StopAsync();
    }

    [TestMethod]
    public async Task ExecuteAsync_PositiveWaitPublishesOneExactUtcNextRunLineBeforeDelay()
    {
        var fixture = LoopFixture.Create(Start, Enabled("0 * * * *"));
        await fixture.StartAsync();
        await fixture.Time.TimerCreated(0).WaitAsync(Bound);
        Assert.AreEqual(1, Messages(fixture.State).Count(message => message == "Next run scheduled at 2026-08-30 13:00:00Z"));
        Assert.AreEqual(0, fixture.Executor.CallCount);
        await fixture.StopAsync();
    }

    [TestMethod]
    public async Task ExecuteAsync_FutureAlreadyDueAndCancelledDuePlansHaveExactSingleTriggerSemantics()
    {
        var future = LoopFixture.Create(Start, Enabled("0 * * * *"));
        await future.StartAsync();
        await future.Time.TimerCreated(0).WaitAsync(Bound);
        future.Time.Advance(TimeSpan.FromMinutes(26));
        await future.Executor.Entered.Task.WaitAsync(Bound);
        Assert.AreEqual(1, future.Executor.CallCount);
        future.Executor.Release.TrySetResult();
        await future.Config.Read(1).WaitAsync(Bound);
        Assert.AreEqual(1, future.Executor.CallCount);
        await future.StopAsync();

        var alreadyDueTime = new ManualTimeProvider(Start);
        alreadyDueTime.AdvanceOnGetUtcNowCall = (2, TimeSpan.FromMinutes(27));
        var alreadyDue = LoopFixture.Create(alreadyDueTime, Enabled("0 * * * *"));
        await alreadyDue.StartAsync();
        await alreadyDue.Executor.Entered.Task.WaitAsync(Bound);
        Assert.AreEqual(1, alreadyDue.Executor.CallCount);
        Assert.AreEqual(0, Messages(alreadyDue.State).Count(message => message.StartsWith("Next run scheduled", StringComparison.Ordinal)));
        alreadyDue.Executor.Release.TrySetResult();
        await alreadyDue.Config.Read(1).WaitAsync(Bound);
        await alreadyDue.StopAsync();

        var cancelled = LoopFixture.Create(Start, Enabled("0 * * * *"));
        await cancelled.StartAsync();
        await cancelled.Time.TimerCreated(0).WaitAsync(Bound);
        await cancelled.StopAsync();
        cancelled.Time.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual(0, cancelled.Executor.CallCount);
    }

    [TestMethod]
    public async Task ExecuteAsync_ConfigMutationDuringEveryWaitAppliesOnlyNextIteration()
    {
        await AssertPinnedConfigAsync(Disabled(), TimeSpan.FromMinutes(1));
        await AssertPinnedConfigAsync(Enabled("bad"), TimeSpan.FromMinutes(5));
        await AssertPinnedConfigAsync(Enabled("0 * * * *"), TimeSpan.FromMinutes(26), releaseExecutor: true);
    }


    [TestMethod]
    public async Task ExecuteAsync_DueAccepted_DoesNotReadConfigAgainUntilTerminal()
    {
        var fixture = LoopFixture.Create(Start, Enabled("0 * * * *"));
        await fixture.StartAsync();
        await fixture.Time.TimerCreated(0).WaitAsync(Bound);
        fixture.Time.Advance(TimeSpan.FromMinutes(26));
        await fixture.Executor.Entered.Task.WaitAsync(Bound);
        Assert.AreEqual(1, fixture.Config.CallCount);
        Assert.IsFalse(fixture.Config.Read(1).IsCompleted);
        fixture.Executor.Release.TrySetResult();
        await fixture.Config.Read(1).WaitAsync(Bound);
        Assert.AreEqual(2, fixture.Config.CallCount);
        await fixture.StopAsync();
    }

    [TestMethod]
    public async Task ExecuteAsync_AcceptedTriggerBlocksReevaluationAndPropagatesShutdownToken()
    {
        var fixture = LoopFixture.Create(Start, Enabled("0 * * * *"));
        using var stopping = new CancellationTokenSource();
        var loop = fixture.Service.RunLoopAsync(stopping.Token);
        await fixture.Time.TimerCreated(0).WaitAsync(Bound);
        fixture.Time.Advance(TimeSpan.FromMinutes(26));
        var invocation = await fixture.Executor.Entered.Task.WaitAsync(Bound);
        Assert.IsTrue(invocation.Token.CanBeCanceled);
        Assert.IsFalse(invocation.Token.IsCancellationRequested);
        await fixture.Executor.TokenArmed.Task.WaitAsync(Bound);
        Assert.AreEqual(1, fixture.Config.CallCount);
        stopping.Cancel();
        Assert.IsTrue(invocation.Token.IsCancellationRequested);
        fixture.Executor.Release.TrySetResult();
        await loop.WaitAsync(Bound);
        Assert.AreEqual(1, fixture.Config.CallCount);
        Assert.AreEqual(1, fixture.Executor.CallCount);
    }


    [TestMethod]
    public async Task ExecuteAsync_StartupInitializationCompletesBeforeServiceLogConfigAndTrigger()
    {
        var initializationEntered = Signal();
        var initializationRelease = Signal();
        async Task Initialize()
        {
            initializationEntered.TrySetResult();
            await initializationRelease.Task.WaitAsync(Bound);
        }

        var fixture = LoopFixture.Create(Start, Disabled(), Initialize);
        await fixture.StartAsync();
        await initializationEntered.Task.WaitAsync(Bound);
        Assert.AreEqual(0, fixture.Config.CallCount);
        Assert.AreEqual(0, Messages(fixture.State).Length);
        Assert.AreEqual(0, fixture.Executor.CallCount);
        initializationRelease.TrySetResult();
        await fixture.Config.Read(0).WaitAsync(Bound);
        CollectionAssert.AreEqual(new[] { "Service started. Waiting for next scheduled run." }, Messages(fixture.State));
        await fixture.StopAsync();
    }




    [TestMethod]
    public async Task TryTriggerScheduledAsync_RejectionAndAcceptancePreserveExactAdmissionSemantics()
    {
        var fixture = LoopFixture.Create(Start, Disabled());
        await fixture.Service.TriggerRunAsync();
        await fixture.Executor.Entered.Task.WaitAsync(Bound);
        var rejected = await fixture.Service.TriggerScheduledAsync(CancellationToken.None).WaitAsync(Bound);
        Assert.AreEqual(ScheduledTriggerResult.RejectedAlreadyRunning, rejected);
        Assert.AreEqual(1, fixture.Executor.CallCount);
        Assert.AreEqual(1, Messages(fixture.State).Count(message => message == "Scheduled run skipped because a processing pass is already in progress."));
        fixture.Executor.Release.TrySetResult();
        await fixture.Service.WaitForManualAdmissionAsync().WaitAsync(Bound);

        fixture.Executor.Reset();
        var accepted = fixture.Service.TriggerScheduledAsync(CancellationToken.None);
        var invocation = await fixture.Executor.Entered.Task.WaitAsync(Bound);
        Assert.AreEqual(ProcessingRunTrigger.Scheduled, invocation.Request.Trigger);
        Assert.IsFalse(accepted.IsCompleted);
        fixture.Executor.Release.TrySetResult();
        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await accepted.WaitAsync(Bound));
        Assert.AreEqual(1, fixture.Executor.CallCount);
    }

    [TestMethod]
    public async Task ExecuteAsync_DueRejected_AppendsExactContentionLineAndDoesNotExecute()
    {
        var fixture = LoopFixture.Create(Start, Enabled("0 * * * *"));
        await fixture.Service.TriggerRunAsync();
        await fixture.Executor.Entered.Task.WaitAsync(Bound);
        await fixture.StartAsync();
        await fixture.Time.TimerCreated(0).WaitAsync(Bound);
        fixture.Time.Advance(TimeSpan.FromMinutes(26));
        await fixture.Config.Read(1).WaitAsync(Bound);
        Assert.AreEqual(1, fixture.Executor.CallCount);
        Assert.AreEqual(1, Messages(fixture.State).Count(message => message == "Scheduled run skipped because a processing pass is already in progress."));
        fixture.Executor.Release.TrySetResult();
        await fixture.Service.WaitForManualAdmissionAsync().WaitAsync(Bound);
        await fixture.StopAsync();
    }

    [TestMethod]
    public async Task ScheduledTriggerBoundary_ReportsRejectedOrAcceptedAfterTerminalWithExactToken()
    {
        var fixture = LoopFixture.Create(Start, Disabled());
        using var cancellation = new CancellationTokenSource();
        var accepted = fixture.Service.TriggerScheduledAsync(cancellation.Token);
        var invocation = await fixture.Executor.Entered.Task.WaitAsync(Bound);
        Assert.IsTrue(invocation.Token.CanBeCanceled);
        Assert.IsFalse(invocation.Token.IsCancellationRequested);
        Assert.AreEqual(ProcessingRunTrigger.Scheduled, invocation.Request.Trigger);
        Assert.IsFalse(accepted.IsCompleted);
        fixture.Executor.Release.TrySetResult();
        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await accepted.WaitAsync(Bound));
    }









    private static async Task AssertRetryAsync(bool enabled, string cron, TimeSpan retry)
    {
        var fixture = LoopFixture.Create(Start, new AppConfig { Schedule = new ScheduleConfig { Enabled = enabled, Cron = cron } });
        await fixture.StartAsync();
        Assert.AreEqual(retry, await fixture.Time.TimerCreated(0).WaitAsync(Bound));
        Assert.AreEqual(1, fixture.Config.CallCount);
        Assert.AreEqual(0, fixture.Executor.CallCount);
        Assert.AreEqual(0, Messages(fixture.State).Count(message => message.StartsWith("Next run scheduled", StringComparison.Ordinal)));
        fixture.Time.Advance(retry - TimeSpan.FromTicks(1));
        Assert.AreEqual(1, fixture.Config.CallCount);
        fixture.Time.Advance(TimeSpan.FromTicks(1));
        await fixture.Config.Read(1).WaitAsync(Bound);
        Assert.AreEqual(2, fixture.Config.CallCount);
        Assert.AreEqual(0, fixture.Executor.CallCount);
        await fixture.StopAsync();
    }

    private static async Task AssertRetryCancellationAsync(bool enabled, string cron)
    {
        var fixture = LoopFixture.Create(Start, new AppConfig { Schedule = new ScheduleConfig { Enabled = enabled, Cron = cron } });
        await fixture.StartAsync();
        await fixture.Time.TimerCreated(0).WaitAsync(Bound);
        await fixture.StopAsync();
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.AreEqual(0, fixture.Executor.CallCount);
        Assert.AreEqual(1, fixture.Config.CallCount);
    }

    private static async Task AssertPinnedConfigAsync(AppConfig initial, TimeSpan advance, bool releaseExecutor = false)
    {
        var fixture = LoopFixture.Create(Start, initial);
        await fixture.StartAsync();
        await fixture.Time.TimerCreated(0).WaitAsync(Bound);
        fixture.Config.Current = Disabled();
        Assert.AreEqual(1, fixture.Config.CallCount);
        fixture.Time.Advance(advance);
        if (releaseExecutor)
        {
            await fixture.Executor.Entered.Task.WaitAsync(Bound);
            Assert.AreEqual(1, fixture.Config.CallCount);
            fixture.Executor.Release.TrySetResult();
        }
        await fixture.Config.Read(1).WaitAsync(Bound);
        Assert.AreEqual(2, fixture.Config.CallCount);
        await fixture.StopAsync();
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ProcessingBackgroundService>>(NullLogger<ProcessingBackgroundService>.Instance);
        services.AddSingleton((ConfigService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ConfigService)));
        services.AddSingleton((AdministrativeAreaResolverService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AdministrativeAreaResolverService)));
        services.AddSingleton((ImmichDbRepository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ImmichDbRepository)));
        services.AddSingleton((ImmichReverseGeo.Overture.Services.OverturePlacesService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ImmichReverseGeo.Overture.Services.OverturePlacesService)));
        services.AddSingleton((SkippedAssetsRepository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SkippedAssetsRepository)));
        services.AddProcessingServices();
        return services.BuildServiceProvider();
    }

    private static string[] Messages(ProcessingState state) => state.GetRecentLog().Select(line => line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..]).ToArray();
    private static AppConfig Enabled(string cron) => new() { Schedule = new ScheduleConfig { Enabled = true, Cron = cron } };
    private static AppConfig Disabled() => new() { Schedule = new ScheduleConfig { Enabled = false, Cron = "0 * * * *" } };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);


    private sealed class LoopFixture
    {
        private LoopFixture(ManualTimeProvider time, AppConfig config, Func<Task>? initialize)
        {
            Time = time;
            Config = new RecordingConfiguration(config);
            Reporter = new ProcessingStateEventReporter(State);
            Service = new TestHost(State, Reporter, Executor, Config, initialize ?? (() => Task.CompletedTask), Time);
        }

        public ProcessingState State { get; } = new();
        public ProcessingStateEventReporter Reporter { get; }
        public RecordingExecutor Executor { get; } = new();
        public RecordingConfiguration Config { get; }
        public ManualTimeProvider Time { get; }
        public TestHost Service { get; }

        public static LoopFixture Create(DateTimeOffset now, AppConfig config, Func<Task>? initialize = null) => new(new ManualTimeProvider(now), config, initialize);
        public static LoopFixture Create(ManualTimeProvider time, AppConfig config, Func<Task>? initialize = null) => new(time, config, initialize);
        public Task StartAsync() => Service.StartAsync(CancellationToken.None).WaitAsync(Bound);
        public Task StopAsync() => Service.StopAsync(CancellationToken.None).WaitAsync(Bound);
    }

    private sealed class TestHost : ProcessingBackgroundService
    {
        private readonly ProcessingRunCoordinator _coordinator;

        public TestHost(
            ProcessingState state,
            ProcessingStateEventReporter reporter,
            IProcessingRunExecutor executor,
            IProcessingScheduleConfiguration configuration,
            Func<Task> initialize,
            TimeProvider timeProvider)
            : this(state, configuration, initialize, timeProvider, CreateCoordinator(state, reporter, executor))
        {
        }

        private TestHost(
            ProcessingState state,
            IProcessingScheduleConfiguration configuration,
            Func<Task> initialize,
            TimeProvider timeProvider,
            ProcessingRunCoordinator coordinator)
            : base(NullLogger<ProcessingBackgroundService>.Instance, state, configuration, initialize, timeProvider, coordinator)
        {
            _coordinator = coordinator;
        }

        public Task RunLoopAsync(CancellationToken stoppingToken) => ExecuteAsync(stoppingToken);
        public async Task TriggerRunAsync() => await _coordinator.TriggerManualAsync().ConfigureAwait(false);
        public Task WaitForManualAdmissionAsync() => _coordinator.WaitForActiveRunAsync();

        private static ProcessingRunCoordinator CreateCoordinator(
            ProcessingState state,
            ProcessingStateEventReporter reporter,
            IProcessingRunExecutor executor)
        {
            return new ProcessingRunCoordinator(
                state,
                reporter,
                executor,
                NullLogger<ProcessingRunCoordinator>.Instance,
                Guid.NewGuid);
        }
    }

    private sealed class RecordingConfiguration(AppConfig current) : IProcessingScheduleConfiguration
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _reads = new();
        private int _calls;
        public AppConfig Current { get; set; } = current;
        public int CallCount => Volatile.Read(ref _calls);
        public Task Read(int index) => _reads.GetOrAdd(index, _ => Signal()).Task;
        public Task<ProcessingScheduleSnapshot> GetSnapshotAsync()
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            _reads.GetOrAdd(index, _ => Signal()).TrySetResult();
            var value = Current;
            return Task.FromResult(new ProcessingScheduleSnapshot(value.Schedule.Enabled, value.Schedule.Cron));
        }
    }

    private sealed class RecordingExecutor : IProcessingRunExecutor
    {
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);
        public TaskCompletionSource<Invocation> Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; private set; } = Signal();
        public TaskCompletionSource TokenArmed { get; private set; } = Signal();
        public TaskCompletionSource CancellationObserved { get; private set; } = Signal();

        public async Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult(new Invocation(request, reporter, cancellationToken));
            using var registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            TokenArmed.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(Bound, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            var now = Start;
            var session = await reporter.OpenRunAsync(request, now, CancellationToken.None).AsTask().WaitAsync(Bound);
            await session.DetermineEligibilityAsync(0, CancellationToken.None).AsTask().WaitAsync(Bound);
            var result = new ProcessingRunResult(request, now, now, 0, 0, 0, 0, cancellationToken.IsCancellationRequested ? ProcessingRunOutcome.Cancelled : ProcessingRunOutcome.Completed, null);
            await session.FinishAsync(result).AsTask().WaitAsync(Bound);
            return result;
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _calls, 0);
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = Signal();
            TokenArmed = Signal();
            CancellationObserved = Signal();
        }
    }

    private sealed class ZeroExecutor : IProcessingRunExecutor
    {
        public int CallCount { get; private set; }
        public async Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            CallCount++;
            var session = await reporter.OpenRunAsync(request, Start, CancellationToken.None).AsTask().WaitAsync(Bound);
            await session.DetermineEligibilityAsync(0, cancellationToken).AsTask().WaitAsync(Bound);
            var result = new ProcessingRunResult(request, Start, Start, 0, 0, 0, 0, ProcessingRunOutcome.Completed, null);
            await session.FinishAsync(result).AsTask().WaitAsync(Bound);
            return result;
        }
    }

    private sealed record Invocation(ProcessingRunRequest Request, IProcessingEventReporter Reporter, CancellationToken Token);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly ConcurrentDictionary<int, TaskCompletionSource<TimeSpan>> _created = new();
        private DateTimeOffset _now;
        private int _timerCount;
        private int _nowCalls;

        public ManualTimeProvider(DateTimeOffset now) => _now = now;
        public (int Call, TimeSpan Advance)? AdvanceOnGetUtcNowCall { get; set; }
        public Task<TimeSpan> TimerCreated(int index) => _created.GetOrAdd(index, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                var call = ++_nowCalls;
                if (AdvanceOnGetUtcNowCall is { } advance && advance.Call == call)
                {
                    _now += advance.Advance;
                }
                return _now;
            }
        }

        public override long GetTimestamp() => GetUtcNow().UtcTicks;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer;
            int index;
            lock (_sync)
            {
                timer = new ManualTimer(this, callback, state, _now + dueTime, period);
                _timers.Add(timer);
                index = _timerCount++;
            }
            _created.GetOrAdd(index, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(dueTime);
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            ManualTimer[] due;
            lock (_sync)
            {
                _now += amount;
                due = _timers.Where(timer => timer.IsDue(_now)).ToArray();
                foreach (var timer in due)
                {
                    timer.MarkFired(_now);
                }
            }
            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_sync)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset due, TimeSpan period) : ITimer
        {
            private bool _disposed;
            private DateTimeOffset _due = due;
            public bool IsDue(DateTimeOffset now) => !_disposed && _due <= now;
            public void MarkFired(DateTimeOffset now)
            {
                if (period == Timeout.InfiniteTimeSpan)
                {
                    _disposed = true;
                    owner.Remove(this);
                }
                else
                {
                    _due = now + period;
                }
            }
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan newPeriod) => throw new NotSupportedException();
            public void Dispose() { _disposed = true; owner.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
