using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProcessingScheduleChange12AuditTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 34, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ExecuteAsync_ExactStoppingTokenCancellationIsSuppressedAtConfigBoundary()
    {
        using var stopping = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        var config = new GatedFailureConfiguration(entered, release, () => new OperationCanceledException(stopping.Token));
        var fixture = HostFixture.Create(config);
        var execution = fixture.Host.RunLoopAsync(stopping.Token);
        await entered.Task.WaitAsync(Bound);
        stopping.Cancel();
        release.TrySetResult();
        await execution.WaitAsync(Bound);
        Assert.AreEqual(0, fixture.Executor.CallCount);
        AssertNoOrdinaryErrorOrTrigger(fixture);
    }

    [TestMethod]
    public async Task ExecuteAsync_ForeignConfigCancellationPropagatesExactReferenceWhenShutdownRaces()
    {
        using var stopping = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        var foreign = new OperationCanceledException("foreign config cancellation");
        var fixture = HostFixture.Create(new GatedFailureConfiguration(entered, release, () => foreign));
        var execution = fixture.Host.RunLoopAsync(stopping.Token);
        await entered.Task.WaitAsync(Bound);
        stopping.Cancel();
        release.TrySetResult();
        var actual = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => execution.WaitAsync(Bound));
        Assert.AreSame(foreign, actual);
        Assert.AreEqual(0, fixture.Executor.CallCount);
        AssertNoOrdinaryErrorOrTrigger(fixture);
    }

    [TestMethod]
    public async Task ExecuteAsync_ForeignDownstreamCancellationPropagatesExactReferenceDuringShutdownWithoutRecursiveTrigger()
    {
        using var stopping = new CancellationTokenSource();
        var foreign = new OperationCanceledException("foreign downstream cancellation");
        var executor = new GatedThrowingExecutor(foreign);
        var time = new ControlledTimeProvider(Now, TimeZoneInfo.Utc) { AdvanceOnUtcCall = (2, TimeSpan.FromMinutes(27)) };
        var fixture = HostFixture.Create(new SnapshotConfiguration(Enabled("0 * * * *")), executor, time);
        var execution = fixture.Host.RunLoopAsync(stopping.Token);
        var invocation = await executor.Entered.Task.WaitAsync(Bound);
        Assert.AreEqual(stopping.Token, invocation.Token);
        stopping.Cancel();
        executor.Release.TrySetResult();
        var actual = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => execution.WaitAsync(Bound));
        Assert.AreSame(foreign, actual);
        Assert.AreEqual(1, executor.CallCount);
        Assert.AreEqual(1, fixture.Configuration.CallCount);
        Assert.AreEqual(1, executor.Invocations.Count);
        Assert.AreSame(invocation.Request, executor.Invocations.Single().Request);
        Assert.AreEqual(ProcessingRunTrigger.Scheduled, invocation.Request.Trigger);
        Assert.AreEqual(1, fixture.Events.OfType<RunStarted>().Count());
        Assert.AreEqual(0, fixture.Events.OfType<RunFinished>().Count());
        Assert.AreEqual(0, Messages(fixture.State).Count(message => message.StartsWith("Next run scheduled", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ScheduleLoop_IdenticalUtcAcrossUtcAndDstLocalZonesProducesIdenticalExactDue()
    {
        var utc = await ObserveFirstDueAsync(TimeZoneInfo.Utc);
        var dstZone = TimeZoneInfo.CreateCustomTimeZone(
            "Controlled-DST",
            TimeSpan.FromHours(-5),
            "Controlled-DST",
            "Controlled-Standard",
            "Controlled-Daylight",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2026, 1, 1),
                new DateTime(2026, 12, 31),
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday))]);
        var dst = await ObserveFirstDueAsync(dstZone);
        Assert.AreEqual(utc.Due, dst.Due);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 30, 13, 0, 0, TimeSpan.Zero), utc.Due);
        Assert.AreEqual(TimeSpan.Zero, utc.Due.Offset);
        Assert.AreEqual(TimeSpan.Zero, dst.Due.Offset);
        Assert.AreEqual(TimeSpan.FromMinutes(26), utc.Delay);
        Assert.AreEqual(utc.Delay, dst.Delay);
        Assert.AreEqual(2, utc.UtcCalls);
        Assert.AreEqual(2, dst.UtcCalls);
    }

    [TestMethod]
    public async Task ScheduleLoop_DisabledSnapshotPinsExactCronUntilOneMinuteThenUsesEnabledReplacement()
    {
        var first = Disabled("disabled-cron-A");
        var second = new ProcessingScheduleSnapshot(true, "0 * * * *");
        var fixture = LoopProbe.Create(first);
        var loop = fixture.RunAsync();
        Assert.AreEqual(TimeSpan.FromMinutes(1), await fixture.Time.TimerCreated(0).WaitAsync(Bound));
        fixture.Configuration.Current = second;
        AssertSnapshots(fixture.Configuration, (false, "disabled-cron-A"));
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await fixture.Configuration.Read(1).WaitAsync(Bound);
        Assert.AreEqual(TimeSpan.FromMinutes(25), await fixture.Time.TimerCreated(1).WaitAsync(Bound));
        AssertSnapshots(fixture.Configuration, (false, "disabled-cron-A"), (true, "0 * * * *"));
        CollectionAssert.AreEqual(new[] { "Next run scheduled at 2026-08-30 13:00:00Z" }, fixture.Logs.ToArray());
        await fixture.CancelAndAwaitAsync(loop);
    }

    [TestMethod]
    public async Task ScheduleLoop_InvalidCronASnapshotPinsFiveMinutesThenUsesValidCronB()
    {
        var fixture = LoopProbe.Create(Enabled("invalid-A"));
        var loop = fixture.RunAsync();
        Assert.AreEqual(TimeSpan.FromMinutes(5), await fixture.Time.TimerCreated(0).WaitAsync(Bound));
        fixture.Configuration.Current = new ProcessingScheduleSnapshot(true, "30 * * * *");
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        await fixture.Configuration.Read(1).WaitAsync(Bound);
        Assert.AreEqual(TimeSpan.FromMinutes(51), await fixture.Time.TimerCreated(1).WaitAsync(Bound));
        AssertSnapshots(fixture.Configuration, (true, "invalid-A"), (true, "30 * * * *"));
        CollectionAssert.AreEqual(new[] { "Next run scheduled at 2026-08-30 13:30:00Z" }, fixture.Logs.ToArray());
        Assert.AreEqual(0, fixture.Trigger.CallCount);
        await fixture.CancelAndAwaitAsync(loop);
    }

    [TestMethod]
    public async Task ScheduleLoop_DueCronARemainsTriggerUntilDueThenCronBIsReadAndPlanned()
    {
        var fixture = LoopProbe.Create(Enabled("0 * * * *"));
        var loop = fixture.RunAsync();
        Assert.AreEqual(TimeSpan.FromMinutes(26), await fixture.Time.TimerCreated(0).WaitAsync(Bound));
        fixture.Configuration.Current = new ProcessingScheduleSnapshot(true, "30 * * * *");
        fixture.Time.Advance(TimeSpan.FromMinutes(26));
        var call = await fixture.Trigger.Entered(0).WaitAsync(Bound);
        Assert.AreEqual(fixture.Stopping.Token, call.Token);
        Assert.AreEqual(ScheduledTriggerResult.RejectedAlreadyRunning, call.PlannedResult);
        await fixture.Configuration.Read(1).WaitAsync(Bound);
        Assert.AreEqual(TimeSpan.FromMinutes(30), await fixture.Time.TimerCreated(1).WaitAsync(Bound));
        AssertSnapshots(fixture.Configuration, (true, "0 * * * *"), (true, "30 * * * *"));
        CollectionAssert.AreEqual(new[]
        {
            "Next run scheduled at 2026-08-30 13:00:00Z",
            "Next run scheduled at 2026-08-30 13:30:00Z"
        }, fixture.Logs.ToArray());
        Assert.AreEqual(1, fixture.Trigger.CallCount);
        await fixture.CancelAndAwaitAsync(loop);
    }

    [TestMethod]
    public async Task ScheduleLoop_AcceptedRunPinsCronAAndBlocksCronBUntilTerminal()
    {
        var trigger = new SubstituteTrigger(ScheduledTriggerResult.AcceptedAfterTerminal, gateTerminal: true);
        var fixture = LoopProbe.Create(Enabled("0 * * * *"), trigger: trigger);
        var loop = fixture.RunAsync();
        await fixture.Time.TimerCreated(0).WaitAsync(Bound);
        fixture.Configuration.Current = new ProcessingScheduleSnapshot(true, "30 * * * *");
        fixture.Time.Advance(TimeSpan.FromMinutes(26));
        var call = await trigger.Entered(0).WaitAsync(Bound);
        Assert.AreEqual(fixture.Stopping.Token, call.Token);
        Assert.AreEqual(1, fixture.Configuration.CallCount);
        Assert.IsFalse(fixture.Configuration.Read(1).IsCompleted);
        trigger.ReleaseTerminal.TrySetResult();
        await fixture.Configuration.Read(1).WaitAsync(Bound);
        Assert.AreEqual(TimeSpan.FromMinutes(30), await fixture.Time.TimerCreated(1).WaitAsync(Bound));
        AssertSnapshots(fixture.Configuration, (true, "0 * * * *"), (true, "30 * * * *"));
        Assert.AreEqual(1, trigger.CallCount);
        await fixture.CancelAndAwaitAsync(loop);
    }

    [TestMethod]
    public async Task ExecuteAsync_StartupCausalSequenceIsLoggerInitializeReadyUiThenFirstConfigWithNoTrigger()
    {
        var operations = new ConcurrentQueue<string>();
        var logger = new RecordingLogger<ProcessingBackgroundService>(operations);
        var initializerEntered = Signal();
        var initializerRelease = Signal();
        async Task Initialize()
        {
            operations.Enqueue("initializer:enter");
            initializerEntered.TrySetResult();
            await initializerRelease.Task.WaitAsync(Bound);
            operations.Enqueue("initializer:release");
        }
        var config = new SnapshotConfiguration(Disabled("startup-disabled"), operations);
        var fixture = HostFixture.Create(config, logger: logger, initialize: Initialize, operations: operations);
        fixture.State.OnChanged += () => RecordNewUi(fixture.State, operations, fixture.UiRecorded);
        using var stopping = new CancellationTokenSource();
        var execution = fixture.Host.RunLoopAsync(stopping.Token);
        await initializerEntered.Task.WaitAsync(Bound);
        CollectionAssert.AreEqual(new[] { "logger:ProcessingBackgroundService: initialising skipped-assets db", "initializer:enter" }, operations.ToArray());
        Assert.AreEqual(0, config.CallCount);
        Assert.AreEqual(0, fixture.Executor.CallCount);
        initializerRelease.TrySetResult();
        await config.Read(0).WaitAsync(Bound);
        await fixture.Time.TimerCreated(0).WaitAsync(Bound);
        var actual = operations.ToArray();
        CollectionAssert.AreEqual(new[]
        {
            "logger:ProcessingBackgroundService: initialising skipped-assets db",
            "initializer:enter",
            "initializer:release",
            "logger:ProcessingBackgroundService: skipped-assets db ready in <elapsed>ms",
            "ui:Service started. Waiting for next scheduled run.",
            "config:False|startup-disabled"
        }, actual);
        Assert.AreEqual(0, fixture.Executor.CallCount);
        stopping.Cancel();
        await execution.WaitAsync(Bound);
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectionHasExactStartupNextRunContentionSequenceAndZeroScheduledExecution()
    {
        var executor = new RecordingHostExecutor();
        var fixture = HostFixture.Create(new SnapshotConfiguration(Enabled("0 * * * *")), executor);
        await fixture.Host.TriggerRunAsync().WaitAsync(Bound);
        var manual = await executor.Entered(0).WaitAsync(Bound);
        Assert.AreEqual(ProcessingRunTrigger.Manual, manual.Request.Trigger);
        using var stopping = new CancellationTokenSource();
        var loop = fixture.Host.RunLoopAsync(stopping.Token);
        await fixture.Time.TimerCreated(0).WaitAsync(Bound);
        fixture.Configuration.Current = new ProcessingScheduleSnapshot(false, "post-rejection-disabled");
        fixture.Time.Advance(TimeSpan.FromMinutes(26));
        await fixture.Configuration.Read(1).WaitAsync(Bound);
        CollectionAssert.AreEqual(new[]
        {
            "Service started. Waiting for next scheduled run.",
            "Next run scheduled at 2026-08-30 13:00:00Z",
            "Scheduled run skipped because a processing pass is already in progress."
        }, Messages(fixture.State));
        Assert.AreEqual(1, executor.CallCount);
        Assert.AreEqual(0, fixture.Events.Count);
        Assert.AreEqual(0L, fixture.State.TotalUnprocessed);
        stopping.Cancel();
        await loop.WaitAsync(Bound);
        executor.Release(0);
        await fixture.Host.WaitForManualAdmissionAsync().WaitAsync(Bound);
        Assert.AreEqual(1, executor.CallCount);
    }

    [TestMethod]
    public async Task StartAsync_StopAsync_CooperativelyStopsDueWaitWithoutPostStopOperations()
    {
        var config = new SnapshotConfiguration(Enabled("0 * * * *"));
        var fixture = HostFixture.Create(config);
        await fixture.Host.StartAsync(CancellationToken.None).WaitAsync(Bound);
        Assert.AreEqual(TimeSpan.FromMinutes(26), await fixture.Time.TimerCreated(0).WaitAsync(Bound));
        var expectedUi = new[]
        {
            "Service started. Waiting for next scheduled run.",
            "Next run scheduled at 2026-08-30 13:00:00Z"
        };
        CollectionAssert.AreEqual(expectedUi, Messages(fixture.State));
        AssertSnapshots(config, (true, "0 * * * *"));
        Assert.AreEqual(0, fixture.Executor.CallCount);
        Assert.AreEqual(0, fixture.Events.Count);

        await fixture.Host.StopAsync(CancellationToken.None).WaitAsync(Bound);
        fixture.Time.Advance(TimeSpan.FromHours(2));
        CollectionAssert.AreEqual(expectedUi, Messages(fixture.State));
        AssertSnapshots(config, (true, "0 * * * *"));
        Assert.AreEqual(1, config.CallCount);
        Assert.AreEqual(0, fixture.Executor.CallCount);
        Assert.AreEqual(0, fixture.Events.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_AcceptedScheduledCausalSequenceIsPendingRequestExecutorTerminalThenReread()
    {
        var operations = new ConcurrentQueue<string>();
        var executor = new RecordingHostExecutor(operations);
        var config = new SnapshotConfiguration(Enabled("0 * * * *"), operations);
        var fixture = HostFixture.Create(config, executor, operations: operations);
        using var stopping = new CancellationTokenSource();
        var loop = fixture.Host.RunLoopAsync(stopping.Token);
        await fixture.Time.TimerCreated(0).WaitAsync(Bound);
        fixture.Time.Advance(TimeSpan.FromMinutes(26));
        var invocation = await executor.Entered(0).WaitAsync(Bound);
        Assert.AreEqual(ProcessingRunTrigger.Scheduled, invocation.Request.Trigger);
        Assert.AreEqual(stopping.Token, invocation.Token);
        Assert.AreEqual(1, config.CallCount);
        Assert.IsTrue(fixture.State.IsRunning);
        executor.Release(0);
        await config.Read(1).WaitAsync(Bound);
        await fixture.Time.TimerCreated(1).WaitAsync(Bound);
        var expectedUi = new[]
        {
            "Service started. Waiting for next scheduled run.",
            "Next run scheduled at 2026-08-30 13:00:00Z",
            "Run started — nothing to process, all assets already have location data.",
            "Run complete. Processed=0 Skipped=0 Errors=0",
            "Next run scheduled at 2026-08-30 14:00:00Z"
        };
        var expectedOperations = new[]
        {
            "config:True|0 * * * *",
            "executor:Scheduled:enter",
            "executor:Scheduled:terminal",
            "config:True|0 * * * *"
        };
        var expectedHistory = new[] { new ProcessingScheduleSnapshot(true, "0 * * * *"), new ProcessingScheduleSnapshot(true, "0 * * * *") };
        var events = fixture.Events.ToArray();
        CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(RunFinished) }, events.Select(item => item.GetType()).ToArray());
        Assert.IsTrue(events.All(item => ReferenceEquals(invocation.Request, item.Request)));
        var result = events.OfType<RunFinished>().Single().Result;
        Assert.AreSame(invocation.Request, result.Request);
        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        Assert.AreEqual(0L, result.ProcessedCount);
        Assert.AreEqual(1, executor.CallCount);
        Assert.AreEqual(2, config.CallCount);
        Assert.AreEqual(1, executor.Invocations.Count);
        Assert.AreSame(invocation.Request, executor.Invocations.Single().Request);
        CollectionAssert.AreEqual(expectedUi, Messages(fixture.State));
        CollectionAssert.AreEqual(expectedOperations, operations.ToArray());
        CollectionAssert.AreEqual(expectedHistory, config.History.ToArray());
        Assert.IsFalse(fixture.State.IsRunning);

        stopping.Cancel();
        await loop.WaitAsync(Bound);
        fixture.Time.Advance(TimeSpan.FromHours(2));
        CollectionAssert.AreEqual(expectedUi, Messages(fixture.State));
        CollectionAssert.AreEqual(expectedOperations, operations.ToArray());
        CollectionAssert.AreEqual(expectedHistory, config.History.ToArray());
        CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(RunFinished) }, fixture.Events.Select(item => item.GetType()).ToArray());
        Assert.IsTrue(fixture.Events.All(item => ReferenceEquals(invocation.Request, item.Request)));
        Assert.AreEqual(2, config.CallCount);
        Assert.AreEqual(1, executor.CallCount);
        Assert.AreEqual(1, executor.Invocations.Count);
    }

    [TestMethod]
    public async Task ScheduleLoop_FutureAlreadyDueAndCancelledPlansHaveCompleteExactArrays()
    {
        var future = LoopProbe.Create(Enabled("0 * * * *"));
        var futureLoop = future.RunAsync();
        Assert.AreEqual(TimeSpan.FromMinutes(26), await future.Time.TimerCreated(0).WaitAsync(Bound));
        future.Configuration.Current = new ProcessingScheduleSnapshot(false, "after-future");
        future.Time.Advance(TimeSpan.FromMinutes(26));
        var futureCall = await future.Trigger.Entered(0).WaitAsync(Bound);
        Assert.AreEqual(ProcessingRunTrigger.Scheduled, futureCall.Origin);
        Assert.AreEqual(ScheduledTriggerResult.RejectedAlreadyRunning, futureCall.PlannedResult);
        Assert.AreEqual(future.Stopping.Token, futureCall.Token);
        await future.Configuration.Read(1).WaitAsync(Bound);
        Assert.AreEqual(TimeSpan.FromMinutes(1), await future.Time.TimerCreated(1).WaitAsync(Bound));
        CollectionAssert.AreEqual(new[] { "Next run scheduled at 2026-08-30 13:00:00Z" }, future.Logs.ToArray());
        AssertSnapshots(future.Configuration, (true, "0 * * * *"), (false, "after-future"));
        Assert.AreEqual(1, future.Trigger.CallCount);
        Assert.AreEqual(3, future.Time.UtcCallCount);
        await future.CancelAndAwaitAsync(futureLoop);

        var passedTime = new ControlledTimeProvider(Now, TimeZoneInfo.Utc) { AdvanceOnUtcCall = (2, TimeSpan.FromMinutes(27)) };
        var passedTrigger = new SubstituteTrigger(ScheduledTriggerResult.AcceptedAfterTerminal, gateTerminal: true);
        var passed = LoopProbe.Create(Enabled("0 * * * *"), passedTime, passedTrigger);
        var passedLoop = passed.RunAsync();
        var passedCall = await passedTrigger.Entered(0).WaitAsync(Bound);
        Assert.AreEqual(ProcessingRunTrigger.Scheduled, passedCall.Origin);
        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, passedCall.PlannedResult);
        Assert.AreEqual(passed.Stopping.Token, passedCall.Token);
        passed.Configuration.Current = new ProcessingScheduleSnapshot(false, "after-passed");
        passedTrigger.ReleaseTerminal.TrySetResult();
        await passed.Configuration.Read(1).WaitAsync(Bound);
        Assert.AreEqual(TimeSpan.FromMinutes(1), await passed.Time.TimerCreated(0).WaitAsync(Bound));
        Assert.AreEqual(0, passed.Logs.Count);
        AssertSnapshots(passed.Configuration, (true, "0 * * * *"), (false, "after-passed"));
        Assert.AreEqual(1, passedTrigger.CallCount);
        Assert.AreEqual(3, passed.Time.UtcCallCount);
        await passed.CancelAndAwaitAsync(passedLoop);

        var cancelled = LoopProbe.Create(Enabled("0 * * * *"));
        var cancelledLoop = cancelled.RunAsync();
        Assert.AreEqual(TimeSpan.FromMinutes(26), await cancelled.Time.TimerCreated(0).WaitAsync(Bound));
        cancelled.Stopping.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelledLoop.WaitAsync(Bound));
        CollectionAssert.AreEqual(new[] { "Next run scheduled at 2026-08-30 13:00:00Z" }, cancelled.Logs.ToArray());
        AssertSnapshots(cancelled.Configuration, (true, "0 * * * *"));
        Assert.AreEqual(0, cancelled.Trigger.CallCount);
        Assert.AreEqual(2, cancelled.Time.UtcCallCount);
        cancelled.Time.Advance(TimeSpan.FromHours(2));
        Assert.AreEqual(0, cancelled.Trigger.CallCount);
        cancelled.Stopping.Dispose();
    }

    [TestMethod]
    public async Task ScheduledZeroWork_RealExecutorPublishesExactEventsAndTouchesNoPostCountDependency()
    {
        var calls = new ConcurrentQueue<string>();
        var operations = new ProcessingBackgroundService.ProcessingOperations(
            token => { calls.Enqueue("count"); return Task.FromResult(0L); },
            () => throw Unexpected("processing config"),
            () => throw Unexpected("skipped snapshot"),
            (_, _, _) => throw Unexpected("batch"),
            (_, _, _, _, _) => throw Unexpected("resolver"),
            (_, _, _, _) => throw Unexpected("geodata"),
            _ => throw Unexpected("skipped persistence"),
            (_, _, _) => throw Unexpected("asset persistence"));
        var time = new ControlledTimeProvider(Now, TimeZoneInfo.Utc);
        var executor = new ProcessingRunExecutor(NullLogger<ProcessingBackgroundService>.Instance, operations, operations, operations, operations, operations, operations, time);
        var state = new ProcessingState();
        var events = new ConcurrentQueue<ProcessingEvent>();
        var reporter = new ProcessingStateEventReporter(state, events.Enqueue);
        var host = new TestHost(NullLogger<ProcessingBackgroundService>.Instance, state, reporter, executor, new SnapshotConfiguration(Disabled("unused")), () => Task.CompletedTask, time);
        var result = await host.TriggerScheduledAsync(CancellationToken.None).WaitAsync(Bound);
        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, result);
        var exact = events.ToArray();
        CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(RunFinished) }, exact.Select(item => item.GetType()).ToArray());
        var request = exact[0].Request;
        Assert.AreEqual(ProcessingRunTrigger.Scheduled, request.Trigger);
        Assert.IsTrue(exact.All(item => ReferenceEquals(request, item.Request)));
        Assert.AreEqual(0L, exact.OfType<EligibilityDetermined>().Single().EligibleCount);
        var terminal = exact.OfType<RunFinished>().Single().Result;
        Assert.AreSame(request, terminal.Request);
        Assert.AreEqual(ProcessingRunOutcome.Completed, terminal.Outcome);
        Assert.AreEqual(0L, terminal.ProcessedCount);
        Assert.AreEqual(0L, terminal.UpdatedCount);
        Assert.AreEqual(0L, terminal.SkippedCount);
        Assert.AreEqual(0L, terminal.FailedCount);
        CollectionAssert.AreEqual(new[] { "count" }, calls.ToArray());
        Assert.IsFalse(state.IsRunning);
    }

    [TestMethod]
    public void Architecture_ExactAllowedSurfacesExcludeDataPlaneAdmissionAndCoordinatorDependencies()
    {
        AssertExactFieldTypes(typeof(ProcessingScheduleLoop), typeof(IProcessingScheduleConfiguration), typeof(TimeProvider), typeof(Action<string>), typeof(IScheduledRunTrigger));
        AssertExactFieldTypes(typeof(ProcessingScheduleSnapshot), typeof(bool), typeof(string));
        var scheduleConfigurationMethod = typeof(IProcessingScheduleConfiguration).GetMethods().Single();
        Assert.AreEqual(nameof(IProcessingScheduleConfiguration.GetSnapshotAsync), scheduleConfigurationMethod.Name);
        Assert.AreEqual(typeof(Task<ProcessingScheduleSnapshot>), scheduleConfigurationMethod.ReturnType);
        Assert.AreEqual(0, scheduleConfigurationMethod.GetParameters().Length);
        Assert.AreEqual(0, typeof(ProcessingScheduleCalculator).GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length);
        CollectionAssert.AreEqual(new[] { typeof(bool), typeof(string), typeof(DateTimeOffset) }, typeof(ProcessingScheduleCalculator).GetMethod("Calculate", BindingFlags.Static | BindingFlags.NonPublic)!.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        AssertExactFieldTypes(typeof(DisabledRetry), typeof(TimeSpan));
        AssertExactFieldTypes(typeof(InvalidRetry), typeof(TimeSpan));
        AssertExactFieldTypes(typeof(Due), typeof(DateTimeOffset));
        var triggerMethod = typeof(IScheduledRunTrigger).GetMethods().Single();
        Assert.AreEqual(typeof(Task<ScheduledTriggerResult>), triggerMethod.ReturnType);
        CollectionAssert.AreEqual(new[] { typeof(CancellationToken) }, triggerMethod.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        var forbidden = new[] { typeof(IProcessingRunConfiguration), typeof(AppConfig), typeof(IProcessingAssetRepository), typeof(IProcessingSkippedStore), typeof(IProcessingAdministrativeResolver), typeof(IProcessingInfrastructureLookup), typeof(IProcessingRunExecutor), typeof(ProcessingState), typeof(ProcessingRunExecutor) };
        var surfaces = new[] { typeof(ProcessingScheduleLoop), typeof(ProcessingScheduleSnapshot), typeof(IProcessingScheduleConfiguration), typeof(ProcessingScheduleCalculator), typeof(ProcessingSchedulePlan), typeof(DisabledRetry), typeof(InvalidRetry), typeof(Due), typeof(IScheduledRunTrigger) };
        Assert.IsFalse(surfaces.SelectMany(SurfaceTypes).Any(forbidden.Contains));
        Assert.IsFalse(surfaces.SelectMany(SurfaceTypes).Any(type => type.Name.Contains("Coordinator", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Architecture_DIIdentityIsExactAndHasNoTriggerToHostConstructorBackEdge()
    {
        using var provider = BuildProvider();
        var host = provider.GetRequiredService<ProcessingBackgroundService>();
        var config = provider.GetRequiredService<ConfigService>();
        Assert.AreSame(config, provider.GetRequiredService<IProcessingRunConfiguration>());
        Assert.AreSame(config, provider.GetRequiredService<IProcessingScheduleConfiguration>());
        Assert.AreSame(host, provider.GetRequiredService<IHostedService>());
        Assert.AreSame(host, provider.GetRequiredService<IScheduledRunTrigger>());
        Assert.AreEqual(1, provider.GetServices<IHostedService>().Count());
        Assert.IsFalse(typeof(ProcessingBackgroundService).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SelectMany(constructor => constructor.GetParameters()).Any(parameter => parameter.ParameterType == typeof(IScheduledRunTrigger)));
        AssertExactFieldTypes(typeof(ProcessingBackgroundService),
            typeof(ILogger<ProcessingBackgroundService>), typeof(ProcessingState), typeof(ProcessingStateEventReporter), typeof(IProcessingRunExecutor),
            typeof(Func<Task>), typeof(ProcessingScheduleLoop), typeof(CancellationTokenSource), typeof(Task), typeof(SemaphoreSlim));
    }

    [TestMethod]
    public async Task Block13SubstituteTrigger_ExercisesRejectedAndAcceptedAfterTerminalSemantics()
    {
        var rejected = LoopProbe.Create(Enabled("0 * * * *"), trigger: new SubstituteTrigger(ScheduledTriggerResult.RejectedAlreadyRunning));
        var rejectedLoop = rejected.RunAsync();
        await rejected.Time.TimerCreated(0).WaitAsync(Bound);
        rejected.Time.Advance(TimeSpan.FromMinutes(26));
        var rejectedCall = await rejected.Trigger.Entered(0).WaitAsync(Bound);
        Assert.AreEqual(rejected.Stopping.Token, rejectedCall.Token);
        Assert.AreEqual(ProcessingRunTrigger.Scheduled, rejectedCall.Origin);
        Assert.AreEqual(ScheduledTriggerResult.RejectedAlreadyRunning, rejectedCall.PlannedResult);
        await rejected.Configuration.Read(1).WaitAsync(Bound);
        Assert.AreEqual(1, rejected.Trigger.CallCount);
        await rejected.CancelAndAwaitAsync(rejectedLoop);
        Assert.AreEqual(1, rejected.Trigger.CallCount);

        var acceptedTrigger = new SubstituteTrigger(ScheduledTriggerResult.AcceptedAfterTerminal, gateTerminal: true);
        var accepted = LoopProbe.Create(Enabled("0 * * * *"), trigger: acceptedTrigger);
        var acceptedLoop = accepted.RunAsync();
        await accepted.Time.TimerCreated(0).WaitAsync(Bound);
        accepted.Time.Advance(TimeSpan.FromMinutes(26));
        var acceptedCall = await acceptedTrigger.Entered(0).WaitAsync(Bound);
        Assert.AreEqual(accepted.Stopping.Token, acceptedCall.Token);
        Assert.AreEqual(ProcessingRunTrigger.Scheduled, acceptedCall.Origin);
        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, acceptedCall.PlannedResult);
        Assert.IsFalse(accepted.Configuration.Read(1).IsCompleted);
        acceptedTrigger.ReleaseTerminal.TrySetResult();
        await accepted.Configuration.Read(1).WaitAsync(Bound);
        Assert.AreEqual(1, acceptedTrigger.CallCount);
        await accepted.CancelAndAwaitAsync(acceptedLoop);
        Assert.AreEqual(1, acceptedTrigger.CallCount);
    }

    [TestMethod]
    public void SchedulerContract_CannotPublishPreflightOrEligibilityWhenFutureProbeDisagrees()
    {
        var methods = typeof(IScheduledRunTrigger).GetMethods();
        Assert.AreEqual(1, methods.Length);
        Assert.AreEqual("TriggerScheduledAsync", methods[0].Name);
        Assert.IsFalse(SurfaceTypes(typeof(IScheduledRunTrigger)).Any(type => type == typeof(EligibilityDetermined) || type == typeof(IProcessingAssetRepository) || type == typeof(IProcessingRunExecutor)));
        Assert.IsFalse(typeof(ProcessingScheduleLoop).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Any(method => method.Name.Contains("Eligible", StringComparison.Ordinal) || method.Name.Contains("Count", StringComparison.Ordinal) || method.Name.Contains("Preflight", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ConfigService_ScheduleBoundaryReturnsExactImmutableSnapshotWithoutDuplicateOwner()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"change12-boundary-{Guid.NewGuid():N}");
        try
        {
            var configService = new ConfigService(NullLogger<ConfigService>.Instance, directory);
            var appConfig = Enabled("17 5 * * 2");
            appConfig.Processing.BatchSize = 777;
            await configService.SaveConfigAsync(appConfig).WaitAsync(Bound);
            var scheduleConfiguration = (IProcessingScheduleConfiguration)configService;
            var snapshot = await scheduleConfiguration.GetSnapshotAsync().WaitAsync(Bound);
            Assert.AreSame(configService, scheduleConfiguration);
            Assert.AreEqual(new ProcessingScheduleSnapshot(true, "17 5 * * 2"), snapshot);
            AssertExactFieldTypes(typeof(ProcessingScheduleSnapshot), typeof(bool), typeof(string));
            Assert.IsTrue(typeof(ProcessingScheduleSnapshot).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).All(field => field.IsInitOnly));
            Assert.IsFalse(typeof(IProcessingScheduleConfiguration).GetMethods().Single().ReturnType.ToString().Contains(nameof(AppConfig), StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PresetCron_RoundTripsConfigAndIsConsumedByRuntimeWithoutNewScheduleFields()
    {
        var editor = new ScheduleEditorState { Mode = ScheduleEditorState.ModeWeekly, Time = "06:30", WeeklyDay = "FRI" };
        await AssertCronRoundTripAndRuntimeAsync(editor.ToCron(), new DateTimeOffset(2026, 9, 4, 6, 30, 0, TimeSpan.Zero));
        Assert.AreEqual("Every Friday at 06:30", editor.Describe());
    }

    [TestMethod]
    public async Task CustomCron_RoundTripsConfigAndIsConsumedByRuntimeWithoutEditorRewrite()
    {
        const string cron = "7,37 3-5 * * 2-6";
        await AssertCronRoundTripAndRuntimeAsync(cron, new DateTimeOffset(2026, 9, 1, 3, 7, 0, TimeSpan.Zero));
        Assert.AreEqual("0 2 * * *", ScheduleEditorState.FromCron(cron).ToCron(), "Unknown custom text retains the existing editor fallback while ConfigService/runtime preserve the stored cron exactly.");
    }

    [TestMethod]
    public async Task ManualCompatibility_TwoRunsHaveExactIdentityCancellationAndNoCrossRunDuplicates()
    {
        var state = new ProcessingState();
        var events = new ConcurrentQueue<ProcessingEvent>();
        var reporter = new ProcessingStateEventReporter(state, events.Enqueue);
        var executor = new ManualLifecycleExecutor();
        var host = new TestHost(NullLogger<ProcessingBackgroundService>.Instance, state, reporter, executor, new SnapshotConfiguration(Disabled("unused")), () => Task.CompletedTask, new ControlledTimeProvider(Now, TimeZoneInfo.Utc));

        await host.TriggerRunAsync().WaitAsync(Bound);
        var first = await executor.Entered(0).WaitAsync(Bound);
        Assert.AreEqual(ProcessingRunTrigger.Manual, first.Request.Trigger);
        Assert.IsTrue(first.Token.CanBeCanceled);
        host.CancelRun();
        Assert.IsTrue(first.Token.IsCancellationRequested);
        executor.Release(0);
        await host.WaitForManualAdmissionAsync().WaitAsync(Bound);

        await host.TriggerRunAsync().WaitAsync(Bound);
        var second = await executor.Entered(1).WaitAsync(Bound);
        Assert.AreEqual(ProcessingRunTrigger.Manual, second.Request.Trigger);
        Assert.AreNotSame(first.Request, second.Request);
        executor.Release(1);
        await host.WaitForManualAdmissionAsync().WaitAsync(Bound);
        host.CancelRun();

        var firstEvents = events.Where(item => ReferenceEquals(item.Request, first.Request)).ToArray();
        var secondEvents = events.Where(item => ReferenceEquals(item.Request, second.Request)).ToArray();
        CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(RunFinished) }, firstEvents.Select(item => item.GetType()).ToArray());
        CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(RunFinished) }, secondEvents.Select(item => item.GetType()).ToArray());
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, firstEvents.OfType<RunFinished>().Single().Result.Outcome);
        Assert.AreEqual(ProcessingRunOutcome.Completed, secondEvents.OfType<RunFinished>().Single().Result.Outcome);
        Assert.AreEqual(6, events.Count);
        Assert.IsTrue(firstEvents.All(item => ReferenceEquals(first.Request, item.Request)));
        Assert.IsTrue(secondEvents.All(item => ReferenceEquals(second.Request, item.Request)));
        Assert.AreEqual(2, executor.CallCount);
        Assert.IsFalse(state.IsRunning);
        Assert.IsNull(state.LastError);
    }

    [TestMethod]
    public async Task VerificationEvidenceArtifact_LoadsTypedExternalMapAndPartitionsAll29Tasks()
    {
        static TaskMethodEvidence Method(string methodName, string assertionClauses) => new(methodName, assertionClauses);

        var executable = new Dictionary<string, TaskMethodEvidence[]>
        {
            ["1.1"] = [Method("ExecuteAsync_ZeroEligibility_UsesOneSessionBeforeCountAndReturnsExactCompletedResult", "executor owns count, eligibility zero gate, one session, exact zero terminal and no downstream work")],
            ["1.2"] = [
                Method(nameof(ExecuteAsync_StartupCausalSequenceIsLoggerInitializeReadyUiThenFirstConfigWithNoTrigger), "startup ILogger/initializer/UI/config order and no startup trigger"),
                Method("ExecuteAsync_DisabledAndInvalidRetryWaitExactDurationsBeforeFreshRead", "exact one-minute disabled and five-minute invalid waits with reevaluation only afterward"),
                Method("Calculate_HourlyDailyWeeklyCustomAndInvalidMatrix_IsUtcAndHostZoneIndependent", "Cronos Standard UTC matrix and strictly-future current-match exclusion"),
                Method("ExecuteAsync_PositiveDue_LogsExactUtcLineThenWaitsAndTriggersOnce", "positive-delay exact next-run line, timer, and one Scheduled trigger"),
                Method(nameof(ExecuteAsync_RejectionHasExactStartupNextRunContentionSequenceAndZeroScheduledExecution), "exact contention line and zero Scheduled execution"),
                Method(nameof(ExecuteAsync_AcceptedScheduledCausalSequenceIsPendingRequestExecutorTerminalThenReread), "accepted run awaits terminal before reread"),
                Method(nameof(ManualCompatibility_TwoRunsHaveExactIdentityCancellationAndNoCrossRunDuplicates), "manual surface, CTS cancellation, identity and reuse"),
                Method(nameof(Architecture_DIIdentityIsExactAndHasNoTriggerToHostConstructorBackEdge), "concrete/hosted/trigger singleton identity")],
            ["1.3"] = [
                Method(nameof(Architecture_ExactAllowedSurfacesExcludeDataPlaneAdmissionAndCoordinatorDependencies), "exact schedule snapshot/config/loop allowed type surfaces and every forbidden dependency including IProcessingRunConfiguration/AppConfig"),
                Method(nameof(ConfigService_ScheduleBoundaryReturnsExactImmutableSnapshotWithoutDuplicateOwner), "exact immutable schedule-only snapshot from the existing ConfigService owner")],
            ["2.1"] = [Method("Calculate_ReturnsExactImmutablePlanShapesAndDurations", "immutable DisabledRetry, InvalidRetry and Due values with explicit UTC input")],
            ["2.2"] = [
                Method("Calculate_FixedUtcExpressions_UseStandardStrictlyFutureUtcSemantics", "valid hourly/daily/weekly/custom Standard expressions and zero-offset UTC results"),
                Method("Calculate_HourlyDailyWeeklyCustomAndInvalidMatrix_IsUtcAndHostZoneIndependent", "current matching instant excluded and six-field invalid classification"),
                Method("ExecuteAsync_DisabledAndInvalidRetryWaitExactDurationsBeforeFreshRead", "invalid expression maps to exact five-minute retry"),
                Method("Calculate_ExpressionWithoutNextOccurrence_ReturnsFiveMinuteInvalidRetry", "valid expression with no next occurrence maps to exact InvalidRetry")],
            ["2.3"] = [Method(nameof(ScheduleLoop_FutureAlreadyDueAndCancelledPlansHaveCompleteExactArrays), "one TimeProvider supplies exact UTC reads/timers; positive, passed-due and cancelled arrays")],
            ["3.1"] = [
                Method(nameof(Architecture_ExactAllowedSurfacesExcludeDataPlaneAdmissionAndCoordinatorDependencies), "minimal token-to-two-result scheduler-facing contract"),
                Method(nameof(Block13SubstituteTrigger_ExercisesRejectedAndAcceptedAfterTerminalSemantics), "both exact results, Scheduled origin, hosted token and accepted terminal gating"),
                Method(nameof(ExecuteAsync_AcceptedScheduledCausalSequenceIsPendingRequestExecutorTerminalThenReread), "production Scheduled identity and accepted-after-terminal behavior")],
            ["3.2"] = [Method(nameof(Architecture_DIIdentityIsExactAndHasNoTriggerToHostConstructorBackEdge), "temporary direct-host adapter preserves owner and has no trigger-to-host DI back-edge")],
            ["3.3"] = [
                Method(nameof(ExecuteAsync_RejectionHasExactStartupNextRunContentionSequenceAndZeroScheduledExecution), "rejection exact contention and zero Scheduled request/reporter/executor/accounting"),
                Method(nameof(ExecuteAsync_AcceptedScheduledCausalSequenceIsPendingRequestExecutorTerminalThenReread), "pending, one executor, exact terminal and no reread before terminal")],
            ["3.4"] = [
                Method(nameof(Architecture_ExactAllowedSurfacesExcludeDataPlaneAdmissionAndCoordinatorDependencies), "no coordinator/public metadata/control API in Change 12 surfaces"),
                Method(nameof(Architecture_DIIdentityIsExactAndHasNoTriggerToHostConstructorBackEdge), "Dashboard-compatible concrete host identity remains and no coordinator DI owner exists"),
                Method(nameof(ManualCompatibility_TwoRunsHaveExactIdentityCancellationAndNoCrossRunDuplicates), "manual ownership and cancellation remain on existing host path"),
                Method(nameof(SchedulerContract_CannotPublishPreflightOrEligibilityWhenFutureProbeDisagrees), "no future coordinator/preflight behavior introduced")],
            ["4.1"] = [Method(nameof(ExecuteAsync_StartupCausalSequenceIsLoggerInitializeReadyUiThenFirstConfigWithNoTrigger), "skipped initialization timing then exact service UI then first config")],
            ["4.2"] = [
                Method(nameof(ConfigService_ScheduleBoundaryReturnsExactImmutableSnapshotWithoutDuplicateOwner), "one narrow immutable schedule snapshot from ConfigService without duplicate owner"),
                Method("ExecuteAsync_PositiveDue_LogsExactUtcLineThenWaitsAndTriggersOnce", "one snapshot, plan, exact log, TimeProvider wait and one trigger")],
            ["4.3"] = [
                Method(nameof(ScheduleLoop_DisabledSnapshotPinsExactCronUntilOneMinuteThenUsesEnabledReplacement), "disabled snapshot pinning"),
                Method(nameof(ScheduleLoop_InvalidCronASnapshotPinsFiveMinutesThenUsesValidCronB), "invalid snapshot pinning"),
                Method(nameof(ScheduleLoop_DueCronARemainsTriggerUntilDueThenCronBIsReadAndPlanned), "due snapshot pinning"),
                Method(nameof(ScheduleLoop_AcceptedRunPinsCronAAndBlocksCronBUntilTerminal), "accepted-run snapshot pinning through terminal")],
            ["4.4"] = [
                Method("ExecuteAsync_ShutdownDuringDisabledOrInvalidRetryProducesNoTriggerOrError", "shutdown during both disabled and invalid retry waits"),
                Method(nameof(ScheduleLoop_FutureAlreadyDueAndCancelledPlansHaveCompleteExactArrays), "shutdown during due wait with zero post-cancel trigger"),
                Method("ExecuteAsync_AcceptedTriggerBlocksReevaluationAndPropagatesShutdownToken", "shutdown token reaches accepted execution and blocks reread"),
                Method(nameof(ExecuteAsync_ExactStoppingTokenCancellationIsSuppressedAtConfigBoundary), "active exact-token cancellation is cooperative with no ordinary error"),
                Method(nameof(ExecuteAsync_ForeignConfigCancellationPropagatesExactReferenceWhenShutdownRaces), "foreign config cancellation is not suppressed"),
                Method(nameof(ExecuteAsync_ForeignDownstreamCancellationPropagatesExactReferenceDuringShutdownWithoutRecursiveTrigger), "foreign downstream cancellation is not suppressed or recursively triggered"),
                Method(nameof(ManualCompatibility_TwoRunsHaveExactIdentityCancellationAndNoCrossRunDuplicates), "manual CancelRun CTS remains scoped per manual request")],
            ["4.5"] = [Method(nameof(ScheduledZeroWork_RealExecutorPublishesExactEventsAndTouchesNoPostCountDependency), "accepted trigger always reaches real executor; zero scheduling preflight/data access")],
            ["4.6"] = [Method(nameof(Architecture_DIIdentityIsExactAndHasNoTriggerToHostConstructorBackEdge), "exact concrete/IHostedService/IScheduledRunTrigger ReferenceEquals")],
            ["5.1"] = [
                Method("Calculate_FixedUtcExpressions_UseStandardStrictlyFutureUtcSemantics", "valid expression matrix and exact zero-offset UTC results"),
                Method("Calculate_HourlyDailyWeeklyCustomAndInvalidMatrix_IsUtcAndHostZoneIndependent", "current-match exclusion and invalid six-field expression"),
                Method("Calculate_ExpressionWithoutNextOccurrence_ReturnsFiveMinuteInvalidRetry", "no-next expression classification"),
                Method(nameof(ScheduleLoop_IdenticalUtcAcrossUtcAndDstLocalZonesProducesIdenticalExactDue), "actual loop UTC-vs-DST LocalTimeZone independence"),
                Method("Calculate_NonZeroOffsetInput_IsRejectedAndUtcInputIgnoresLocalDst", "nonzero-offset rejection")],
            ["5.2"] = [Method("ExecuteAsync_DisabledAndInvalidRetryWaitExactDurationsBeforeFreshRead", "exact one/five-minute advancement, no trigger/log and fresh read only afterward")],
            ["5.3"] = [Method(nameof(ScheduleLoop_FutureAlreadyDueAndCancelledPlansHaveCompleteExactArrays), "future/passed/cancelled exact complete arrays and zero duplicate/post-cancel calls")],
            ["5.4"] = [
                Method(nameof(ExecuteAsync_RejectionHasExactStartupNextRunContentionSequenceAndZeroScheduledExecution), "rejection zero executor with exact line"),
                Method(nameof(ExecuteAsync_AcceptedScheduledCausalSequenceIsPendingRequestExecutorTerminalThenReread), "accepted exact token/identity/events/terminal/reread and post-stop immutability")],
            ["5.5"] = [
                Method(nameof(ScheduleLoop_DisabledSnapshotPinsExactCronUntilOneMinuteThenUsesEnabledReplacement), "disabled mutation exact history"),
                Method(nameof(ScheduleLoop_InvalidCronASnapshotPinsFiveMinutesThenUsesValidCronB), "invalid mutation exact history"),
                Method(nameof(ScheduleLoop_DueCronARemainsTriggerUntilDueThenCronBIsReadAndPlanned), "due mutation exact history"),
                Method(nameof(ScheduleLoop_AcceptedRunPinsCronAAndBlocksCronBUntilTerminal), "accepted mutation exact history"),
                Method(nameof(PresetCron_RoundTripsConfigAndIsConsumedByRuntimeWithoutNewScheduleFields), "preset ConfigService/runtime round trip"),
                Method(nameof(CustomCron_RoundTripsConfigAndIsConsumedByRuntimeWithoutEditorRewrite), "custom ConfigService/runtime round trip with editor unchanged")],
            ["5.6"] = [
                Method(nameof(ExecuteAsync_StartupCausalSequenceIsLoggerInitializeReadyUiThenFirstConfigWithNoTrigger), "startup lifecycle exact order and no immediate run"),
                Method(nameof(StartAsync_StopAsync_CooperativelyStopsDueWaitWithoutPostStopOperations), "actual BackgroundService StartAsync/StopAsync cooperative termination and post-stop immutability")],
            ["5.7"] = [
                Method(nameof(ScheduledZeroWork_RealExecutorPublishesExactEventsAndTouchesNoPostCountDependency), "real executor exact zero-work lifecycle and layering"),
                Method(nameof(Architecture_ExactAllowedSurfacesExcludeDataPlaneAdmissionAndCoordinatorDependencies), "schedule collaborators exclude full config and all count/eligibility/skipped/batch/geodata/persistence dependencies")],
            ["5.8"] = [
                Method(nameof(ManualCompatibility_TwoRunsHaveExactIdentityCancellationAndNoCrossRunDuplicates), "manual two-run exact identity, CTS, outcomes and event arrays"),
                Method("TriggerRunAsync_WhileRunOwnsExecution_SilentlyRejectsDuplicateManualTrigger", "duplicate manual exclusion while ownership is held"),
                Method("TriggerRunAsync_Accepted_DelegatesExactArmedRequestReporterAndManualTokenOnce", "exact armed Manual request/reporter/token delegation")],
            ["6.4"] = [
                Method(nameof(Architecture_ExactAllowedSurfacesExcludeDataPlaneAdmissionAndCoordinatorDependencies), "exact narrow dependency/scope surfaces reject IProcessingRunConfiguration and AppConfig"),
                Method(nameof(ConfigService_ScheduleBoundaryReturnsExactImmutableSnapshotWithoutDuplicateOwner), "immutable snapshot and no duplicate configuration owner"),
                Method(nameof(Architecture_DIIdentityIsExactAndHasNoTriggerToHostConstructorBackEdge), "exact ConfigService/run-config/schedule-config identity plus hosted/trigger identity/no-cycle surfaces")],
            ["6.5"] = [
                Method(nameof(Block13SubstituteTrigger_ExercisesRejectedAndAcceptedAfterTerminalSemantics), "substitute both results, Scheduled origin, token, terminal gate and no extras"),
                Method(nameof(Architecture_ExactAllowedSurfacesExcludeDataPlaneAdmissionAndCoordinatorDependencies), "coordinator/public API remains absent"),
                Method(nameof(Architecture_DIIdentityIsExactAndHasNoTriggerToHostConstructorBackEdge), "Dashboard/concrete host identity retained until block 13"),
                Method(nameof(ManualCompatibility_TwoRunsHaveExactIdentityCancellationAndNoCrossRunDuplicates), "manual ownership/cancellation remains existing host behavior")]
        };

        var evidencePath = Path.Combine(AppContext.BaseDirectory, "change12-verification-evidence.json");
        Assert.IsTrue(File.Exists(evidencePath), $"Authoritative verification evidence must exist at deterministic path {evidencePath}.");
        await using var evidenceStream = File.OpenRead(evidencePath);
        var artifact = await JsonSerializer.DeserializeAsync<VerificationEvidenceArtifact>(
            evidenceStream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }).AsTask().WaitAsync(Bound);
        Assert.IsNotNull(artifact);
        Assert.AreEqual(1, artifact.SchemaVersion);
        Assert.AreEqual("12-separate-schedule-calculation-from-execution", artifact.Change);
        var external = artifact.Records.ToDictionary(record => record.TaskId, record => record.Commands, StringComparer.Ordinal);

        var expectedTaskIds = new[]
        {
            "1.1", "1.2", "1.3", "2.1", "2.2", "2.3", "3.1", "3.2", "3.3", "3.4",
            "4.1", "4.2", "4.3", "4.4", "4.5", "4.6", "5.1", "5.2", "5.3", "5.4",
            "5.5", "5.6", "5.7", "5.8", "6.1", "6.2", "6.3", "6.4", "6.5"
        };
        var expectedExternalTaskIds = new[] { "6.1", "6.2", "6.3" };
        CollectionAssert.AreEquivalent(expectedExternalTaskIds, external.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { "focused" }, external["6.1"].Select(item => item.Kind).ToArray());
        CollectionAssert.AreEqual(new[] { "canonical" }, external["6.2"].Select(item => item.Kind).ToArray());
        CollectionAssert.AreEqual(new[] { "strict", "status", "apply" }, external["6.3"].Select(item => item.Kind).ToArray());
        Assert.AreEqual(0, executable.Keys.Intersect(external.Keys, StringComparer.Ordinal).Count());
        CollectionAssert.AreEquivalent(expectedTaskIds, executable.Keys.Concat(external.Keys).ToArray());
        Assert.AreEqual(29, executable.Count + external.Count);

        var assembly = GetType().Assembly;
        foreach (var (task, evidence) in executable)
        {
            Assert.IsTrue(evidence.Length > 0, $"Task {task} has no direct executable proof.");
            foreach (var item in evidence)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(item.AssertionClauses), $"Task {task} method {item.MethodName} has no semantic assertion clauses.");
                var candidates = assembly.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public)).Where(method => method.Name == item.MethodName && method.GetCustomAttribute<TestMethodAttribute>() is not null).ToArray();
                Assert.AreEqual(1, candidates.Length, $"Task {task} method {item.MethodName} must resolve to exactly one direct executable TestMethod.");
            }
        }
        Assert.AreEqual(3, artifact.Records.Length);
        foreach (var (task, evidence) in external)
        {
            var deserializedRecord = artifact.Records.Single(record => record.TaskId == task);
            Assert.AreSame(deserializedRecord.Commands, evidence, $"Typed map for {task} must reference the deserialized authoritative command array.");
            Assert.IsTrue(evidence.Length > 0, $"External task {task} has no command evidence.");
            foreach (var item in evidence)
            {
                Assert.IsFalse(item.Command.Contains("<", StringComparison.Ordinal) || item.Command.Contains(">", StringComparison.Ordinal), $"External task {task} contains placeholder command text.");
                AssertVerificationCommand(item);
            }
        }
        await Task.CompletedTask.WaitAsync(Bound);
    }

    private static void AssertVerificationCommand(ExternalGateEvidence evidence)
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(evidence.Command));
        Assert.IsFalse(string.IsNullOrWhiteSpace(evidence.Result));
        switch (evidence.Kind)
        {
            case "focused":
            {
                Assert.IsTrue(Regex.IsMatch(evidence.Command, "^dotnet test --project [^ ]+ --no-build --filter \"[^\"]+\"$", RegexOptions.CultureInvariant));
                var filter = evidence.Command[(evidence.Command.IndexOf("--filter \"", StringComparison.Ordinal) + 10)..^1];
                var terms = filter.Split('|', StringSplitOptions.RemoveEmptyEntries);
                Assert.AreEqual(11, terms.Length);
                Assert.AreEqual(terms.Length, terms.Distinct(StringComparer.Ordinal).Count());
                Assert.IsTrue(terms.All(term => term.StartsWith("FullyQualifiedName~", StringComparison.Ordinal)));
                AssertSuccessfulTestResult(evidence.Result, requireDefaultExclusions: false);
                break;
            }
            case "canonical":
                Assert.IsTrue(Regex.IsMatch(evidence.Command, "^npm run test$", RegexOptions.CultureInvariant));
                AssertSuccessfulTestResult(evidence.Result, requireDefaultExclusions: true);
                break;
            case "strict":
                Assert.IsTrue(Regex.IsMatch(evidence.Command, "^openspec validate [a-z0-9-]+ --strict$", RegexOptions.CultureInvariant));
                StringAssert.Contains(evidence.Result, "exit 0;");
                StringAssert.Contains(evidence.Result, " is valid");
                break;
            case "status":
                Assert.IsTrue(Regex.IsMatch(evidence.Command, "^openspec status --change [a-z0-9-]+ --json$", RegexOptions.CultureInvariant));
                StringAssert.Contains(evidence.Result, "exit 0;");
                StringAssert.Contains(evidence.Result, "isComplete true");
                StringAssert.Contains(evidence.Result, "proposal/specs/design/tasks status done");
                break;
            case "apply":
                Assert.IsTrue(Regex.IsMatch(evidence.Command, "^openspec instructions apply --change [a-z0-9-]+ --json$", RegexOptions.CultureInvariant));
                StringAssert.Contains(evidence.Result, "exit 0;");
                StringAssert.Contains(evidence.Result, "state all_done");
                StringAssert.Contains(evidence.Result, "total 29; complete 29; remaining 0");
                break;
            default:
                Assert.Fail($"Unsupported verification command kind '{evidence.Kind}'.");
                break;
        }
    }

    private static void AssertSuccessfulTestResult(string result, bool requireDefaultExclusions)
    {
        var match = Regex.Match(result, "^(?<passed>[0-9]+)/(?<total>[0-9]+) passed; 0 failed; 0 skipped; duration (?<duration>[0-9]+(?:ms|s(?: [0-9]+ms)?))(?:; default Integration/Performance exclusions)?$", RegexOptions.CultureInvariant);
        Assert.IsTrue(match.Success, $"Invalid successful test result schema: {result}");
        Assert.AreEqual(match.Groups["passed"].Value, match.Groups["total"].Value);
        Assert.IsTrue(int.Parse(match.Groups["passed"].Value) > 0);
        Assert.AreEqual(requireDefaultExclusions, result.EndsWith("; default Integration/Performance exclusions", StringComparison.Ordinal));
    }

    private static async Task<(DateTimeOffset Due, TimeSpan Delay, int UtcCalls)> ObserveFirstDueAsync(TimeZoneInfo zone)
    {
        var time = new ControlledTimeProvider(Now, zone);
        var logs = new ConcurrentQueue<string>();
        var config = new SnapshotConfiguration(Enabled("0 * * * *"));
        var trigger = new SubstituteTrigger(ScheduledTriggerResult.RejectedAlreadyRunning);
        using var stopping = new CancellationTokenSource();
        var loop = new ProcessingScheduleLoop(config, time, logs.Enqueue, trigger).RunAsync(stopping.Token);
        var delay = await time.TimerCreated(0).WaitAsync(Bound);
        var dueText = logs.Single();
        Assert.AreEqual("Next run scheduled at 2026-08-30 13:00:00Z", dueText);
        stopping.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => loop.WaitAsync(Bound));
        return (new DateTimeOffset(2026, 8, 30, 13, 0, 0, TimeSpan.Zero), delay, time.UtcCallCount);
    }

    private static async Task AssertCronRoundTripAndRuntimeAsync(string cron, DateTimeOffset expectedDue)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"change12-{Guid.NewGuid():N}");
        try
        {
            var service = new ConfigService(NullLogger<ConfigService>.Instance, directory);
            await service.SaveConfigAsync(Enabled(cron)).WaitAsync(Bound);
            var loaded = await service.GetConfigAsync().WaitAsync(Bound);
            Assert.IsTrue(loaded.Schedule.Enabled);
            Assert.AreEqual(cron, loaded.Schedule.Cron);
            Assert.AreEqual(2, typeof(ScheduleConfig).GetProperties(BindingFlags.Instance | BindingFlags.Public).Length);
            var time = new ControlledTimeProvider(new DateTimeOffset(2026, 8, 30, 12, 34, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
            var trigger = new SubstituteTrigger(ScheduledTriggerResult.RejectedAlreadyRunning);
            var logs = new ConcurrentQueue<string>();
            using var stopping = new CancellationTokenSource();
            var loop = new ProcessingScheduleLoop(new SnapshotConfiguration(loaded), time, logs.Enqueue, trigger).RunAsync(stopping.Token);
            await time.TimerCreated(0).WaitAsync(Bound);
            Assert.AreEqual($"Next run scheduled at {expectedDue.UtcDateTime:u}", logs.Single());
            stopping.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => loop.WaitAsync(Bound));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void AssertSnapshots(SnapshotConfiguration configuration, params (bool Enabled, string Cron)[] expected)
        => CollectionAssert.AreEqual(expected.Select(item => new ProcessingScheduleSnapshot(item.Enabled, item.Cron)).ToArray(), configuration.History.ToArray());

    private static void AssertNoOrdinaryErrorOrTrigger(HostFixture fixture)
    {
        Assert.AreEqual(0, fixture.Executor.CallCount);
        Assert.AreEqual(0, fixture.Events.Count);
        Assert.IsFalse(Messages(fixture.State).Any(message => message.Contains("ERROR", StringComparison.Ordinal) || message.Contains("Fatal", StringComparison.Ordinal)));
    }

    private static void AssertExactFieldTypes(Type type, params Type[] expected)
    {
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => !field.IsStatic)
            .Select(field => field.FieldType)
            .Distinct()
            .ToArray();
        CollectionAssert.AreEquivalent(expected.Distinct().ToArray(), fields, $"Unexpected field surface for {type.Name}: {string.Join(", ", fields.Select(item => item.Name))}");
    }

    private static IEnumerable<Type> SurfaceTypes(Type type)
    {
        return type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Select(field => field.FieldType)
            .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType)))
            .Concat(type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType)));
    }

    private static void RecordNewUi(ProcessingState state, ConcurrentQueue<string> operations, ConcurrentDictionary<string, byte> recorded)
    {
        foreach (var message in Messages(state))
        {
            if (recorded.TryAdd(message, 0))
            {
                operations.Enqueue($"ui:{message}");
            }
        }
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<ProcessingBackgroundService>>(NullLogger<ProcessingBackgroundService>.Instance);
        services.AddSingleton((ConfigService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ConfigService)));
        services.AddSingleton((AdministrativeAreaResolverService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AdministrativeAreaResolverService)));
        services.AddSingleton((ImmichDbRepository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ImmichDbRepository)));
        services.AddSingleton((ImmichReverseGeo.Overture.Services.OverturePlacesService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ImmichReverseGeo.Overture.Services.OverturePlacesService)));
        services.AddSingleton((SkippedAssetsRepository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SkippedAssetsRepository)));
        services.AddProcessingServices();
        return services.BuildServiceProvider();
    }

    private static AppConfig Enabled(string cron) => new() { Schedule = new ScheduleConfig { Enabled = true, Cron = cron } };
    private static AppConfig Disabled(string cron) => new() { Schedule = new ScheduleConfig { Enabled = false, Cron = cron } };
    private static string[] Messages(ProcessingState state) => state.GetRecentLog().Select(line => line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..]).ToArray();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static AssertFailedException Unexpected(string operation) => new($"Unexpected {operation} after authoritative zero count.");

    private sealed class TestHost(ILogger<ProcessingBackgroundService> logger, ProcessingState state, ProcessingStateEventReporter reporter, IProcessingRunExecutor executor, IProcessingScheduleConfiguration configuration, Func<Task> initialize, TimeProvider timeProvider)
        : ProcessingBackgroundService(logger, state, reporter, executor, configuration, initialize, timeProvider)
    {
        public Task RunLoopAsync(CancellationToken token) => ExecuteAsync(token);
    }

    private sealed class HostFixture
    {
        private HostFixture(IProcessingScheduleConfiguration configuration, IProcessingRunExecutor executor, ControlledTimeProvider time, ILogger<ProcessingBackgroundService> logger, Func<Task> initialize, ConcurrentQueue<string>? operations)
        {
            Configuration = configuration as SnapshotConfiguration ?? new SnapshotConfiguration(Disabled("not-recorded"));
            Executor = executor as RecordingHostExecutor ?? new RecordingHostExecutor();
            Time = time;
            Reporter = new ProcessingStateEventReporter(State, Events.Enqueue);
            Host = new TestHost(logger, State, Reporter, executor, configuration, initialize, time);
            Operations = operations ?? new();
        }
        public ProcessingState State { get; } = new();
        public ConcurrentQueue<ProcessingEvent> Events { get; } = new();
        public ConcurrentDictionary<string, byte> UiRecorded { get; } = new();
        public ConcurrentQueue<string> Operations { get; }
        public SnapshotConfiguration Configuration { get; }
        public RecordingHostExecutor Executor { get; }
        public ControlledTimeProvider Time { get; }
        public ProcessingStateEventReporter Reporter { get; }
        public TestHost Host { get; }
        public static HostFixture Create(IProcessingScheduleConfiguration configuration, IProcessingRunExecutor? executor = null, ControlledTimeProvider? time = null, ILogger<ProcessingBackgroundService>? logger = null, Func<Task>? initialize = null, ConcurrentQueue<string>? operations = null)
            => new(configuration, executor ?? new RecordingHostExecutor(operations), time ?? new ControlledTimeProvider(Now, TimeZoneInfo.Utc), logger ?? NullLogger<ProcessingBackgroundService>.Instance, initialize ?? (() => Task.CompletedTask), operations);
    }

    private sealed class SnapshotConfiguration : IProcessingScheduleConfiguration
    {
        private readonly ConcurrentQueue<string>? _operations;

        public SnapshotConfiguration(AppConfig current, ConcurrentQueue<string>? operations = null)
        {
            Current = new ProcessingScheduleSnapshot(current.Schedule.Enabled, current.Schedule.Cron);
            _operations = operations;
        }
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _reads = new();
        private int _calls;
        public ProcessingScheduleSnapshot Current { get; set; }
        public ConcurrentQueue<ProcessingScheduleSnapshot> History { get; } = new();
        public int CallCount => Volatile.Read(ref _calls);
        public Task Read(int index) => _reads.GetOrAdd(index, _ => Signal()).Task;
        public Task<ProcessingScheduleSnapshot> GetSnapshotAsync()
        {
            var snapshot = Current;
            History.Enqueue(snapshot);
            _operations?.Enqueue($"config:{snapshot.Enabled}|{snapshot.Cron}");
            var index = Interlocked.Increment(ref _calls) - 1;
            _reads.GetOrAdd(index, _ => Signal()).TrySetResult();
            return Task.FromResult(snapshot);
        }
    }

    private sealed class GatedFailureConfiguration(TaskCompletionSource entered, TaskCompletionSource release, Func<Exception> failure) : IProcessingScheduleConfiguration
    {
        public async Task<ProcessingScheduleSnapshot> GetSnapshotAsync()
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(Bound);
            throw failure();
        }
    }

    private sealed class RecordingHostExecutor(ConcurrentQueue<string>? operations = null) : IProcessingRunExecutor
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<Invocation>> _entered = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _releases = new();
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);
        public ConcurrentQueue<Invocation> Invocations { get; } = new();
        public Task<Invocation> Entered(int index) => _entered.GetOrAdd(index, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        public void Release(int index) => _releases.GetOrAdd(index, _ => Signal()).TrySetResult();
        public async Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            var invocation = new Invocation(request, cancellationToken);
            Invocations.Enqueue(invocation);
            operations?.Enqueue($"executor:{request.Trigger}:enter");
            _entered.GetOrAdd(index, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(invocation);
            await _releases.GetOrAdd(index, _ => Signal()).Task.WaitAsync(Bound, cancellationToken);
            var session = await reporter.OpenRunAsync(request, Now, CancellationToken.None).AsTask().WaitAsync(Bound);
            await session.DetermineEligibilityAsync(0, CancellationToken.None).AsTask().WaitAsync(Bound);
            var result = new ProcessingRunResult(request, Now, Now, 0, 0, 0, 0, ProcessingRunOutcome.Completed, null);
            await session.FinishAsync(result).AsTask().WaitAsync(Bound);
            operations?.Enqueue($"executor:{request.Trigger}:terminal");
            return result;
        }
    }

    private sealed class ManualLifecycleExecutor : IProcessingRunExecutor
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<Invocation>> _entered = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _releases = new();
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);
        public Task<Invocation> Entered(int index) => _entered.GetOrAdd(index, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        public void Release(int index) => _releases.GetOrAdd(index, _ => Signal()).TrySetResult();
        public async Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            var invocation = new Invocation(request, cancellationToken);
            _entered.GetOrAdd(index, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(invocation);
            await _releases.GetOrAdd(index, _ => Signal()).Task.WaitAsync(Bound);
            var session = await reporter.OpenRunAsync(request, Now, CancellationToken.None).AsTask().WaitAsync(Bound);
            await session.DetermineEligibilityAsync(0, CancellationToken.None).AsTask().WaitAsync(Bound);
            var outcome = cancellationToken.IsCancellationRequested ? ProcessingRunOutcome.Cancelled : ProcessingRunOutcome.Completed;
            var result = new ProcessingRunResult(request, Now, Now, 0, 0, 0, 0, outcome, null);
            await session.FinishAsync(result).AsTask().WaitAsync(Bound);
            return result;
        }
    }

    private sealed class GatedThrowingExecutor(OperationCanceledException failure) : IProcessingRunExecutor
    {
        public TaskCompletionSource<Invocation> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = Signal();
        public ConcurrentQueue<Invocation> Invocations { get; } = new();
        public int CallCount { get; private set; }
        public async Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            CallCount++;
            var invocation = new Invocation(request, cancellationToken);
            Invocations.Enqueue(invocation);
            var session = await reporter.OpenRunAsync(request, Now, CancellationToken.None).AsTask().WaitAsync(Bound);
            Entered.TrySetResult(invocation);
            await Release.Task.WaitAsync(Bound);
            throw failure;
        }
    }

    private sealed record TaskMethodEvidence(string MethodName, string AssertionClauses);
    private sealed record VerificationEvidenceArtifact(int SchemaVersion, string Change, VerificationTaskEvidence[] Records);
    private sealed record VerificationTaskEvidence(string TaskId, ExternalGateEvidence[] Commands);
    private sealed record ExternalGateEvidence(string Kind, string Command, string Result);
    private sealed record Invocation(ProcessingRunRequest Request, CancellationToken Token);

    private sealed class RecordingLogger<T>(ConcurrentQueue<string> operations) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message.StartsWith("ProcessingBackgroundService: skipped-assets db ready in ", StringComparison.Ordinal))
            {
                operations.Enqueue("logger:ProcessingBackgroundService: skipped-assets db ready in <elapsed>ms");
            }
            else
            {
                operations.Enqueue($"logger:{message}");
            }
        }
    }

    private sealed class SubstituteTrigger(ScheduledTriggerResult result, bool gateTerminal = false) : IScheduledRunTrigger
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<TriggerCall>> _entered = new();
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);
        public TaskCompletionSource ReleaseTerminal { get; } = Signal();
        public Task<TriggerCall> Entered(int index) => _entered.GetOrAdd(index, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        public async Task<ScheduledTriggerResult> TriggerScheduledAsync(CancellationToken stoppingToken)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            _entered.GetOrAdd(index, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(new TriggerCall(ProcessingRunTrigger.Scheduled, stoppingToken, result));
            if (gateTerminal)
            {
                await ReleaseTerminal.Task.WaitAsync(Bound, stoppingToken);
            }
            return result;
        }
    }

    private sealed record TriggerCall(ProcessingRunTrigger Origin, CancellationToken Token, ScheduledTriggerResult PlannedResult);

    private sealed class LoopProbe
    {
        private LoopProbe(AppConfig config, ControlledTimeProvider time, SubstituteTrigger trigger)
        {
            Time = time;
            Trigger = trigger;
            Configuration = new SnapshotConfiguration(config);
            Loop = new ProcessingScheduleLoop(Configuration, Time, Logs.Enqueue, Trigger);
        }
        public CancellationTokenSource Stopping { get; } = new();
        public ConcurrentQueue<string> Logs { get; } = new();
        public SnapshotConfiguration Configuration { get; }
        public ControlledTimeProvider Time { get; }
        public SubstituteTrigger Trigger { get; }
        public ProcessingScheduleLoop Loop { get; }
        public static LoopProbe Create(AppConfig config, ControlledTimeProvider? time = null, SubstituteTrigger? trigger = null) => new(config, time ?? new ControlledTimeProvider(Now, TimeZoneInfo.Utc), trigger ?? new SubstituteTrigger(ScheduledTriggerResult.RejectedAlreadyRunning));
        public Task RunAsync() => Loop.RunAsync(Stopping.Token);
        public async Task CancelAndAwaitAsync(Task loop)
        {
            Stopping.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => loop.WaitAsync(Bound));
            Stopping.Dispose();
        }
    }

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ControlledTimer> _timers = [];
        private readonly ConcurrentDictionary<int, TaskCompletionSource<TimeSpan>> _created = new();
        private DateTimeOffset _now;
        private int _timerCount;
        private int _utcCalls;
        public ControlledTimeProvider(DateTimeOffset now, TimeZoneInfo localZone) { _now = now; LocalZone = localZone; }
        public TimeZoneInfo LocalZone { get; }
        public (int Call, TimeSpan Advance)? AdvanceOnUtcCall { get; set; }
        public int UtcCallCount => Volatile.Read(ref _utcCalls);
        public Task<TimeSpan> TimerCreated(int index) => _created.GetOrAdd(index, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                var call = ++_utcCalls;
                if (AdvanceOnUtcCall is { } advance && advance.Call == call) { _now += advance.Advance; }
                return _now;
            }
        }
        public override long GetTimestamp() { lock (_sync) { return _now.UtcTicks; } }
        public override TimeZoneInfo LocalTimeZone => LocalZone;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ControlledTimer timer; int index;
            lock (_sync) { timer = new ControlledTimer(this, callback, state, _now + dueTime, period); _timers.Add(timer); index = _timerCount++; }
            _created.GetOrAdd(index, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(dueTime);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            ControlledTimer[] due;
            lock (_sync) { _now += amount; due = _timers.Where(timer => timer.IsDue(_now)).ToArray(); foreach (var timer in due) { timer.MarkFired(_now); } }
            foreach (var timer in due) { timer.Fire(); }
        }
        private void Remove(ControlledTimer timer) { lock (_sync) { _timers.Remove(timer); } }
        private sealed class ControlledTimer(ControlledTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset due, TimeSpan period) : ITimer
        {
            private bool _disposed; private DateTimeOffset _due = due;
            public bool IsDue(DateTimeOffset now) => !_disposed && _due <= now;
            public void MarkFired(DateTimeOffset now) { if (period == Timeout.InfiniteTimeSpan) { _disposed = true; owner.Remove(this); } else { _due = now + period; } }
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan newPeriod) => throw new NotSupportedException();
            public void Dispose() { _disposed = true; owner.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
