using System.Collections.Concurrent;
using System.Reflection;
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
public sealed class ProcessingRunCoordinatorChange13Tests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 0, 10, 47, TimeSpan.Zero);

    [TestMethod]
    public async Task ManualDuringScheduled_ReturnsAlreadyRunningWithoutIdentityArmDispatchOrLog()
    {
        var fixture = new Fixture(Guid.Parse("13000000-0000-0000-0000-000000000001"));
        var plan = fixture.Executor.Enqueue();
        var scheduled = ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(CancellationToken.None);
        var invocation = await plan.Entered.Task.WaitAsync(TestTimeout);
        var beforeLog = fixture.Messages();
        var beforeEvents = fixture.Events.ToArray();

        var rejected = await fixture.Coordinator.TriggerManualAsync().WaitAsync(TestTimeout);

        Assert.AreEqual(ProcessingRunAdmissionResult.AlreadyRunning, rejected);
        Assert.AreEqual(1, fixture.IdCalls);
        Assert.AreEqual(1, fixture.Executor.CallCount);
        Assert.AreSame(invocation.Request, fixture.Coordinator.ActiveRequest);
        CollectionAssert.AreEqual(beforeLog, fixture.Messages());
        CollectionAssert.AreEqual(beforeEvents, fixture.Events.ToArray());
        plan.Release.TrySetResult();
        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await scheduled.WaitAsync(TestTimeout));
    }

    [TestMethod]
    public async Task ScheduledDuringManual_ReturnsRejectedAlreadyRunningWithExactSingleMessageAndNoRunWork()
    {
        var fixture = new Fixture(Guid.Parse("13000000-0000-0000-0000-000000000002"));
        var plan = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var invocation = await plan.Entered.Task.WaitAsync(TestTimeout);

        var rejected = await ((IScheduledRunTrigger)fixture.Coordinator)
            .TriggerScheduledAsync(CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.AreEqual(ScheduledTriggerResult.RejectedAlreadyRunning, rejected);
        Assert.AreEqual(1, fixture.IdCalls);
        Assert.AreEqual(1, fixture.Executor.CallCount);
        Assert.AreSame(invocation.Request, fixture.Coordinator.ActiveRequest);
        CollectionAssert.AreEqual(
            new[] { "Scheduled run skipped because a processing pass is already in progress." },
            fixture.Messages());
        Assert.AreEqual(0, fixture.Events.Count);
        plan.Release.TrySetResult();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task PendingObservation_SeesPublishedActiveTokenAndImmediateCancelTargetsIt()
    {
        var expectedId = Guid.Parse("13000000-0000-0000-0000-000000000003");
        var fixture = new Fixture(expectedId);
        var plan = fixture.Executor.Enqueue();
        ProcessingRunRequest? pendingRequest = null;
        var cancelResult = false;
        fixture.State.OnChanged += ObservePending;

        var admission = await fixture.Coordinator.TriggerManualAsync().WaitAsync(TestTimeout);
        var invocation = await plan.Entered.Task.WaitAsync(TestTimeout);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, admission);
        Assert.IsTrue(cancelResult);
        Assert.AreSame(invocation.Request, pendingRequest);
        Assert.AreEqual(expectedId, invocation.Request.RunId);
        Assert.AreEqual(ProcessingRunTrigger.Manual, invocation.Request.Trigger);
        Assert.IsTrue(invocation.Token.CanBeCanceled);
        Assert.IsTrue(invocation.Token.IsCancellationRequested);
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, (await plan.TerminalResult.Task.WaitAsync(TestTimeout)).Outcome);
        CollectionAssert.AreEqual(
            new[] { typeof(RunStarted), typeof(RunFinished) },
            fixture.Events.Select(item => item.GetType()).ToArray());

        void ObservePending()
        {
            if (fixture.State.IsRunning && pendingRequest is null)
            {
                pendingRequest = fixture.Coordinator.ActiveRequest;
                cancelResult = fixture.Coordinator.CancelActiveRun();
            }
        }
    }

    [TestMethod]
    public async Task AcceptedScheduled_WaitsForTerminalAndExactCleanupBeforeAcceptedAfterTerminal()
    {
        var fixture = new Fixture(Guid.Parse("13000000-0000-0000-0000-000000000004"));
        var plan = fixture.Executor.Enqueue(gateAfterTerminal: true);
        var accepted = ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(CancellationToken.None);
        var invocation = await plan.Entered.Task.WaitAsync(TestTimeout);
        Assert.AreEqual(ProcessingRunTrigger.Scheduled, invocation.Request.Trigger);
        Assert.IsFalse(accepted.IsCompleted);

        plan.Release.TrySetResult();
        await plan.TerminalProjected.Task.WaitAsync(TestTimeout);
        Assert.IsFalse(fixture.State.IsRunning);
        Assert.IsFalse(accepted.IsCompleted, "Projection idle must not release local admission before executor return and cleanup.");
        var rejected = await fixture.Coordinator.TriggerManualAsync().WaitAsync(TestTimeout);
        Assert.AreEqual(ProcessingRunAdmissionResult.AlreadyRunning, rejected);
        Assert.AreEqual(1, fixture.IdCalls);
        Assert.AreEqual(1, fixture.Executor.CallCount);

        plan.AfterTerminalRelease.TrySetResult();
        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await accepted.WaitAsync(TestTimeout));
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
    }

    [TestMethod]
    public async Task AcceptedScheduled_UsesOneExactScheduledIdentityTokenReporterAndDispatch()
    {
        var expectedId = Guid.Parse("13000000-0000-0000-0000-000000000005");
        var fixture = new Fixture(expectedId);
        var plan = fixture.Executor.Enqueue();
        using var stopping = new CancellationTokenSource();
        var accepted = ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(stopping.Token);
        var invocation = await plan.Entered.Task.WaitAsync(TestTimeout);

        Assert.AreEqual(expectedId, invocation.Request.RunId);
        Assert.AreEqual(ProcessingRunTrigger.Scheduled, invocation.Request.Trigger);
        Assert.AreSame(fixture.Reporter, invocation.Reporter);
        Assert.IsTrue(invocation.Token.CanBeCanceled);
        Assert.AreEqual(1, fixture.IdCalls);
        Assert.AreEqual(1, fixture.Executor.CallCount);
        Assert.AreSame(invocation.Request, fixture.Coordinator.ActiveRequest);
        plan.Release.TrySetResult();
        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await accepted.WaitAsync(TestTimeout));
        Assert.AreSame(invocation.Request, (await plan.TerminalResult.Task.WaitAsync(TestTimeout)).Request);
        Assert.AreEqual(1, fixture.Events.OfType<RunFinished>().Count());
    }

    [TestMethod]
    public async Task CancelActiveRun_ScheduledTokenObservedOnceAndLaterRunUnaffected()
    {
        var ids = new[]
        {
            Guid.Parse("13000000-0000-0000-0000-000000000006"),
            Guid.Parse("13000000-0000-0000-0000-000000000007")
        };
        var fixture = new Fixture(ids);
        var scheduledPlan = fixture.Executor.Enqueue();
        var scheduled = ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(CancellationToken.None);
        var scheduledInvocation = await scheduledPlan.Entered.Task.WaitAsync(TestTimeout);

        Assert.IsTrue(fixture.Coordinator.CancelActiveRun());
        Assert.IsTrue(scheduledInvocation.Token.IsCancellationRequested);
        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await scheduled.WaitAsync(TestTimeout));
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, (await scheduledPlan.TerminalResult.Task.WaitAsync(TestTimeout)).Outcome);

        var manualPlan = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var manualInvocation = await manualPlan.Entered.Task.WaitAsync(TestTimeout);
        Assert.AreEqual(ids[1], manualInvocation.Request.RunId);
        Assert.IsFalse(manualInvocation.Token.IsCancellationRequested);
        manualPlan.Release.TrySetResult();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        Assert.AreEqual(ProcessingRunOutcome.Completed, (await manualPlan.TerminalResult.Task.WaitAsync(TestTimeout)).Outcome);
    }

    [TestMethod]
    public async Task CancelActiveRun_WhileIdleIsFalseCreatesNothingAndNextManualIsAccepted()
    {
        var expectedId = Guid.Parse("13000000-0000-0000-0000-000000000008");
        var fixture = new Fixture(expectedId);
        Assert.IsFalse(fixture.Coordinator.CancelActiveRun());
        Assert.AreEqual(0, fixture.IdCalls);
        Assert.AreEqual(0, fixture.Executor.CallCount);
        Assert.AreEqual(0, fixture.Events.Count);

        var plan = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var invocation = await plan.Entered.Task.WaitAsync(TestTimeout);
        Assert.AreEqual(expectedId, invocation.Request.RunId);
        plan.Release.TrySetResult();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task EveryTerminalAndInfrastructureBoundary_CleanupAllowsNewIdentity()
    {
        foreach (var outcome in new[] { ProcessingRunOutcome.Completed, ProcessingRunOutcome.Cancelled, ProcessingRunOutcome.Failed })
        {
            var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var fixture = new Fixture(ids);
            var first = fixture.Executor.Enqueue(outcome: outcome);
            Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
            await first.Entered.Task.WaitAsync(TestTimeout);
            first.Release.TrySetResult();
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);

            var second = fixture.Executor.Enqueue();
            Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
            var secondInvocation = await second.Entered.Task.WaitAsync(TestTimeout);
            Assert.AreEqual(ids[1], secondInvocation.Request.RunId);
            Assert.AreNotEqual(ids[0], ids[1]);
            second.Release.TrySetResult();
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
            Assert.AreEqual(2, fixture.Executor.CallCount);
            Assert.AreEqual(2, fixture.Events.OfType<RunFinished>().Count());
        }
    }

    [TestMethod]
    public async Task SetupDispatchFault_PropagatesExactReferenceCleansAndPermitsRetrigger()
    {
        var failure = new InvalidOperationException("synchronous dispatch failed");
        var fixture = new Fixture(new[] { Guid.NewGuid(), Guid.NewGuid() });
        fixture.Executor.Enqueue(synchronousFailure: failure);

        var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Coordinator.TriggerManualAsync());
        Assert.AreSame(failure, observed);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        Assert.IsFalse(fixture.State.IsRunning);
        Assert.AreEqual(0, fixture.Events.Count);

        var recovery = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        await recovery.Entered.Task.WaitAsync(TestTimeout);
        recovery.Release.TrySetResult();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        Assert.AreEqual(2, fixture.Executor.CallCount);
    }

    [TestMethod]
    public async Task AsyncForeignCancellationAndOutOfMemory_PropagateAfterCleanupWithoutSyntheticTerminal()
    {
        foreach (var failure in new Exception[]
        {
            new OperationCanceledException("foreign cancellation"),
            new OutOfMemoryException("controlled coordinator oom")
        })
        {
            var fixture = new Fixture(Guid.NewGuid());
            var plan = fixture.Executor.Enqueue(asyncFailure: failure);
            var scheduled = ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(CancellationToken.None);
            await plan.Entered.Task.WaitAsync(TestTimeout);
            plan.Release.TrySetResult();
            var observed = await Assert.ThrowsAsync<Exception>(() => scheduled.WaitAsync(TestTimeout));
            Assert.AreSame(failure, observed);
            Assert.IsNull(fixture.Coordinator.ActiveRequest);
            Assert.IsFalse(fixture.State.IsRunning);
            Assert.AreEqual(0, fixture.Events.OfType<RunFinished>().Count());
            Assert.AreEqual(1, fixture.Logger.Entries.Count);
        }
    }

    [TestMethod]
    public async Task StopAsync_ClosesAdmissionCancelsAndDrainsExactRunIdempotentlyWithinToken()
    {
        var fixture = new Fixture(Guid.NewGuid());
        var plan = fixture.Executor.Enqueue();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Coordinator.TriggerManualAsync());
        var invocation = await plan.Entered.Task.WaitAsync(TestTimeout);

        var firstStop = fixture.Coordinator.StopAsync(CancellationToken.None);
        var secondStop = fixture.Coordinator.StopAsync(CancellationToken.None);
        Assert.IsTrue(invocation.Token.IsCancellationRequested);
        Assert.AreEqual(ProcessingRunAdmissionResult.Stopping, await fixture.Coordinator.TriggerManualAsync());
        Assert.AreEqual(ScheduledTriggerResult.RejectedAlreadyRunning,
            await ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(CancellationToken.None));
        Assert.AreEqual(1, fixture.IdCalls);
        Assert.AreEqual(1, fixture.Executor.CallCount);
        await Task.WhenAll(firstStop, secondStop).WaitAsync(TestTimeout);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, (await plan.TerminalResult.Task.WaitAsync(TestTimeout)).Outcome);
        var immutableEvents = fixture.Events.ToArray();
        var immutableMessages = fixture.Messages();
        CollectionAssert.AreEqual(immutableEvents, fixture.Events.ToArray());
        CollectionAssert.AreEqual(immutableMessages, fixture.Messages());
    }

    [TestMethod]
    public void ManualContract_ExposesOnlyAdmissionAndCancelWithoutRunOnceOrMutableInternals()
    {
        var methods = typeof(IManualProcessingRunCoordinator).GetMethods();
        CollectionAssert.AreEquivalent(
            new[] { nameof(IManualProcessingRunCoordinator.TriggerManualAsync), nameof(IManualProcessingRunCoordinator.CancelActiveRun) },
            methods.Select(method => method.Name).ToArray());
        Assert.AreEqual(typeof(Task<ProcessingRunAdmissionResult>), methods.Single(method => method.Name == nameof(IManualProcessingRunCoordinator.TriggerManualAsync)).ReturnType);
        Assert.AreEqual(typeof(bool), methods.Single(method => method.Name == nameof(IManualProcessingRunCoordinator.CancelActiveRun)).ReturnType);
        Assert.IsFalse(methods.Any(method => method.Name.Contains("RunOnce", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(
            new[] { nameof(ProcessingRunAdmissionResult.Accepted), nameof(ProcessingRunAdmissionResult.AlreadyRunning), nameof(ProcessingRunAdmissionResult.Stopping) },
            Enum.GetNames<ProcessingRunAdmissionResult>());
        CollectionAssert.AreEqual(
            new[] { nameof(ScheduledTriggerResult.RejectedAlreadyRunning), nameof(ScheduledTriggerResult.AcceptedAfterTerminal) },
            Enum.GetNames<ScheduledTriggerResult>());
    }

    [TestMethod]
    public void AddProcessingServices_CoordinatorAliasesAreReferenceIdenticalAndSchedulerIsSeparateHostedSingleton()
    {
        var services = BuildServices();
        using var provider = services.BuildServiceProvider();
        var coordinator = provider.GetRequiredService<ProcessingRunCoordinator>();
        var scheduler = provider.GetRequiredService<ProcessingBackgroundService>();
        Assert.AreSame(coordinator, provider.GetRequiredService<IManualProcessingRunCoordinator>());
        Assert.AreSame(coordinator, provider.GetRequiredService<IScheduledRunTrigger>());
        var hosted = provider.GetServices<IHostedService>().ToArray();
        Assert.AreEqual(2, hosted.Length);
        Assert.IsTrue(hosted.Any(item => ReferenceEquals(item, coordinator)));
        Assert.IsTrue(hosted.Any(item => ReferenceEquals(item, scheduler)));
        Assert.IsFalse(ReferenceEquals(coordinator, scheduler));
        Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == typeof(ProcessingRunCoordinator)));
        Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == typeof(ProcessingBackgroundService)));
        Assert.IsTrue(typeof(ProcessingBackgroundService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Any(field => field.FieldType == typeof(IScheduledRunTrigger)));
        Assert.IsFalse(typeof(ProcessingBackgroundService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Any(field =>
            field.FieldType == typeof(IProcessingRunExecutor)
            || field.FieldType == typeof(ProcessingStateEventReporter)
            || field.FieldType == typeof(CancellationTokenSource)
            || field.FieldType == typeof(SemaphoreSlim)));
        Assert.IsFalse(typeof(ProcessingRunCoordinator).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => parameter.ParameterType == typeof(ProcessingBackgroundService)));
    }

    private static ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<ProcessingBackgroundService>>(NullLogger<ProcessingBackgroundService>.Instance);
        services.AddSingleton((ConfigService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ConfigService)));
        services.AddSingleton((AdministrativeAreaResolverService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AdministrativeAreaResolverService)));
        services.AddSingleton((ImmichDbRepository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ImmichDbRepository)));
        services.AddSingleton((ImmichReverseGeo.Overture.Services.OverturePlacesService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ImmichReverseGeo.Overture.Services.OverturePlacesService)));
        services.AddSingleton((SkippedAssetsRepository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SkippedAssetsRepository)));
        services.AddProcessingServices();
        return services;
    }

    private sealed class Fixture
    {
        private readonly Queue<Guid> _ids;
        private int _idCalls;

        public Fixture(Guid id) : this(new[] { id })
        {
        }

        public Fixture(IEnumerable<Guid> ids)
        {
            _ids = new Queue<Guid>(ids);
            Reporter = new ProcessingStateEventReporter(State, Events.Enqueue);
            Coordinator = new ProcessingRunCoordinator(
                State,
                Reporter,
                Executor,
                Logger,
                NextId);
        }

        public ProcessingState State { get; } = new();
        public ConcurrentQueue<ProcessingEvent> Events { get; } = new();
        public ProcessingStateEventReporter Reporter { get; }
        public PlanExecutor Executor { get; } = new();
        public CaptureLogger Logger { get; } = new();
        public ProcessingRunCoordinator Coordinator { get; }
        public int IdCalls => Volatile.Read(ref _idCalls);

        public string[] Messages()
        {
            return State.GetRecentLog()
                .Select(line => line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..])
                .ToArray();
        }

        private Guid NextId()
        {
            Interlocked.Increment(ref _idCalls);
            return _ids.Dequeue();
        }
    }

    private sealed class PlanExecutor : IProcessingRunExecutor
    {
        private readonly Queue<Plan> _plans = new();
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);

        public Plan Enqueue(
            ProcessingRunOutcome outcome = ProcessingRunOutcome.Completed,
            Exception? synchronousFailure = null,
            Exception? asyncFailure = null,
            bool gateAfterTerminal = false)
        {
            var plan = new Plan(outcome, synchronousFailure, asyncFailure, gateAfterTerminal);
            _plans.Enqueue(plan);
            return plan;
        }

        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var plan = _plans.Dequeue();
            if (plan.SynchronousFailure is not null)
            {
                throw plan.SynchronousFailure;
            }

            return ExecutePlanAsync(plan, request, reporter, cancellationToken);
        }

        private static async Task<ProcessingRunResult> ExecutePlanAsync(
            Plan plan,
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            plan.Entered.TrySetResult(new Invocation(request, reporter, cancellationToken));
            try
            {
                await plan.Release.Task.WaitAsync(TestTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }

            if (plan.AsyncFailure is not null)
            {
                throw plan.AsyncFailure;
            }

            var session = await reporter.OpenRunAsync(request, Now, CancellationToken.None).ConfigureAwait(false);
            var outcome = cancellationToken.IsCancellationRequested
                ? ProcessingRunOutcome.Cancelled
                : plan.Outcome;
            if (outcome == ProcessingRunOutcome.Completed)
            {
                await session.DetermineEligibilityAsync(0, CancellationToken.None).ConfigureAwait(false);
            }

            var result = new ProcessingRunResult(
                request,
                Now,
                Now,
                0,
                0,
                0,
                0,
                outcome,
                outcome == ProcessingRunOutcome.Failed ? "planned domain failure" : null);
            await session.FinishAsync(result).ConfigureAwait(false);
            plan.TerminalResult.TrySetResult(result);
            plan.TerminalProjected.TrySetResult();
            if (plan.GateAfterTerminal)
            {
                await plan.AfterTerminalRelease.Task.WaitAsync(TestTimeout).ConfigureAwait(false);
            }

            return result;
        }
    }

    private sealed class Plan(
        ProcessingRunOutcome outcome,
        Exception? synchronousFailure,
        Exception? asyncFailure,
        bool gateAfterTerminal)
    {
        public ProcessingRunOutcome Outcome { get; } = outcome;
        public Exception? SynchronousFailure { get; } = synchronousFailure;
        public Exception? AsyncFailure { get; } = asyncFailure;
        public bool GateAfterTerminal { get; } = gateAfterTerminal;
        public TaskCompletionSource<Invocation> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TerminalProjected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AfterTerminalRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ProcessingRunResult> TerminalResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record Invocation(
        ProcessingRunRequest Request,
        IProcessingEventReporter Reporter,
        CancellationToken Token);

    private sealed class CaptureLogger : ILogger<ProcessingRunCoordinator>
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Exception)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue((logLevel, exception));
        }
    }
}
