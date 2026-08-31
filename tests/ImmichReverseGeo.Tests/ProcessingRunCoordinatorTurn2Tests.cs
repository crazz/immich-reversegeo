using System.Collections.Concurrent;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProcessingRunCoordinatorTurn2Tests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 2, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task AcceptedManual_ReservePendingArmDispatch_ExactCompleteOperationArray()
    {
        var id = Guid.Parse("13200000-0000-0000-0000-000000000001");
        var fixture = new DirectFixture([id]);
        var plan = fixture.Executor.Enqueue();

        var admission = await fixture.Coordinator.TriggerManualAsync().WaitAsync(TestTimeout);
        var invocation = await plan.Entered.Task.WaitAsync(TestTimeout);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, admission);
        Assert.AreSame(invocation.Request, fixture.Coordinator.ActiveRequest);
        Assert.AreEqual(id, invocation.Request.RunId);
        Assert.AreEqual(ProcessingRunTrigger.Manual, invocation.Request.Trigger);
        Assert.AreSame(fixture.Reporter, invocation.Reporter);
        Assert.AreEqual(fixture.Cancellations.Created.Single().Token, invocation.Token);
        CollectionAssert.AreEqual(
            new[] { $"id:{id}", "cts:create:Manual", $"pending:{id}", $"arm:{id}", $"dispatch:{id}" },
            fixture.Operations.ToArray());

        plan.Release.TrySetResult();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        CollectionAssert.AreEqual(
            new[]
            {
                $"id:{id}", "cts:create:Manual", $"pending:{id}", $"arm:{id}", $"dispatch:{id}",
                $"event:RunStarted:{id}", $"event:EligibilityDetermined:{id}", $"event:RunFinished:{id}",
                $"release:{id}", $"cts:dispose:{id}"
            },
            fixture.Operations.ToArray());
        Assert.AreEqual(1, fixture.Cancellations.Created.Single().DisposeCount);
        Assert.AreEqual(0, fixture.Cancellations.Created.Single().CancelCount);
    }

    [TestMethod]
    public async Task AlreadyRunningAndStopping_FreezeAllIdentityCancellationProjectionDispatchAndReferenceArrays()
    {
        var firstId = Guid.Parse("13200000-0000-0000-0000-000000000002");
        var fixture = new DirectFixture([firstId]);
        var plan = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var invocation = await plan.Entered.Task.WaitAsync(TestTimeout);
        var beforeManual = fixture.Snapshot();

        Assert.AreEqual(ProcessingRunAdmissionResult.AlreadyRunning, await fixture.Coordinator.TriggerManualAsync());
        fixture.AssertSnapshot(beforeManual);
        Assert.AreSame(invocation.Request, fixture.Coordinator.ActiveRequest);
        Assert.AreEqual(invocation.Token, fixture.Cancellations.Created.Single().Token);

        var scheduledBefore = fixture.Snapshot();
        Assert.AreEqual(ScheduledTriggerResult.RejectedAlreadyRunning,
            await ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(CancellationToken.None));
        Assert.AreEqual(scheduledBefore.IdCalls, fixture.IdCalls);
        Assert.AreEqual(scheduledBefore.CtsCreates, fixture.Cancellations.Created.Count);
        Assert.AreEqual(scheduledBefore.Dispatches, fixture.Executor.CallCount);
        Assert.AreEqual(scheduledBefore.Events, fixture.Events.Count);
        Assert.AreEqual(scheduledBefore.Abandons, fixture.ControlOperations.Count(item => item.StartsWith("abandon:", StringComparison.Ordinal)));
        Assert.AreEqual(scheduledBefore.Messages + 1, fixture.Messages().Length);
        Assert.AreEqual("Scheduled run skipped because a processing pass is already in progress.", fixture.Messages()[^1]);
        Assert.AreSame(invocation.Request, fixture.Coordinator.ActiveRequest);

        using var stopBound = new CancellationTokenSource(TestTimeout);
        var stop = fixture.Coordinator.StopAsync(stopBound.Token);
        await plan.CancelObserved.Task.WaitAsync(TestTimeout);
        plan.Release.TrySetResult();
        await stop.WaitAsync(TestTimeout);
        var stopped = fixture.Snapshot();
        Assert.AreEqual(ProcessingRunAdmissionResult.Stopping, await fixture.Coordinator.TriggerManualAsync());
        Assert.AreEqual(ScheduledTriggerResult.RejectedAlreadyRunning,
            await ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(CancellationToken.None));
        Assert.IsFalse(fixture.Coordinator.CancelActiveRun());
        fixture.AssertSnapshot(stopped);
    }

    [TestMethod]
    public async Task ActivePublicationBeforePending_ImmediateAndDuplicateCancelUseExactTokenOnceThenDisposeOnce()
    {
        var id = Guid.Parse("13200000-0000-0000-0000-000000000003");
        DirectFixture? fixture = null;
        ProcessingRunRequest? pendingRequest = null;
        CancellationToken pendingToken = default;
        bool firstCancel = false;
        bool secondCancel = false;
        fixture = new DirectFixture([id], onPending: request =>
        {
            pendingRequest = fixture!.Coordinator.ActiveRequest;
            pendingToken = fixture!.Cancellations.Created.Single().Token;
            firstCancel = fixture!.Coordinator.CancelActiveRun();
            secondCancel = fixture!.Coordinator.CancelActiveRun();
        });
        var plan = fixture.Executor.Enqueue(completeOnCancellation: true);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var invocation = await plan.Entered.Task.WaitAsync(TestTimeout);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);

        Assert.IsTrue(firstCancel);
        Assert.IsTrue(secondCancel);
        Assert.AreSame(invocation.Request, pendingRequest);
        Assert.AreEqual(invocation.Token, pendingToken);
        Assert.IsTrue(invocation.Token.IsCancellationRequested);
        var cancellation = fixture.Cancellations.Created.Single();
        Assert.AreEqual(1, cancellation.CancelCount);
        Assert.AreEqual(1, cancellation.DisposeCount);
        var terminal = fixture.Events.OfType<RunFinished>().Single();
        Assert.AreSame(invocation.Request, terminal.Request);
        Assert.AreSame(invocation.Request, terminal.Result.Request);
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, terminal.Result.Outcome);
        Assert.AreEqual(0, fixture.Logger.Entries.Count);
        Assert.AreEqual(6, fixture.NotificationCount);
        Assert.AreEqual(0, fixture.ControlOperations.Count(item => item.StartsWith("abandon:", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(
            new[] { $"id:{id}", "cts:create:Manual", $"pending:{id}", $"cts:cancel:{id}", $"arm:{id}", $"dispatch:{id}", $"event:RunStarted:{id}", $"event:EligibilityDetermined:{id}", $"event:RunFinished:{id}", $"release:{id}", $"cts:dispose:{id}" },
            fixture.Operations.ToArray());
        var immutable = fixture.Snapshot();
        Assert.IsFalse(fixture.Coordinator.CancelActiveRun());
        fixture.AssertSnapshot(immutable);
    }

    [TestMethod]
    public async Task IdleDuplicateCancel_HasExactEmptyArraysThenCompleteNextAcceptedRunAndImmutableCleanup()
    {
        var id = Guid.NewGuid();
        var fixture = new DirectFixture([id]);
        var empty = fixture.Snapshot();
        Assert.IsFalse(fixture.Coordinator.CancelActiveRun());
        Assert.IsFalse(fixture.Coordinator.CancelActiveRun());
        fixture.AssertSnapshot(empty);
        Assert.AreEqual(0, fixture.IdCalls);
        Assert.AreEqual(0, fixture.NotificationCount);
        Assert.AreEqual(0, fixture.Cancellations.Created.Count);
        Assert.AreEqual(0, fixture.Events.Count);
        Assert.AreEqual(0, fixture.Logger.Entries.Count);
        Assert.AreEqual(0, fixture.ControlOperations.Count);
        Assert.AreEqual(0, fixture.Executor.CallCount);
        Assert.AreEqual(0, fixture.Operations.Count);

        var plan = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var invocation = await plan.Entered.Task.WaitAsync(TestTimeout);
        plan.Release.TrySetResult();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        var terminal = fixture.Events.OfType<RunFinished>().Single();
        Assert.AreSame(invocation.Request, terminal.Request);
        Assert.AreSame(invocation.Request, terminal.Result.Request);
        Assert.AreEqual(ProcessingRunOutcome.Completed, terminal.Result.Outcome);
        Assert.AreEqual(0, fixture.Logger.Entries.Count);
        Assert.AreEqual(5, fixture.NotificationCount);
        Assert.AreEqual(1, fixture.Cancellations.Created.Single().DisposeCount);
        CollectionAssert.AreEqual(
            new[] { $"id:{id}", "cts:create:Manual", $"pending:{id}", $"arm:{id}", $"dispatch:{id}", $"event:RunStarted:{id}", $"event:EligibilityDetermined:{id}", $"event:RunFinished:{id}", $"release:{id}", $"cts:dispose:{id}" },
            fixture.Operations.ToArray());
        var immutable = fixture.Snapshot();
        Assert.IsFalse(fixture.Coordinator.CancelActiveRun());
        fixture.AssertSnapshot(immutable);
    }

    [TestMethod]
    public async Task ScheduledCancelActiveRun_HasExactOwnedTokenTerminalCleanupAndUnaffectedRetriggerArrays()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var fixture = new DirectFixture([first, second]);
        var scheduledPlan = fixture.Executor.Enqueue();
        var scheduled = ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(CancellationToken.None);
        var firstInvocation = await scheduledPlan.Entered.Task.WaitAsync(TestTimeout);
        var firstSource = fixture.Cancellations.Created.Single();
        Assert.AreEqual(ProcessingRunTrigger.Scheduled, firstInvocation.Request.Trigger);
        Assert.AreEqual(firstSource.Token, firstInvocation.Token);
        Assert.IsTrue(fixture.Coordinator.CancelActiveRun());
        Assert.IsTrue(fixture.Coordinator.CancelActiveRun());
        await scheduledPlan.CancelObserved.Task.WaitAsync(TestTimeout);
        Assert.AreEqual(1, firstSource.CancelCount);
        scheduledPlan.Release.TrySetResult();
        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await scheduled.WaitAsync(TestTimeout));

        var firstTerminal = fixture.Events.OfType<RunFinished>().Single();
        Assert.AreSame(firstInvocation.Request, firstTerminal.Request);
        Assert.AreSame(firstInvocation.Request, firstTerminal.Result.Request);
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, firstTerminal.Result.Outcome);
        Assert.AreEqual(1, firstSource.DisposeCount);
        Assert.AreEqual(0, fixture.Logger.Entries.Count);
        Assert.AreEqual(6, fixture.NotificationCount);
        CollectionAssert.AreEqual(
            new[] { $"id:{first}", "cts:create:Scheduled", $"pending:{first}", $"arm:{first}", $"dispatch:{first}", $"cts:cancel:{first}", $"event:RunStarted:{first}", $"event:EligibilityDetermined:{first}", $"event:RunFinished:{first}", $"release:{first}", $"cts:dispose:{first}" },
            fixture.Operations.ToArray());

        var recovery = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var secondInvocation = await recovery.Entered.Task.WaitAsync(TestTimeout);
        Assert.AreEqual(second, secondInvocation.Request.RunId);
        Assert.AreEqual(ProcessingRunTrigger.Manual, secondInvocation.Request.Trigger);
        Assert.AreNotEqual(firstInvocation.Token, secondInvocation.Token);
        Assert.IsFalse(secondInvocation.Token.IsCancellationRequested);
        recovery.Release.TrySetResult();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        Assert.AreEqual(1, fixture.Cancellations.Created[1].DisposeCount);
        Assert.AreEqual(0, fixture.Logger.Entries.Count);
        CollectionAssert.AreEqual(
            new[]
            {
                $"id:{first}", "cts:create:Scheduled", $"pending:{first}", $"arm:{first}", $"dispatch:{first}", $"cts:cancel:{first}", $"event:RunStarted:{first}", $"event:EligibilityDetermined:{first}", $"event:RunFinished:{first}", $"release:{first}", $"cts:dispose:{first}",
                $"id:{second}", "cts:create:Manual", $"pending:{second}", $"arm:{second}", $"dispatch:{second}", $"event:RunStarted:{second}", $"event:EligibilityDetermined:{second}", $"event:RunFinished:{second}", $"release:{second}", $"cts:dispose:{second}"
            },
            fixture.Operations.ToArray());
        var immutable = fixture.Snapshot();
        Assert.IsFalse(fixture.Coordinator.CancelActiveRun());
        fixture.AssertSnapshot(immutable);
    }

    [TestMethod]
    [DataRow("manual", false)]
    [DataRow("manual", true)]
    [DataRow("lifetime", false)]
    [DataRow("lifetime", true)]
    [DataRow("stop", false)]
    [DataRow("stop", true)]
    public async Task ThrowingCancellationRegistration_IsContainedAndExactActiveDrainStillCompletes(string path, bool directOom)
    {
        Exception cancellationFailure = directOom
            ? new OutOfMemoryException($"direct cancellation oom {path}")
            : new AggregateException("callback failure", new InvalidOperationException(path));
        var lifetime = new TestLifetime();
        var id = Guid.NewGuid();
        var fixture = new DirectFixture([id], cancelFailure: cancellationFailure, lifetime: lifetime);
        var plan = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        await plan.Entered.Task.WaitAsync(TestTimeout);
        Task? stop = null;
        using var stopBound = new CancellationTokenSource(TestTimeout);

        if (path == "manual")
        {
            Assert.IsTrue(fixture.Coordinator.CancelActiveRun());
        }
        else if (path == "lifetime")
        {
            lifetime.StopApplication();
        }
        else
        {
            stop = fixture.Coordinator.StopAsync(stopBound.Token);
        }

        await plan.CancelObserved.Task.WaitAsync(TestTimeout);
        if (stop is not null)
        {
            Assert.IsFalse(stop.IsCompleted);
        }
        plan.Release.TrySetResult();
        if (stop is not null)
        {
            await stop.WaitAsync(TestTimeout);
        }
        else
        {
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        }

        var cancellation = fixture.Cancellations.Created.Single();
        Assert.AreEqual(1, cancellation.CancelCount);
        Assert.AreEqual(1, cancellation.DisposeCount);
        Assert.AreEqual(1, fixture.Logger.Entries.Count(entry => ReferenceEquals(entry.Exception, cancellationFailure)));
        Assert.AreEqual(directOom ? LogLevel.Critical : LogLevel.Error, fixture.Logger.Entries.Single().Level);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        var terminal = fixture.Events.OfType<RunFinished>().Single();
        Assert.AreSame(terminal.Request, terminal.Result.Request);
        Assert.AreEqual(id, terminal.Request.RunId);
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, terminal.Result.Outcome);
        CollectionAssert.AreEqual(
            new[] { $"id:{id}", "cts:create:Manual", $"pending:{id}", $"arm:{id}", $"dispatch:{id}", $"cts:cancel:{id}", $"event:RunStarted:{id}", $"event:EligibilityDetermined:{id}", $"event:RunFinished:{id}", $"release:{id}", $"cts:dispose:{id}" },
            fixture.Operations.ToArray());
    }

    [TestMethod]
    public async Task CancellationCallbackFailure_DoesNotReplacePrimaryOutOfMemoryFailure()
    {
        var cancellationFailure = new AggregateException("cancel callbacks");
        var primary = new OutOfMemoryException("primary execution oom");
        var fixture = new DirectFixture([Guid.NewGuid()], cancelFailure: cancellationFailure);
        var plan = fixture.Executor.Enqueue(asyncFailure: primary);
        var scheduled = ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(CancellationToken.None);
        await plan.Entered.Task.WaitAsync(TestTimeout);
        Assert.IsTrue(fixture.Coordinator.CancelActiveRun());
        plan.Release.TrySetResult();

        var observed = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() => scheduled.WaitAsync(TestTimeout));

        Assert.AreSame(primary, observed);
        Assert.AreEqual(1, fixture.Logger.Entries.Count(entry => ReferenceEquals(entry.Exception, cancellationFailure)));
        Assert.AreEqual(1, fixture.Logger.Entries.Count(entry => ReferenceEquals(entry.Exception, primary)));
        Assert.AreEqual(1, fixture.Cancellations.Created.Single().DisposeCount);
    }

    [TestMethod]
    public async Task PendingNotificationFailure_RecoversExactUnarmedStateCleansAndRetriggers()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var failure = new InvalidOperationException("pending notification");
        var fixture = new DirectFixture([first, second], onPending: request =>
        {
            if (request.RunId == first)
            {
                throw failure;
            }
        });

        var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Coordinator.TriggerManualAsync());
        Assert.AreSame(failure, observed);
        Assert.IsFalse(fixture.State.IsRunning);
        Assert.AreEqual($"Fatal: {failure.Message}", fixture.State.LastError);
        CollectionAssert.AreEqual(
            new[] { $"id:{first}", "cts:create:Manual", $"pending:{first}", $"cts:dispose:{first}" },
            fixture.Operations.ToArray());
        Assert.AreEqual(1, fixture.Logger.Entries.Count(entry => ReferenceEquals(entry.Exception, failure)));

        var recovery = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var invocation = await recovery.Entered.Task.WaitAsync(TestTimeout);
        Assert.AreEqual(fixture.Cancellations.Created[1].Token, invocation.Token);
        recovery.Release.TrySetResult();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        var terminal = fixture.Events.OfType<RunFinished>().Single();
        Assert.AreSame(invocation.Request, terminal.Request);
        Assert.AreSame(invocation.Request, terminal.Result.Request);
        Assert.AreEqual(ProcessingRunOutcome.Completed, terminal.Result.Outcome);
        CollectionAssert.AreEqual(
            new[] { new CapturedLog(LogLevel.Error, $"Processing run {first} faulted outside a domain terminal", failure) },
            fixture.Logger.Entries.ToArray());
        CollectionAssert.AreEqual(
            new[] { $"id:{first}", "cts:create:Manual", $"pending:{first}", $"cts:dispose:{first}", $"id:{second}", "cts:create:Manual", $"pending:{second}", $"arm:{second}", $"dispatch:{second}", $"event:RunStarted:{second}", $"event:EligibilityDetermined:{second}", $"event:RunFinished:{second}", $"release:{second}", $"cts:dispose:{second}" },
            fixture.Operations.ToArray());
        Assert.AreEqual(1, fixture.Cancellations.Created[0].DisposeCount);
        Assert.AreEqual(1, fixture.Cancellations.Created[1].DisposeCount);
        Assert.AreEqual(7, fixture.NotificationCount);
    }

    [TestMethod]
    public async Task ArmRejection_RollsBackPendingCleansExactHandleAndLaterRetriggersAfterForeignRelease()
    {
        var foreign = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var fixture = new DirectFixture([first, second]);
        Assert.IsTrue(fixture.Reporter.Arm(foreign));

        var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Coordinator.TriggerManualAsync());
        Assert.AreEqual("Processing event reporter is already armed.", observed.Message);
        Assert.IsFalse(fixture.State.IsRunning);
        Assert.AreEqual(0, fixture.Executor.CallCount);
        Assert.AreEqual(1, fixture.Cancellations.Created[0].DisposeCount);
        Assert.IsTrue(fixture.Reporter.Abandon(foreign, new InvalidOperationException("foreign release")));

        var recovery = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var invocation = await recovery.Entered.Task.WaitAsync(TestTimeout);
        Assert.AreEqual(fixture.Cancellations.Created[1].Token, invocation.Token);
        recovery.Release.TrySetResult();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        var terminal = fixture.Events.OfType<RunFinished>().Single();
        Assert.AreSame(invocation.Request, terminal.Request);
        Assert.AreSame(invocation.Request, terminal.Result.Request);
        Assert.AreEqual(ProcessingRunOutcome.Completed, terminal.Result.Outcome);
        CollectionAssert.AreEqual(
            new[] { new CapturedLog(LogLevel.Error, $"Processing run {first} faulted outside a domain terminal", observed) },
            fixture.Logger.Entries.ToArray());
        CollectionAssert.AreEqual(
            new[] { $"arm:{foreign.RunId}", $"id:{first}", "cts:create:Manual", $"pending:{first}", $"cts:dispose:{first}", $"abandon:{foreign.RunId}", $"release:{foreign.RunId}", $"id:{second}", "cts:create:Manual", $"pending:{second}", $"arm:{second}", $"dispatch:{second}", $"event:RunStarted:{second}", $"event:EligibilityDetermined:{second}", $"event:RunFinished:{second}", $"release:{second}", $"cts:dispose:{second}" },
            fixture.Operations.ToArray());
        Assert.AreEqual(1, fixture.Cancellations.Created[0].DisposeCount);
        Assert.AreEqual(1, fixture.Cancellations.Created[1].DisposeCount);
        Assert.AreEqual(8, fixture.NotificationCount);
    }

    [TestMethod]
    public async Task ReporterArmCallbackFailure_AbandonsExactArmCleansOnceAndRetriggers()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var failure = new InvalidOperationException("arm callback");
        var fixture = new DirectFixture([first, second], controlAction: (operation, request) =>
        {
            if (operation == "arm" && request.RunId == first)
            {
                throw failure;
            }
        });

        var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Coordinator.TriggerManualAsync());
        Assert.AreSame(failure, observed);
        Assert.IsFalse(fixture.State.IsRunning);
        Assert.AreEqual($"Fatal: {failure.Message}", fixture.State.LastError);
        Assert.AreEqual(1, fixture.Logger.Entries.Count(entry => ReferenceEquals(entry.Exception, failure)));
        Assert.AreEqual(1, fixture.Cancellations.Created[0].DisposeCount);
        CollectionAssert.AreEqual(
            new[] { $"id:{first}", "cts:create:Manual", $"pending:{first}", $"arm:{first}", $"abandon:{first}", $"release:{first}", $"cts:dispose:{first}" },
            fixture.Operations.ToArray());

        var recovery = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var invocation = await recovery.Entered.Task.WaitAsync(TestTimeout);
        Assert.AreEqual(fixture.Cancellations.Created[1].Token, invocation.Token);
        recovery.Release.TrySetResult();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        var terminal = fixture.Events.OfType<RunFinished>().Single();
        Assert.AreSame(invocation.Request, terminal.Request);
        Assert.AreSame(invocation.Request, terminal.Result.Request);
        Assert.AreEqual(ProcessingRunOutcome.Completed, terminal.Result.Outcome);
        CollectionAssert.AreEqual(
            new[] { new CapturedLog(LogLevel.Error, $"Processing run {first} faulted outside a domain terminal", failure) },
            fixture.Logger.Entries.ToArray());
        CollectionAssert.AreEqual(
            new[] { $"id:{first}", "cts:create:Manual", $"pending:{first}", $"arm:{first}", $"abandon:{first}", $"release:{first}", $"cts:dispose:{first}", $"id:{second}", "cts:create:Manual", $"pending:{second}", $"arm:{second}", $"dispatch:{second}", $"event:RunStarted:{second}", $"event:EligibilityDetermined:{second}", $"event:RunFinished:{second}", $"release:{second}", $"cts:dispose:{second}" },
            fixture.Operations.ToArray());
        Assert.AreEqual(1, fixture.Cancellations.Created[0].DisposeCount);
        Assert.AreEqual(1, fixture.Cancellations.Created[1].DisposeCount);
        Assert.AreEqual(7, fixture.NotificationCount);
    }

    [TestMethod]
    public async Task CancellationDuringArm_TargetsPublishedExactTokenBeforeSingleDispatchAndTerminalCleanup()
    {
        var id = Guid.NewGuid();
        DirectFixture? fixture = null;
        fixture = new DirectFixture([id], controlAction: (operation, _) =>
        {
            if (operation == "arm")
            {
                Assert.IsTrue(fixture!.Coordinator.CancelActiveRun());
            }
        });
        var plan = fixture.Executor.Enqueue();

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        await plan.CancelObserved.Task.WaitAsync(TestTimeout);
        plan.Release.TrySetResult();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, fixture.Events.OfType<RunFinished>().Single().Result.Outcome);
        CollectionAssert.AreEqual(
            new[] { $"id:{id}", "cts:create:Manual", $"pending:{id}", $"arm:{id}", $"cts:cancel:{id}", $"dispatch:{id}", $"event:RunStarted:{id}", $"event:EligibilityDetermined:{id}", $"event:RunFinished:{id}", $"release:{id}", $"cts:dispose:{id}" },
            fixture.Operations.ToArray());
    }

    [TestMethod]
    [DataRow(4)]
    [DataRow(5)]
    public async Task RealExecutorTerminalOnChangedFailure_RecoversLogsOnceCleansAndRetriggers(int terminalNotification)
    {
        var ids = new Queue<Guid>([Guid.NewGuid(), Guid.NewGuid()]);
        var operations = new ConcurrentQueue<string>();
        var state = new ProcessingState();
        var callbackFailure = new InvalidOperationException($"terminal notification {terminalNotification}");
        var notifications = new ThrowOnExactNotification(terminalNotification, callbackFailure);
        state.OnChanged += notifications.Invoke;
        var events = new ConcurrentQueue<ProcessingEvent>();
        var controls = new ConcurrentQueue<string>();
        var reporter = new ProcessingStateEventReporter(
            state,
            processingEvent =>
            {
                events.Enqueue(processingEvent);
                operations.Enqueue($"event:{processingEvent.GetType().Name}:{processingEvent.Request.RunId}");
            },
            (operation, request) =>
            {
                controls.Enqueue($"{operation}:{request.RunId}");
                operations.Enqueue($"{operation}:{request.RunId}");
            });
        var collaborators = ZeroOperations();
        var realExecutor = new ProcessingRunExecutor(
            NullLogger<ProcessingBackgroundService>.Instance,
            collaborators, collaborators, collaborators, collaborators, collaborators, collaborators,
            TimeProvider.System);
        var executor = new RecordingDelegatingExecutor(realExecutor, operations);
        var cancellationFactory = new RecordingCancellationFactory(operations, null, null);
        var logger = new CaptureLogger();
        Guid NextId()
        {
            var id = ids.Dequeue();
            operations.Enqueue($"id:{id}");
            return id;
        }
        var coordinator = new ProcessingRunCoordinator(state, reporter, executor, logger, NextId, cancellationFactory, null);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await coordinator.TriggerManualAsync());
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);

        Assert.AreEqual(1, logger.Entries.Count);
        Assert.AreSame(callbackFailure, logger.Entries.Single().Exception);
        Assert.IsFalse(state.IsRunning);
        Assert.AreEqual($"Fatal: {callbackFailure.Message}", state.LastError);
        Assert.AreEqual(1, events.OfType<RunFinished>().Count());
        Assert.AreEqual(0, controls.Count(item => item.StartsWith("abandon:", StringComparison.Ordinal)));
        Assert.AreEqual(1, controls.Count(item => item.StartsWith("release:", StringComparison.Ordinal)));
        Assert.AreEqual(1, cancellationFactory.Created[0].DisposeCount);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await coordinator.TriggerManualAsync());
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        Assert.AreEqual(2, executor.CallCount);
        Assert.AreEqual(2, events.OfType<RunFinished>().Count());
        Assert.AreEqual(2, cancellationFactory.Created.Count);
        Assert.AreEqual(1, cancellationFactory.Created[1].DisposeCount);
        Assert.AreEqual(2, controls.Count(item => item.StartsWith("release:", StringComparison.Ordinal)));
        Assert.AreEqual(0, controls.Count(item => item.StartsWith("abandon:", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("sync")]
    [DataRow("async")]
    [DataRow("foreign-oce")]
    [DataRow("default-oce")]
    [DataRow("oom")]
    [DataRow("mismatch")]
    [DataRow("open")]
    [DataRow("session")]
    [DataRow("terminal-projection")]
    [DataRow("abandon")]
    [DataRow("cleanup")]
    [DataRow("dispose-after-primary")]
    [DataRow("before-detach-after-primary")]
    [DataRow("observer-after-primary")]
    public async Task CoordinatorFailureMatrix_PreservesOriginalCleansOnceLogsAndRetriggers(string boundary)
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        using var foreignSource = new CancellationTokenSource();
        Exception failure = boundary switch
        {
            "foreign-oce" => new OperationCanceledException("foreign", null, foreignSource.Token),
            "default-oce" => new OperationCanceledException("default"),
            "oom" => new OutOfMemoryException("execution oom"),
            _ => new InvalidOperationException(boundary)
        };
        var cleanupFailure = new InvalidOperationException($"{boundary} cleanup");
        IProcessingRunCoordinatorObserver? cleanupObserver = boundary switch
        {
            "before-detach-after-primary" => new ThrowingCleanupObserver(cleanupFailure, true),
            "observer-after-primary" => new ThrowingCleanupObserver(cleanupFailure, false),
            _ => null
        };
        var projectionType = boundary switch
        {
            "open" => typeof(RunStarted),
            "session" => typeof(EligibilityDetermined),
            "terminal-projection" => typeof(RunFinished),
            _ => null
        };
        var fixture = new DirectFixture(
            [firstId, secondId],
            projectionFailureType: projectionType,
            projectionFailure: failure,
            abandonFailure: boundary == "abandon" ? cleanupFailure : null,
            disposeFailure: boundary == "cleanup" ? failure : boundary == "dispose-after-primary" ? cleanupFailure : null,
            observer: cleanupObserver);
        var plan = fixture.Executor.Enqueue(
            synchronousFailure: boundary == "sync" ? failure : null,
            asyncFailure: boundary is "async" or "foreign-oce" or "default-oce" or "oom" or "abandon" or "dispose-after-primary" or "before-detach-after-primary" or "observer-after-primary" ? failure : null,
            mismatchedResult: boundary == "mismatch");

        Exception observed;
        Invocation? firstInvocation = null;
        if (boundary == "sync")
        {
            observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Coordinator.TriggerManualAsync());
        }
        else
        {
            var scheduled = ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(CancellationToken.None);
            firstInvocation = await plan.Entered.Task.WaitAsync(TestTimeout);
            plan.Release.TrySetResult();
            observed = await Assert.ThrowsAsync<Exception>(() => scheduled.WaitAsync(TestTimeout));
        }

        if (boundary is not "mismatch")
        {
            Assert.AreSame(failure, observed);
        }
        else
        {
            var mismatch = Assert.IsInstanceOfType<InvalidOperationException>(observed);
            Assert.AreEqual("The processing executor returned a result for a different request.", mismatch.Message);
            Assert.IsNotNull(plan.ReturnedRequest);
            Assert.IsNotNull(firstInvocation);
            Assert.AreNotSame(firstInvocation.Request, plan.ReturnedRequest);
            Assert.AreEqual(firstInvocation.Request.Trigger, plan.ReturnedRequest.Trigger);
            Assert.AreNotEqual(firstInvocation.Request.RunId, plan.ReturnedRequest.RunId);
        }
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        Assert.AreEqual(1, fixture.Cancellations.Created[0].DisposeCount);
        Assert.AreEqual(boundary is "terminal-projection" or "cleanup" ? 1 : 0, fixture.Events.OfType<RunFinished>().Count());
        var hasSecondaryCleanupFailure = boundary is "abandon" or "dispose-after-primary" or "before-detach-after-primary" or "observer-after-primary";
        Assert.AreEqual(hasSecondaryCleanupFailure ? 2 : 1, fixture.Logger.Entries.Count);
        Assert.AreEqual(boundary == "mismatch" ? 0 : 1,
            fixture.Logger.Entries.Count(entry => ReferenceEquals(entry.Exception, failure)));
        Assert.AreEqual(hasSecondaryCleanupFailure ? 1 : 0,
            fixture.Logger.Entries.Count(entry => ReferenceEquals(entry.Exception, cleanupFailure)));
        CollectionAssert.AreEqual(
            ExpectedFailureLogs(boundary, firstId, failure, cleanupFailure, observed),
            fixture.Logger.Entries.ToArray());
        Assert.AreEqual(boundary == "cleanup" ? 0 : 1,
            fixture.ControlOperations.Count(item => item == $"abandon:{firstId}"));

        var recovery = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var recoveryInvocation = await recovery.Entered.Task.WaitAsync(TestTimeout);
        Assert.AreEqual(secondId, recoveryInvocation.Request.RunId);
        recovery.Release.TrySetResult();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        Assert.AreEqual(2, fixture.Cancellations.Created.Count);
        Assert.AreEqual(1, fixture.Cancellations.Created[1].DisposeCount);
        CollectionAssert.AreEqual(ExpectedFailureAndRetriggerOperations(boundary, firstId, secondId), fixture.Operations.ToArray(), string.Join(" | ", fixture.Operations));
        var expectedFirstEventCount = boundary switch
        {
            "open" => 1,
            "session" => 2,
            "terminal-projection" or "cleanup" => 3,
            _ => 0
        };
        Assert.AreEqual(expectedFirstEventCount + 3, fixture.Events.Count);
        CollectionAssert.AreEqual(
            Enumerable.Repeat(firstId, expectedFirstEventCount).Concat(Enumerable.Repeat(secondId, 3)).ToArray(),
            fixture.Events.Select(item => item.Request.RunId).ToArray());
        Assert.IsTrue(fixture.Events.OfType<RunFinished>().All(item => ReferenceEquals(item.Request, item.Result.Request)));
    }

    [TestMethod]
    [DataRow(ProcessingRunOutcome.Completed)]
    [DataRow(ProcessingRunOutcome.Cancelled)]
    [DataRow(ProcessingRunOutcome.Failed)]
    public async Task DomainTerminalOutcomes_HaveExactResultEventsCleanupAndNewIdentity(ProcessingRunOutcome outcome)
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var fixture = new DirectFixture([first, second]);
        var plan = fixture.Executor.Enqueue(forcedOutcome: outcome);
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var invocation = await plan.Entered.Task.WaitAsync(TestTimeout);
        if (outcome == ProcessingRunOutcome.Cancelled)
        {
            Assert.IsTrue(fixture.Coordinator.CancelActiveRun());
            await plan.CancelObserved.Task.WaitAsync(TestTimeout);
        }
        plan.Release.TrySetResult();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);

        var terminal = fixture.Events.OfType<RunFinished>().Single();
        Assert.AreSame(invocation.Request, terminal.Request);
        Assert.AreSame(invocation.Request, terminal.Result.Request);
        Assert.AreEqual(outcome, terminal.Result.Outcome);
        Assert.AreEqual(1, fixture.Cancellations.Created[0].DisposeCount);
        Assert.AreEqual(1, fixture.Executor.CallCount);
        Assert.AreEqual(0, fixture.ControlOperations.Count(item => item.StartsWith("abandon:", StringComparison.Ordinal)));
        var expected = new List<string>
        {
            $"id:{first}", "cts:create:Manual", $"pending:{first}", $"arm:{first}", $"dispatch:{first}"
        };
        if (outcome == ProcessingRunOutcome.Cancelled)
        {
            expected.Add($"cts:cancel:{first}");
        }
        expected.AddRange([$"event:RunStarted:{first}", $"event:EligibilityDetermined:{first}", $"event:RunFinished:{first}", $"release:{first}", $"cts:dispose:{first}"]);
        CollectionAssert.AreEqual(expected, fixture.Operations.ToArray());

        var recovery = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var recoveryInvocation = await recovery.Entered.Task.WaitAsync(TestTimeout);
        Assert.AreEqual(second, recoveryInvocation.Request.RunId);
        recovery.Release.TrySetResult();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        Assert.AreEqual(2, fixture.Events.OfType<RunFinished>().Count());
        Assert.AreEqual(2, fixture.Executor.CallCount);
        Assert.AreEqual(1, fixture.Cancellations.Created[1].DisposeCount);
    }

    [TestMethod]
    public async Task ProjectionIdleBeforeDetach_RemainsAlreadyRunningUntilExactCleanupThenRetriggers()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var observer = new PreDetachBarrierObserver(first);
        var fixture = new DirectFixture([first, second], observer: observer);
        var plan = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        plan.Release.TrySetResult();
        await observer.Entered.Task.WaitAsync(TestTimeout);
        Assert.IsFalse(fixture.State.IsRunning);
        Assert.AreEqual(first, fixture.Coordinator.ActiveRequest!.RunId);
        var frozen = fixture.Snapshot();

        Assert.AreEqual(ProcessingRunAdmissionResult.AlreadyRunning, await fixture.Coordinator.TriggerManualAsync());
        Assert.AreEqual(ScheduledTriggerResult.RejectedAlreadyRunning,
            await ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(CancellationToken.None));
        Assert.AreEqual(frozen.IdCalls, fixture.IdCalls);
        Assert.AreEqual(frozen.CtsCreates, fixture.Cancellations.Created.Count);
        Assert.AreEqual(frozen.Dispatches, fixture.Executor.CallCount);
        Assert.AreEqual(frozen.Events, fixture.Events.Count);
        CollectionAssert.AreEqual(frozen.Operations, fixture.Operations.ToArray());
        var messages = fixture.Messages();
        Assert.AreEqual(frozen.Messages + 1, messages.Length);
        Assert.AreEqual("Scheduled run skipped because a processing pass is already in progress.", messages[^1]);

        observer.Release.TrySetResult();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        var recovery = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var invocation = await recovery.Entered.Task.WaitAsync(TestTimeout);
        Assert.AreEqual(second, invocation.Request.RunId);
        recovery.Release.TrySetResult();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task OldDisposalCompletesBeforeNewReservationAndCannotAffectNewHandle()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var observer = new GatedCleanupObserver(oldId);
        var fixture = new DirectFixture([oldId, newId], observer: observer);
        var oldPlan = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var oldInvocation = await oldPlan.Entered.Task.WaitAsync(TestTimeout);
        var oldCleanup = fixture.Coordinator.WaitForActiveRunAsync();
        oldPlan.Release.TrySetResult();
        await observer.Entered.Task.WaitAsync(TestTimeout);
        Assert.AreSame(oldInvocation.Request, fixture.Coordinator.ActiveRequest);
        Assert.AreEqual(ProcessingRunAdmissionResult.AlreadyRunning, await fixture.Coordinator.TriggerManualAsync());
        Assert.AreEqual(1, fixture.IdCalls);

        observer.Release.TrySetResult();
        await observer.Completed.Task.WaitAsync(TestTimeout);
        await oldCleanup.WaitAsync(TestTimeout);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        Assert.AreEqual(1, fixture.Cancellations.Created[0].DisposeCount);

        var newPlan = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var newInvocation = await newPlan.Entered.Task.WaitAsync(TestTimeout);
        Assert.AreEqual(newId, newInvocation.Request.RunId);
        Assert.IsFalse(newInvocation.Token.IsCancellationRequested);
        Assert.AreSame(newInvocation.Request, fixture.Coordinator.ActiveRequest);
        Assert.IsFalse(newInvocation.Token.IsCancellationRequested);
        Assert.AreEqual(0, fixture.Cancellations.Created[1].DisposeCount);
        Assert.AreEqual(0, fixture.ControlOperations.Count(item => item == $"abandon:{newId}"));

        var newCleanup = fixture.Coordinator.WaitForActiveRunAsync();
        newPlan.Release.TrySetResult();
        await newCleanup.WaitAsync(TestTimeout);
        Assert.AreEqual(1, fixture.Cancellations.Created[1].DisposeCount);
        Assert.AreNotEqual(oldInvocation.Token, newInvocation.Token);
    }

    [TestMethod]
    public async Task ShutdownAdmissionLinearizations_AreExactlyAdmittedThenCancelledOrStoppingWithoutFabrication()
    {
        var admitted = new DirectFixture([Guid.NewGuid()]);
        var plan = admitted.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await admitted.Coordinator.TriggerManualAsync());
        await plan.Entered.Task.WaitAsync(TestTimeout);
        using var admittedBound = new CancellationTokenSource(TestTimeout);
        var drain = admitted.Coordinator.StopAsync(admittedBound.Token);
        await plan.CancelObserved.Task.WaitAsync(TestTimeout);
        plan.Release.TrySetResult();
        await drain.WaitAsync(TestTimeout);
        Assert.AreEqual(1, admitted.Cancellations.Created.Single().CancelCount);
        Assert.AreEqual(1, admitted.Cancellations.Created.Single().DisposeCount);

        var stopped = new DirectFixture([Guid.NewGuid()]);
        using var stoppedBound = new CancellationTokenSource(TestTimeout);
        await stopped.Coordinator.StopAsync(stoppedBound.Token);
        var before = stopped.Snapshot();
        Assert.AreEqual(ProcessingRunAdmissionResult.Stopping, await stopped.Coordinator.TriggerManualAsync());
        Assert.IsFalse(stopped.Coordinator.CancelActiveRun());
        stopped.AssertSnapshot(before);
    }

    [TestMethod]
    [DataRow("manual")]
    [DataRow("lifetime")]
    [DataRow("stop")]
    public async Task CrossThreadCancellationCallback_ReentersEveryRequestPathWithoutDeadlockOrDuplicate(string path)
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var lifetime = new TestLifetime();
        var observer = new CancellationRaceObserver();
        var overlap = new CancellationOverlapGate();
        overlap.ReleaseCancel();
        var fixture = new DirectFixture([first, second], lifetime: lifetime, overlapGate: overlap, observer: observer);
        var plan = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var invocation = await plan.Entered.Task.WaitAsync(TestTimeout);
        var ownedToken = fixture.Cancellations.Created[0].Token;
        Task? secondaryRequest = null;
        var secondaryManualResult = false;
        using var stopBound = new CancellationTokenSource(TestTimeout);
        using var crossThreadCallback = invocation.Token.Register(() =>
        {
            fixture.Operations.Enqueue($"cross-enter:{first}:{path}");
            secondaryRequest = path switch
            {
                "manual" => Task.Run(() => secondaryManualResult = fixture.Coordinator.CancelActiveRun()),
                "lifetime" => Task.Run(lifetime.StopApplication),
                _ => Task.Run(async () => await fixture.Coordinator.StopAsync(stopBound.Token))
            };
            if (!observer.RequestReturned.Wait(TestTimeout))
            {
                throw new TimeoutException("Cross-thread cancellation request did not return from the active handle.");
            }
            fixture.Operations.Enqueue($"cross-exit:{first}:{path}");
        });

        try
        {
            await Task.Run(() => Assert.IsTrue(fixture.Coordinator.CancelActiveRun())).WaitAsync(TestTimeout);
            Assert.IsNotNull(secondaryRequest);
            if (path == "manual")
            {
                await secondaryRequest.WaitAsync(TestTimeout);
                Assert.IsTrue(secondaryManualResult);
            }
            Assert.AreEqual(1, fixture.Cancellations.Created[0].CancelCount);
            var drain = fixture.Coordinator.WaitForActiveRunAsync();
            plan.Release.TrySetResult();
            await drain.WaitAsync(TestTimeout);
            await secondaryRequest.WaitAsync(TestTimeout);
        }
        finally
        {
            overlap.ReleaseCancel();
            plan.Release.TrySetResult();
        }

        var terminal = fixture.Events.OfType<RunFinished>().Single();
        Assert.AreSame(invocation.Request, terminal.Request);
        Assert.AreSame(invocation.Request, terminal.Result.Request);
        Assert.AreEqual(ownedToken, invocation.Token);
        CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(RunFinished) }, fixture.Events.Select(item => item.GetType()).ToArray());
        Assert.IsTrue(fixture.Events.All(item => ReferenceEquals(invocation.Request, item.Request)));
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, terminal.Result.Outcome);
        Assert.AreEqual(1, fixture.Cancellations.Created[0].CancelCount);
        Assert.AreEqual(1, fixture.Cancellations.Created[0].DisposeCount);
        Assert.AreEqual(0, fixture.Logger.Entries.Count);
        CollectionAssert.AreEqual(
            new[] { $"id:{first}", "cts:create:Manual", $"pending:{first}", $"arm:{first}", $"dispatch:{first}", $"cancel-enter:{first}", $"cross-enter:{first}:{path}", $"cross-exit:{first}:{path}", $"callback:{first}", $"cancel-exit:{first}", $"event:RunStarted:{first}", $"event:EligibilityDetermined:{first}", $"event:RunFinished:{first}", $"release:{first}", $"dispose-enter:{first}", $"dispose-exit:{first}" },
            fixture.Operations.ToArray());

        if (path == "manual")
        {
            var recovery = fixture.Executor.Enqueue();
            Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
            var recoveryInvocation = await recovery.Entered.Task.WaitAsync(TestTimeout);
            Assert.AreEqual(second, recoveryInvocation.Request.RunId);
            var recoveryDrain = fixture.Coordinator.WaitForActiveRunAsync();
            recovery.Release.TrySetResult();
            await recoveryDrain.WaitAsync(TestTimeout);
            Assert.AreEqual(1, fixture.Cancellations.Created[1].DisposeCount);
            CollectionAssert.AreEqual(
                new[]
                {
                    $"id:{first}", "cts:create:Manual", $"pending:{first}", $"arm:{first}", $"dispatch:{first}", $"cancel-enter:{first}", $"cross-enter:{first}:{path}", $"cross-exit:{first}:{path}", $"callback:{first}", $"cancel-exit:{first}", $"event:RunStarted:{first}", $"event:EligibilityDetermined:{first}", $"event:RunFinished:{first}", $"release:{first}", $"dispose-enter:{first}", $"dispose-exit:{first}",
                    $"id:{second}", "cts:create:Manual", $"pending:{second}", $"arm:{second}", $"dispatch:{second}", $"event:RunStarted:{second}", $"event:EligibilityDetermined:{second}", $"event:RunFinished:{second}", $"release:{second}", $"dispose-enter:{second}", $"dispose-exit:{second}"
                }, fixture.Operations.ToArray());
            var immutable = fixture.Snapshot();
            Assert.IsFalse(fixture.Coordinator.CancelActiveRun());
            fixture.AssertSnapshot(immutable);
        }
        else
        {
            var immutable = fixture.Snapshot();
            Assert.AreEqual(ProcessingRunAdmissionResult.Stopping, await fixture.Coordinator.TriggerManualAsync());
            Assert.IsFalse(fixture.Coordinator.CancelActiveRun());
            fixture.AssertSnapshot(immutable);
        }
    }

    [TestMethod]
    [DataRow("manual")]
    [DataRow("lifetime")]
    [DataRow("stop")]
    public async Task CancelInsideOwnedSource_SerializesBeforeDisposeForEveryRequestPath(string path)
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var lifetime = new TestLifetime();
        var observer = new CancellationRaceObserver();
        var overlap = new CancellationOverlapGate();
        var fixture = new DirectFixture([first, second], lifetime: lifetime, overlapGate: overlap, observer: observer);
        var plan = fixture.Executor.Enqueue(completeOnCancellation: true);
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var invocation = await plan.Entered.Task.WaitAsync(TestTimeout);
        var ownedToken = fixture.Cancellations.Created[0].Token;
        Assert.AreEqual(ownedToken, invocation.Token);
        var reentrantCancel = false;
        using var reentrant = invocation.Token.Register(() => reentrantCancel = fixture.Coordinator.CancelActiveRun());
        using var stopBound = new CancellationTokenSource(TestTimeout);
        Task cancellationRequest;
        if (path == "manual")
        {
            cancellationRequest = Task.Run(() => Assert.IsTrue(fixture.Coordinator.CancelActiveRun()));
        }
        else if (path == "lifetime")
        {
            cancellationRequest = Task.Run(lifetime.StopApplication);
        }
        else
        {
            cancellationRequest = Task.Run(async () => await fixture.Coordinator.StopAsync(stopBound.Token));
        }

        try
        {
            await overlap.CancelEntered.Task.WaitAsync(TestTimeout);
            await overlap.CallbackCompleted.Task.WaitAsync(TestTimeout);
            Assert.IsTrue(reentrantCancel);
            await observer.DisposeAttempted.Task.WaitAsync(TestTimeout);
            Assert.AreSame(invocation.Request, fixture.Coordinator.ActiveRequest);
            Assert.AreEqual(1, fixture.IdCalls);
            Assert.AreEqual(path == "manual" ? ProcessingRunAdmissionResult.AlreadyRunning : ProcessingRunAdmissionResult.Stopping, await fixture.Coordinator.TriggerManualAsync());
            Assert.AreEqual(1, fixture.IdCalls);
            Assert.IsFalse(overlap.DisposeEntered.Task.IsCompleted);
            Assert.IsFalse(cancellationRequest.IsCompleted);
        }
        finally
        {
            overlap.ReleaseCancel();
            plan.Release.TrySetResult();
        }
        await cancellationRequest.WaitAsync(TestTimeout);
        await overlap.DisposeExited.Task.WaitAsync(TestTimeout);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);

        var source = fixture.Cancellations.Created[0];
        var terminal = fixture.Events.OfType<RunFinished>().Single();
        Assert.AreSame(invocation.Request, terminal.Request);
        Assert.AreSame(invocation.Request, terminal.Result.Request);
        Assert.AreEqual(ownedToken, invocation.Token);
        CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(RunFinished) }, fixture.Events.Select(item => item.GetType()).ToArray());
        Assert.IsTrue(fixture.Events.All(item => ReferenceEquals(invocation.Request, item.Request)));
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, terminal.Result.Outcome);
        Assert.AreEqual(1, source.CancelCount);
        Assert.AreEqual(1, source.DisposeCount);
        Assert.AreEqual(0, fixture.Logger.Entries.Count);
        CollectionAssert.AreEqual(
            new[] { $"id:{first}", "cts:create:Manual", $"pending:{first}", $"arm:{first}", $"dispatch:{first}", $"cancel-enter:{first}", $"callback:{first}", $"event:RunStarted:{first}", $"event:EligibilityDetermined:{first}", $"event:RunFinished:{first}", $"release:{first}", $"cancel-exit:{first}", $"dispose-enter:{first}", $"dispose-exit:{first}" },
            fixture.Operations.ToArray());

        if (path == "manual")
        {
            var recovery = fixture.Executor.Enqueue();
            Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
            var recoveryInvocation = await recovery.Entered.Task.WaitAsync(TestTimeout);
            Assert.AreEqual(second, recoveryInvocation.Request.RunId);
            Assert.IsFalse(recoveryInvocation.Token.IsCancellationRequested);
            var recoveryDrain = fixture.Coordinator.WaitForActiveRunAsync();
            recovery.Release.TrySetResult();
            await recoveryDrain.WaitAsync(TestTimeout);
            Assert.AreEqual(2, fixture.Events.OfType<RunFinished>().Count());
            Assert.AreEqual(1, fixture.Cancellations.Created[1].DisposeCount);
            CollectionAssert.AreEqual(
                new[]
                {
                    $"id:{first}", "cts:create:Manual", $"pending:{first}", $"arm:{first}", $"dispatch:{first}", $"cancel-enter:{first}", $"callback:{first}", $"event:RunStarted:{first}", $"event:EligibilityDetermined:{first}", $"event:RunFinished:{first}", $"release:{first}", $"cancel-exit:{first}", $"dispose-enter:{first}", $"dispose-exit:{first}",
                    $"id:{second}", "cts:create:Manual", $"pending:{second}", $"arm:{second}", $"dispatch:{second}", $"event:RunStarted:{second}", $"event:EligibilityDetermined:{second}", $"event:RunFinished:{second}", $"release:{second}", $"dispose-enter:{second}", $"dispose-exit:{second}"
                }, fixture.Operations.ToArray());
            var immutable = fixture.Snapshot();
            Assert.IsFalse(fixture.Coordinator.CancelActiveRun());
            fixture.AssertSnapshot(immutable);
        }
        else
        {
            var immutable = fixture.Snapshot();
            Assert.AreEqual(ProcessingRunAdmissionResult.Stopping, await fixture.Coordinator.TriggerManualAsync());
            Assert.IsFalse(fixture.Coordinator.CancelActiveRun());
            fixture.AssertSnapshot(immutable);
        }
    }

    [TestMethod]
    [DataRow("manual")]
    [DataRow("lifetime")]
    [DataRow("stop")]
    public async Task DisposeWinsBeforePausedCancellation_LaterAndRepeatedRequestsNeverCallSource(string path)
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var lifetime = new TestLifetime();
        var observer = new CancellationRaceObserver(pauseCancellation: true);
        var overlap = new CancellationOverlapGate();
        var fixture = new DirectFixture([first, second], lifetime: lifetime, overlapGate: overlap, observer: observer);
        var plan = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var invocation = await plan.Entered.Task.WaitAsync(TestTimeout);
        var ownedToken = fixture.Cancellations.Created[0].Token;
        Assert.AreEqual(ownedToken, invocation.Token);
        using var stopBound = new CancellationTokenSource(TestTimeout);
        Task cancellationRequest;
        if (path == "manual")
        {
            cancellationRequest = Task.Run(() => Assert.IsTrue(fixture.Coordinator.CancelActiveRun()));
        }
        else if (path == "lifetime")
        {
            cancellationRequest = Task.Run(lifetime.StopApplication);
        }
        else
        {
            cancellationRequest = Task.Run(async () => await fixture.Coordinator.StopAsync(stopBound.Token));
        }

        try
        {
            await observer.CancellationPaused.Task.WaitAsync(TestTimeout);
            plan.Release.TrySetResult();
            await overlap.DisposeExited.Task.WaitAsync(TestTimeout);
            Assert.IsFalse(cancellationRequest.IsCompleted);
        }
        finally
        {
            observer.ReleaseCancellation();
            plan.Release.TrySetResult();
        }
        await cancellationRequest.WaitAsync(TestTimeout);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);

        var source = fixture.Cancellations.Created[0];
        var terminal = fixture.Events.OfType<RunFinished>().Single();
        Assert.AreSame(invocation.Request, terminal.Request);
        Assert.AreSame(invocation.Request, terminal.Result.Request);
        Assert.AreEqual(ownedToken, invocation.Token);
        CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(RunFinished) }, fixture.Events.Select(item => item.GetType()).ToArray());
        Assert.IsTrue(fixture.Events.All(item => ReferenceEquals(invocation.Request, item.Request)));
        Assert.AreEqual(ProcessingRunOutcome.Completed, terminal.Result.Outcome);
        Assert.AreEqual(0, source.CancelCount);
        Assert.AreEqual(1, source.DisposeCount);
        Assert.AreEqual(0, fixture.Logger.Entries.Count);
        CollectionAssert.AreEqual(
            new[] { $"id:{first}", "cts:create:Manual", $"pending:{first}", $"arm:{first}", $"dispatch:{first}", $"event:RunStarted:{first}", $"event:EligibilityDetermined:{first}", $"event:RunFinished:{first}", $"release:{first}", $"dispose-enter:{first}", $"dispose-exit:{first}" },
            fixture.Operations.ToArray());

        if (path == "manual")
        {
            var recovery = fixture.Executor.Enqueue();
            Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
            var recoveryInvocation = await recovery.Entered.Task.WaitAsync(TestTimeout);
            Assert.AreEqual(second, recoveryInvocation.Request.RunId);
            var recoveryDrain = fixture.Coordinator.WaitForActiveRunAsync();
            recovery.Release.TrySetResult();
            await recoveryDrain.WaitAsync(TestTimeout);
            Assert.IsFalse(fixture.Coordinator.CancelActiveRun());
            Assert.IsFalse(fixture.Coordinator.CancelActiveRun());
        }
        else if (path == "lifetime")
        {
            lifetime.StopApplication();
            lifetime.StopApplication();
        }
        else
        {
            using var repeatedBound = new CancellationTokenSource(TestTimeout);
            await fixture.Coordinator.StopAsync(repeatedBound.Token);
            await fixture.Coordinator.StopAsync(repeatedBound.Token);
        }
        Assert.AreEqual(0, source.CancelCount);
        Assert.AreEqual(1, source.DisposeCount);
        Assert.AreEqual(0, fixture.Logger.Entries.Count);
        if (path == "manual")
        {
            CollectionAssert.AreEqual(
                new[]
                {
                    $"id:{first}", "cts:create:Manual", $"pending:{first}", $"arm:{first}", $"dispatch:{first}", $"event:RunStarted:{first}", $"event:EligibilityDetermined:{first}", $"event:RunFinished:{first}", $"release:{first}", $"dispose-enter:{first}", $"dispose-exit:{first}",
                    $"id:{second}", "cts:create:Manual", $"pending:{second}", $"arm:{second}", $"dispatch:{second}", $"event:RunStarted:{second}", $"event:EligibilityDetermined:{second}", $"event:RunFinished:{second}", $"release:{second}", $"dispose-enter:{second}", $"dispose-exit:{second}"
                }, fixture.Operations.ToArray());
        }
        else
        {
            CollectionAssert.AreEqual(
                new[] { $"id:{first}", "cts:create:Manual", $"pending:{first}", $"arm:{first}", $"dispatch:{first}", $"event:RunStarted:{first}", $"event:EligibilityDetermined:{first}", $"event:RunFinished:{first}", $"release:{first}", $"dispose-enter:{first}", $"dispose-exit:{first}" },
                fixture.Operations.ToArray());
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScheduledExactOwnedToken_LinksHostCancellationAndReturnsOnlyAfterTerminalCleanup(bool cancelHost)
    {
        var id = Guid.NewGuid();
        var observer = new GatedCleanupObserver(id);
        var fixture = new DirectFixture([id], observer: observer);
        var plan = fixture.Executor.Enqueue();
        using var stopping = new CancellationTokenSource();
        var scheduled = ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(stopping.Token);
        var invocation = await plan.Entered.Task.WaitAsync(TestTimeout);
        var source = fixture.Cancellations.Created.Single();

        Assert.AreEqual(ProcessingRunTrigger.Scheduled, invocation.Request.Trigger);
        Assert.AreEqual(id, invocation.Request.RunId);
        Assert.AreSame(fixture.Reporter, invocation.Reporter);
        Assert.AreEqual(source.Token, invocation.Token);
        Assert.IsFalse(invocation.Token.IsCancellationRequested);
        if (cancelHost)
        {
            stopping.Cancel();
            await plan.CancelObserved.Task.WaitAsync(TestTimeout);
            Assert.IsTrue(invocation.Token.IsCancellationRequested);
        }

        plan.Release.TrySetResult();
        await observer.Entered.Task.WaitAsync(TestTimeout);
        Assert.IsFalse(scheduled.IsCompleted);
        Assert.AreEqual(0, source.DisposeCount);
        observer.Release.TrySetResult();

        if (cancelHost)
        {
            var observed = await Assert.ThrowsAsync<OperationCanceledException>(() => scheduled.WaitAsync(TestTimeout));
            Assert.AreEqual(stopping.Token, observed.CancellationToken);
        }
        else
        {
            Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await scheduled.WaitAsync(TestTimeout));
        }
        Assert.AreEqual(1, source.DisposeCount);
        Assert.AreEqual(0, source.CancelCount);
        Assert.AreEqual(1, fixture.Executor.CallCount);
        Assert.AreEqual(1, fixture.Events.OfType<RunFinished>().Count());
        CollectionAssert.AreEqual(
            new[]
            {
                $"id:{id}", "cts:create:Scheduled", $"pending:{id}", $"arm:{id}", $"dispatch:{id}",
                $"event:RunStarted:{id}", $"event:EligibilityDetermined:{id}", $"event:RunFinished:{id}",
                $"release:{id}", $"cts:dispose:{id}"
            },
            fixture.Operations.ToArray());
    }

    [TestMethod]
    public async Task StopAsync_BoundedRepeatedCancelDrainCleanupAndPostStopActionsAreExactlyIdempotent()
    {
        var fixture = new DirectFixture([Guid.NewGuid()]);
        var plan = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        await plan.Entered.Task.WaitAsync(TestTimeout);
        using var firstBound = new CancellationTokenSource(TestTimeout);
        using var secondBound = new CancellationTokenSource(TestTimeout);

        var firstStop = fixture.Coordinator.StopAsync(firstBound.Token);
        var secondStop = fixture.Coordinator.StopAsync(secondBound.Token);
        Assert.IsTrue(fixture.Coordinator.CancelActiveRun());
        Assert.IsTrue(fixture.Coordinator.CancelActiveRun());
        await plan.CancelObserved.Task.WaitAsync(TestTimeout);
        Assert.IsFalse(firstStop.IsCompleted);
        Assert.IsFalse(secondStop.IsCompleted);
        Assert.AreEqual(1, fixture.Cancellations.Created.Single().CancelCount);

        plan.Release.TrySetResult();
        await Task.WhenAll(firstStop, secondStop).WaitAsync(TestTimeout);
        Assert.AreEqual(1, fixture.Cancellations.Created.Single().DisposeCount);
        var immutable = fixture.Snapshot();

        Assert.AreEqual(ProcessingRunAdmissionResult.Stopping, await fixture.Coordinator.TriggerManualAsync());
        Assert.AreEqual(ScheduledTriggerResult.RejectedAlreadyRunning,
            await ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(CancellationToken.None));
        Assert.IsFalse(fixture.Coordinator.CancelActiveRun());
        using var thirdBound = new CancellationTokenSource(TestTimeout);
        await fixture.Coordinator.StopAsync(thirdBound.Token);
        fixture.AssertSnapshot(immutable);
    }

    [TestMethod]
    [DataRow("manual-first")]
    [DataRow("stop-first")]
    [DataRow("together")]
    public async Task ConcurrentShutdownAdmissionCommonGate_HasOnlyTwoCompleteLegalLinearizations(string releaseOrder)
    {
        var id = Guid.NewGuid();
        var observer = new AdmissionBarrierObserver();
        var fixture = new DirectFixture([id], observer: observer);
        var plan = fixture.Executor.Enqueue();
        using var stopBound = new CancellationTokenSource(TestTimeout);
        var manual = fixture.Coordinator.TriggerManualAsync();
        var stop = fixture.Coordinator.StopAsync(stopBound.Token);
        await Task.WhenAll(observer.ManualEntered.Task, observer.StopEntered.Task).WaitAsync(TestTimeout);

        if (releaseOrder == "manual-first")
        {
            observer.ManualRelease.TrySetResult();
            Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await manual.WaitAsync(TestTimeout));
            await plan.Entered.Task.WaitAsync(TestTimeout);
            observer.StopRelease.TrySetResult();
        }
        else if (releaseOrder == "stop-first")
        {
            observer.StopRelease.TrySetResult();
            await stop.WaitAsync(TestTimeout);
            observer.ManualRelease.TrySetResult();
        }
        else
        {
            observer.ManualRelease.TrySetResult();
            observer.StopRelease.TrySetResult();
        }

        var admission = await manual.WaitAsync(TestTimeout);
        if (admission == ProcessingRunAdmissionResult.Accepted)
        {
            await plan.Entered.Task.WaitAsync(TestTimeout);
            await plan.CancelObserved.Task.WaitAsync(TestTimeout);
            plan.Release.TrySetResult();
            await stop.WaitAsync(TestTimeout);
            Assert.AreEqual(1, fixture.IdCalls);
            Assert.AreEqual(1, fixture.Cancellations.Created.Count);
            Assert.AreEqual(1, fixture.Executor.CallCount);
            Assert.AreEqual(1, fixture.Cancellations.Created.Single().CancelCount);
            Assert.AreEqual(1, fixture.Cancellations.Created.Single().DisposeCount);
            Assert.AreEqual(1, fixture.Events.OfType<RunFinished>().Count());
            CollectionAssert.AreEqual(new[] { "stop:closing", "stop:closed" }, observer.Lifecycle.ToArray());
            var expected = new[] { $"id:{id}", "cts:create:Manual", $"pending:{id}", $"arm:{id}", $"dispatch:{id}", $"cts:cancel:{id}", $"event:RunStarted:{id}", $"event:EligibilityDetermined:{id}", $"event:RunFinished:{id}", $"release:{id}", $"cts:dispose:{id}" };
            CollectionAssert.AreEqual(expected, fixture.Operations.ToArray(), string.Join(" | ", fixture.Operations));
        }
        else
        {
            Assert.AreEqual(ProcessingRunAdmissionResult.Stopping, admission);
            await stop.WaitAsync(TestTimeout);
            Assert.AreEqual(0, fixture.IdCalls);
            Assert.AreEqual(0, fixture.Cancellations.Created.Count);
            Assert.AreEqual(0, fixture.Executor.CallCount);
            Assert.AreEqual(0, fixture.Events.Count);
            Assert.AreEqual(0, fixture.Messages().Length);
            Assert.AreEqual(0, fixture.Operations.Count);
            CollectionAssert.AreEqual(new[] { "stop:closing", "stop:closed" }, observer.Lifecycle.ToArray());
        }
    }

    private sealed class DirectFixture
    {
        private readonly Queue<Guid> _ids;
        private readonly Action<ProcessingRunRequest>? _onPending;
        private int _idCalls;
        private int _notificationCount;

        public DirectFixture(
            IEnumerable<Guid> ids,
            Action<ProcessingRunRequest>? onPending = null,
            Exception? cancelFailure = null,
            TestLifetime? lifetime = null,
            Type? projectionFailureType = null,
            Exception? projectionFailure = null,
            Exception? abandonFailure = null,
            Exception? disposeFailure = null,
            CancellationOverlapGate? overlapGate = null,
            Action<string, ProcessingRunRequest>? controlAction = null,
            IProcessingRunCoordinatorObserver? observer = null)
        {
            _ids = new Queue<Guid>(ids);
            _onPending = onPending;
            overlapGate?.AttachOperations(Operations);
            Cancellations = new RecordingCancellationFactory(Operations, cancelFailure, disposeFailure, overlapGate);
            Executor = new DirectExecutor(Operations);
            var projectionFailures = 0;
            Reporter = new ProcessingStateEventReporter(
                State,
                processingEvent =>
                {
                    Operations.Enqueue($"event:{processingEvent.GetType().Name}:{processingEvent.Request.RunId}");
                    Events.Enqueue(processingEvent);
                    if (processingEvent.GetType() == projectionFailureType
                        && Interlocked.Exchange(ref projectionFailures, 1) == 0)
                    {
                        throw projectionFailure!;
                    }
                },
                (operation, request) =>
                {
                    Operations.Enqueue($"{operation}:{request.RunId}");
                    ControlOperations.Enqueue($"{operation}:{request.RunId}");
                    controlAction?.Invoke(operation, request);
                    if (operation == "abandon" && abandonFailure is not null)
                    {
                        throw abandonFailure;
                    }
                });
            State.OnChanged += () =>
            {
                Interlocked.Increment(ref _notificationCount);
                var request = Coordinator?.ActiveRequest;
                if (State.IsRunning && request is not null && !ControlOperations.Contains($"arm:{request.RunId}"))
                {
                    Cancellations.Created[^1].SetRequest(request.RunId);
                    Operations.Enqueue($"pending:{request.RunId}");
                    _onPending?.Invoke(request);
                }
            };
            Coordinator = new ProcessingRunCoordinator(
                State,
                Reporter,
                Executor,
                Logger,
                NextId,
                Cancellations,
                observer,
                lifetime);
        }

        public ProcessingState State { get; } = new();
        public ConcurrentQueue<string> Operations { get; } = new();
        public ConcurrentQueue<string> ControlOperations { get; } = new();
        public ConcurrentQueue<ProcessingEvent> Events { get; } = new();
        public DirectExecutor Executor { get; }
        public RecordingCancellationFactory Cancellations { get; }
        public ProcessingStateEventReporter Reporter { get; }
        public CaptureLogger Logger { get; } = new();
        public ProcessingRunCoordinator Coordinator { get; }
        public int IdCalls => Volatile.Read(ref _idCalls);
        public int NotificationCount => Volatile.Read(ref _notificationCount);

        public Snapshot Snapshot() => new(
            IdCalls,
            NotificationCount,
            Cancellations.Created.Count,
            Cancellations.Created.Sum(item => item.CancelCount),
            Cancellations.Created.Sum(item => item.DisposeCount),
            Logger.Entries.Count,
            Executor.CallCount,
            Events.Count,
            ControlOperations.Count(item => item.StartsWith("abandon:", StringComparison.Ordinal)),
            Messages().Length,
            Operations.ToArray());

        public void AssertSnapshot(Snapshot expected)
        {
            Assert.AreEqual(expected.IdCalls, IdCalls);
            Assert.AreEqual(expected.Notifications, NotificationCount);
            Assert.AreEqual(expected.CtsCreates, Cancellations.Created.Count);
            Assert.AreEqual(expected.Cancels, Cancellations.Created.Sum(item => item.CancelCount));
            Assert.AreEqual(expected.Disposes, Cancellations.Created.Sum(item => item.DisposeCount));
            Assert.AreEqual(expected.Logs, Logger.Entries.Count);
            Assert.AreEqual(expected.Dispatches, Executor.CallCount);
            Assert.AreEqual(expected.Events, Events.Count);
            Assert.AreEqual(expected.Abandons, ControlOperations.Count(item => item.StartsWith("abandon:", StringComparison.Ordinal)));
            Assert.AreEqual(expected.Messages, Messages().Length);
            CollectionAssert.AreEqual(expected.Operations, Operations.ToArray());
        }

        public string[] Messages() => State.GetRecentLog().Select(line => line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..]).ToArray();

        private Guid NextId()
        {
            var id = _ids.Dequeue();
            Interlocked.Increment(ref _idCalls);
            Operations.Enqueue($"id:{id}");
            return id;
        }
    }

    private sealed record Snapshot(int IdCalls, int Notifications, int CtsCreates, int Cancels, int Disposes, int Logs, int Dispatches, int Events, int Abandons, int Messages, string[] Operations);

    private static CapturedLog[] ExpectedFailureLogs(
        string boundary,
        Guid requestId,
        Exception primary,
        Exception cleanup,
        Exception observed)
    {
        var logs = new List<CapturedLog>();
        if (boundary == "cleanup")
        {
            logs.Add(new CapturedLog(LogLevel.Error, $"Processing run {requestId} cleanup faulted", primary));
            return logs.ToArray();
        }

        var effectivePrimary = boundary == "mismatch" ? observed : primary;
        logs.Add(boundary == "oom"
            ? new CapturedLog(LogLevel.Critical, $"Processing run {requestId} exhausted memory outside a domain terminal", effectivePrimary)
            : new CapturedLog(LogLevel.Error, $"Processing run {requestId} faulted outside a domain terminal", effectivePrimary));
        if (boundary == "abandon")
        {
            logs.Add(new CapturedLog(LogLevel.Error, $"Processing run {requestId} projection abandonment faulted", cleanup));
        }
        else if (boundary is "dispose-after-primary" or "before-detach-after-primary" or "observer-after-primary")
        {
            logs.Add(new CapturedLog(LogLevel.Error, $"Processing run {requestId} cleanup faulted", cleanup));
        }
        return logs.ToArray();
    }

    private static string[] ExpectedFailureAndRetriggerOperations(string boundary, Guid first, Guid second)
    {
        var operations = new List<string>
        {
            $"id:{first}", $"cts:create:{(boundary == "sync" ? "Manual" : "Scheduled")}", $"pending:{first}", $"arm:{first}", $"dispatch:{first}"
        };
        if (boundary is "open" or "session" or "terminal-projection" or "cleanup")
        {
            operations.Add($"event:RunStarted:{first}");
        }
        if (boundary is "session" or "terminal-projection" or "cleanup")
        {
            operations.Add($"event:EligibilityDetermined:{first}");
        }
        if (boundary is "terminal-projection" or "cleanup")
        {
            operations.Add($"event:RunFinished:{first}");
        }
        if (boundary != "cleanup")
        {
            operations.Add($"abandon:{first}");
        }
        operations.Add($"release:{first}");
        operations.Add($"cts:dispose:{first}");
        operations.AddRange([
            $"id:{second}", "cts:create:Manual", $"pending:{second}", $"arm:{second}", $"dispatch:{second}",
            $"event:RunStarted:{second}", $"event:EligibilityDetermined:{second}", $"event:RunFinished:{second}",
            $"release:{second}", $"cts:dispose:{second}"]);
        return operations.ToArray();
    }

    private static ProcessingRunExecution.ProcessingOperations ZeroOperations()
    {
        return new ProcessingRunExecution.ProcessingOperations(
            _ => Task.FromResult(0L),
            () => Task.FromResult(new AppConfig()),
            () => Task.FromResult(new HashSet<Guid>()),
            (_, _, _) => throw new AssertFailedException("batch not expected"),
            (_, _, _, _, _) => throw new AssertFailedException("resolver not expected"),
            (_, _, _, _) => throw new AssertFailedException("infrastructure not expected"),
            _ => throw new AssertFailedException("skip write not expected"),
            (_, _, _) => throw new AssertFailedException("location write not expected"));
    }

    private sealed class ThrowOnExactNotification(int throwOn, Exception failure)
    {
        private int _calls;
        public void Invoke()
        {
            if (Interlocked.Increment(ref _calls) == throwOn)
            {
                throw failure;
            }
        }
    }

    private sealed class RecordingDelegatingExecutor(IProcessingRunExecutor inner, ConcurrentQueue<string> operations) : IProcessingRunExecutor
    {
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);
        public Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            operations.Enqueue($"dispatch:{request.RunId}");
            return inner.ExecuteAsync(request, reporter, cancellationToken);
        }
    }

    private sealed class DirectExecutor(ConcurrentQueue<string> operations) : IProcessingRunExecutor
    {
        private readonly Queue<DirectPlan> _plans = new();
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);

        public DirectPlan Enqueue(Exception? synchronousFailure = null, Exception? asyncFailure = null, bool mismatchedResult = false, bool completeOnCancellation = false, ProcessingRunOutcome? forcedOutcome = null)
        {
            var plan = new DirectPlan(synchronousFailure, asyncFailure, mismatchedResult, completeOnCancellation, forcedOutcome);
            _plans.Enqueue(plan);
            return plan;
        }

        public Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var plan = _plans.Dequeue();
            operations.Enqueue($"dispatch:{request.RunId}");
            if (plan.SynchronousFailure is not null)
            {
                throw plan.SynchronousFailure;
            }
            return ExecuteAsync(plan, request, reporter, cancellationToken);
        }

        private static async Task<ProcessingRunResult> ExecuteAsync(DirectPlan plan, ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken token)
        {
            using var registration = token.Register(() => plan.CancelObserved.TrySetResult());
            plan.Entered.TrySetResult(new Invocation(request, reporter, token));
            if (plan.CompleteOnCancellation)
            {
                await Task.WhenAny(plan.Release.Task, plan.CancelObserved.Task).WaitAsync(TestTimeout).ConfigureAwait(false);
            }
            else
            {
                await plan.Release.Task.WaitAsync(TestTimeout).ConfigureAwait(false);
            }
            if (plan.AsyncFailure is not null)
            {
                throw plan.AsyncFailure;
            }
            if (plan.MismatchedResult)
            {
                var other = new ProcessingRunRequest(Guid.NewGuid(), request.Trigger);
                plan.ReturnedRequest = other;
                return Result(other, ProcessingRunOutcome.Completed);
            }
            var session = await reporter.OpenRunAsync(request, Now, CancellationToken.None).ConfigureAwait(false);
            await session.DetermineEligibilityAsync(0, CancellationToken.None).ConfigureAwait(false);
            var outcome = plan.ForcedOutcome ?? (token.IsCancellationRequested ? ProcessingRunOutcome.Cancelled : ProcessingRunOutcome.Completed);
            var result = Result(request, outcome);
            await session.FinishAsync(result).ConfigureAwait(false);
            return result;
        }
    }

    private sealed class DirectPlan(Exception? synchronousFailure, Exception? asyncFailure, bool mismatchedResult, bool completeOnCancellation, ProcessingRunOutcome? forcedOutcome)
    {
        public Exception? SynchronousFailure { get; } = synchronousFailure;
        public Exception? AsyncFailure { get; } = asyncFailure;
        public bool MismatchedResult { get; } = mismatchedResult;
        public bool CompleteOnCancellation { get; } = completeOnCancellation;
        public ProcessingRunOutcome? ForcedOutcome { get; } = forcedOutcome;
        public ProcessingRunRequest? ReturnedRequest { get; set; }
        public TaskCompletionSource<Invocation> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record Invocation(ProcessingRunRequest Request, IProcessingEventReporter Reporter, CancellationToken Token);

    private sealed class RecordingCancellationFactory(ConcurrentQueue<string> operations, Exception? cancelFailure, Exception? disposeFailure, CancellationOverlapGate? overlapGate = null) : IProcessingRunCancellationFactory
    {
        public List<RecordingCancellation> Created { get; } = [];
        public IProcessingRunCancellation Create(ProcessingRunRequest request, CancellationToken linkedToken)
        {
            operations.Enqueue($"cts:create:{request.Trigger}");
            var created = new RecordingCancellation(operations, request, linkedToken, cancelFailure, disposeFailure, overlapGate);
            Created.Add(created);
            return created;
        }
    }

    private sealed class RecordingCancellation : IProcessingRunCancellation
    {
        private readonly ConcurrentQueue<string> _operations;
        private readonly CancellationTokenSource _source;
        private readonly Exception? _cancelFailure;
        private readonly Exception? _disposeFailure;
        private readonly CancellationOverlapGate? _overlapGate;
        private Guid _requestId;
        public RecordingCancellation(ConcurrentQueue<string> operations, ProcessingRunRequest request, CancellationToken linkedToken, Exception? cancelFailure, Exception? disposeFailure, CancellationOverlapGate? overlapGate)
        {
            _operations = operations;
            _requestId = request.RunId;
            _source = request.Trigger == ProcessingRunTrigger.Scheduled ? CancellationTokenSource.CreateLinkedTokenSource(linkedToken) : new CancellationTokenSource();
            _cancelFailure = cancelFailure;
            _disposeFailure = disposeFailure;
            _overlapGate = overlapGate;
        }
        public CancellationToken Token => _source.Token;
        public int CancelCount { get; private set; }
        public int DisposeCount { get; private set; }
        public void SetRequest(Guid requestId) => _requestId = requestId;
        public void Cancel()
        {
            CancelCount++;
            if (_overlapGate is not null)
            {
                _overlapGate.Cancel(_requestId, _source);
            }
            else
            {
                _operations.Enqueue($"cts:cancel:{_requestId}");
                _source.Cancel();
            }
            if (_cancelFailure is not null)
            {
                throw _cancelFailure;
            }
        }
        public void Dispose()
        {
            DisposeCount++;
            if (_overlapGate is not null)
            {
                _overlapGate.Dispose(_requestId, _source);
            }
            else
            {
                _operations.Enqueue($"cts:dispose:{_requestId}");
                _source.Dispose();
            }
            if (_disposeFailure is not null)
            {
                throw _disposeFailure;
            }
        }
    }

    private sealed class CancellationOverlapGate
    {
        private readonly object _releaseGate = new();
        private ConcurrentQueue<string>? _operations;
        private bool _cancelReleased;
        public TaskCompletionSource CancelEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CallbackCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void AttachOperations(ConcurrentQueue<string> operations) => _operations = operations;
        private void Record(string operation) => (_operations ?? throw new InvalidOperationException("Overlap operations are not attached.")).Enqueue(operation);

        public void Cancel(Guid requestId, CancellationTokenSource source)
        {
            Record($"cancel-enter:{requestId}");
            CancelEntered.TrySetResult();
            source.Cancel();
            Record($"callback:{requestId}");
            CallbackCompleted.TrySetResult();
            lock (_releaseGate)
            {
                if (!_cancelReleased && !Monitor.Wait(_releaseGate, TestTimeout))
                {
                    throw new TimeoutException("Timed out waiting to release the owned cancellation source.");
                }
            }
            Record($"cancel-exit:{requestId}");
        }

        public void ReleaseCancel()
        {
            lock (_releaseGate)
            {
                _cancelReleased = true;
                Monitor.PulseAll(_releaseGate);
            }
        }

        public void Dispose(Guid requestId, CancellationTokenSource source)
        {
            Record($"dispose-enter:{requestId}");
            DisposeEntered.TrySetResult();
            source.Dispose();
            Record($"dispose-exit:{requestId}");
            DisposeExited.TrySetResult();
        }
    }

    private sealed class CancellationRaceObserver(bool pauseCancellation = false) : IProcessingRunCoordinatorObserver
    {
        private readonly object _releaseGate = new();
        private bool _cancellationReleased;
        public TaskCompletionSource CancellationPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim RequestReturned { get; } = new(false);
        public TaskCompletionSource DisposeAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void AfterRequestCancellation(ProcessingRunRequest request) => RequestReturned.Set();

        public void BeforeRequestCancellation(ProcessingRunRequest request)
        {
            if (!pauseCancellation)
            {
                return;
            }
            CancellationPaused.TrySetResult();
            lock (_releaseGate)
            {
                if (!_cancellationReleased && !Monitor.Wait(_releaseGate, TestTimeout))
                {
                    throw new TimeoutException("Timed out waiting to release the cancellation request seam.");
                }
            }
        }

        public void ReleaseCancellation()
        {
            lock (_releaseGate)
            {
                _cancellationReleased = true;
                Monitor.PulseAll(_releaseGate);
            }
        }

        public ValueTask BeforeDisposeAsync(ProcessingRunRequest request, CancellationToken activeToken)
        {
            DisposeAttempted.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PreDetachBarrierObserver(Guid requestId) : IProcessingRunCoordinatorObserver
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask BeforeDetachAsync(ProcessingRunRequest request, CancellationToken activeToken)
        {
            if (request.RunId == requestId)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TestTimeout).ConfigureAwait(false);
            }
        }
        public ValueTask BeforeDisposeAsync(ProcessingRunRequest request, CancellationToken activeToken) => ValueTask.CompletedTask;
    }

    private sealed class ThrowingCleanupObserver(Exception failure, bool beforeDetach) : IProcessingRunCoordinatorObserver
    {
        public ValueTask BeforeDetachAsync(ProcessingRunRequest request, CancellationToken activeToken)
        {
            if (beforeDetach)
            {
                throw failure;
            }
            return ValueTask.CompletedTask;
        }
        public ValueTask BeforeDisposeAsync(ProcessingRunRequest request, CancellationToken activeToken)
        {
            if (!beforeDetach)
            {
                throw failure;
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AdmissionBarrierObserver : IProcessingRunCoordinatorObserver
    {
        public TaskCompletionSource ManualEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ManualRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Lifecycle { get; } = new();

        public void CoordinatorStopping() => Lifecycle.Enqueue("stop:closing");
        public void CoordinatorStopped() => Lifecycle.Enqueue("stop:closed");

        public async ValueTask BeforeAdmissionGateAsync(ProcessingRunAdmissionAttempt attempt)
        {
            if (attempt == ProcessingRunAdmissionAttempt.Manual)
            {
                ManualEntered.TrySetResult();
                await ManualRelease.Task.WaitAsync(TestTimeout).ConfigureAwait(false);
            }
            else if (attempt == ProcessingRunAdmissionAttempt.Stop)
            {
                StopEntered.TrySetResult();
                await StopRelease.Task.WaitAsync(TestTimeout).ConfigureAwait(false);
            }
        }

        public ValueTask BeforeDisposeAsync(ProcessingRunRequest request, CancellationToken activeToken) => ValueTask.CompletedTask;
    }

    private sealed class GatedCleanupObserver(Guid gatedRequest) : IProcessingRunCoordinatorObserver
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask BeforeDisposeAsync(ProcessingRunRequest request, CancellationToken activeToken)
        {
            if (request.RunId != gatedRequest)
            {
                return;
            }
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TestTimeout).ConfigureAwait(false);
            Completed.TrySetResult();
        }
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public void StopApplication() => _stopping.Cancel();
    }

    private sealed record CapturedLog(LogLevel Level, string Message, Exception? Exception);

    private sealed class CaptureLogger : ILogger<ProcessingRunCoordinator>
    {
        public ConcurrentQueue<CapturedLog> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue(new CapturedLog(logLevel, formatter(state, exception), exception));
        }
    }

    private static ProcessingRunResult Result(ProcessingRunRequest request, ProcessingRunOutcome outcome) => new(request, Now, Now, 0, 0, 0, 0, outcome, outcome == ProcessingRunOutcome.Failed ? "forced domain failure" : null);
}
