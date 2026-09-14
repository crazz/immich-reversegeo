using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.ChildWorkerExecution;

[TestClass]
[TestCategory("Change38")]
public sealed class ChildOnlyProcessingBackendTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Coordinator_ChildBackendReceivesExactDispatchArguments()
    {
        await using var provider = CreateDispatchProvider(out var recorder, out var cancellationFactory);
        var expectedRunId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var coordinator = CreateCoordinator(provider, () => expectedRunId, cancellationFactory);

        var admission = await coordinator.TriggerManualAsync();
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, admission, "admission");
        Assert.AreEqual(1, recorder.Invocations.Count, "dispatch-count");
        var invocation = recorder.Invocations.Single();
        Assert.AreEqual(1, recorder.CreatedCount, "child-adapter-created");
        Assert.AreEqual(ProcessingRunTrigger.Manual, invocation.Request.Trigger, "request-trigger");
        Assert.AreEqual(expectedRunId, invocation.Request.RunId, "request-id");
        Assert.AreSame(recorder.Result!.Request, invocation.Request, "result-request-identity");
        Assert.AreSame(recorder.ExpectedReporter, invocation.Reporter, "reporter-identity");
        Assert.AreEqual(cancellationFactory.CreatedToken, invocation.Token, "coordinator-token");
        Assert.AreEqual(ProcessingRunOutcome.Completed, recorder.Result.Outcome, "success-result");
        Assert.IsFalse(recorder.State.IsRunning, "returns-idle");
    }

    [TestMethod]
    public async Task Coordinator_ActiveRunRejectsManualAndScheduledTriggersWithoutCreatingAnotherChildScope()
    {
        await using var provider = CreateDispatchProvider(out var recorder, out var cancellationFactory, gateExecution: true);
        var ids = new Queue<Guid>(new[]
        {
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            Guid.Parse("20000000-0000-0000-0000-000000000002")
        });
        var coordinator = CreateCoordinator(provider, ids.Dequeue, cancellationFactory);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await coordinator.TriggerManualAsync(), "first-admission");
        await recorder.Entered.Task.WaitAsync(TestTimeout);

        Assert.AreEqual(ProcessingRunAdmissionResult.AlreadyRunning, await coordinator.TriggerManualAsync(), "duplicate-manual");
        Assert.AreEqual(
            ScheduledTriggerResult.RejectedAlreadyRunning,
            await ((IScheduledRunTrigger)coordinator).TriggerScheduledAsync(CancellationToken.None),
            "duplicate-scheduled");
        Assert.AreEqual(1, cancellationFactory.CreateCount, "duplicate-does-not-create-cts");
        Assert.AreEqual(1, recorder.CreatedCount, "duplicate-does-not-create-second-scope-adapter");
        Assert.AreEqual(1, recorder.Invocations.Count, "duplicate-does-not-resolve-or-dispatch");
        Assert.AreEqual(1, ids.Count, "duplicate-does-not-create-run-id");

        recorder.Release.TrySetResult();
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task ControlPlaneComposition_DispatchesManualAndEligibleScheduledRequestsToOneChildContract()
    {
        var services = CreateControlPlaneRegistrationServices();
        var scheduledGate = new PositiveScheduledWorkGate();
        services.AddProcessingControlPlaneServices();
        services.RemoveAll<IChildProcessingRunBackend>();
        services.AddScoped<IChildProcessingRunBackend>(sp => new RecordingChildBackend(
            sp.GetRequiredService<ChildDispatchRecorder>(),
            sp.GetRequiredService<PositiveScheduledWorkGate>()));
        services.RemoveAll<IProcessingWorkDetector>();
        services.AddSingleton<IProcessingWorkDetector>(scheduledGate);
        services.AddSingleton(scheduledGate);
        services.AddSingleton(sp => new ChildDispatchRecorder(
            sp.GetRequiredService<ProcessingState>(),
            sp.GetRequiredService<ProcessingStateEventReporter>(),
            gateExecution: false));

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var coordinator = provider.GetRequiredService<ProcessingRunCoordinator>();
        var recorder = provider.GetRequiredService<ChildDispatchRecorder>();
        var reporter = provider.GetRequiredService<ProcessingStateEventReporter>();
        var state = provider.GetRequiredService<ProcessingState>();

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await coordinator.TriggerManualAsync(), "manual-admission");
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        Assert.AreEqual(
            ScheduledTriggerResult.AcceptedAfterTerminal,
            await ((IScheduledRunTrigger)coordinator).TriggerScheduledAsync(CancellationToken.None),
            "scheduled-admission");
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);

        var invocations = recorder.Invocations.ToArray();
        Assert.AreEqual(2, invocations.Length, "one-backend-per-admitted-trigger");
        Assert.AreEqual(1, scheduledGate.Calls, "eligible-scheduled-detector-once");
        CollectionAssert.AreEqual(new[] { 0, 1 }, scheduledGate.CallsWhenChildBackendConstructed.ToArray(), "detector-before-child-backend-resolution");
        CollectionAssert.AreEqual(
            new[] { ProcessingRunTrigger.Manual, ProcessingRunTrigger.Scheduled },
            invocations.Select(invocation => invocation.Request.Trigger).ToArray(),
            "manual-then-scheduled-dispatch");
        Assert.IsTrue(invocations.All(invocation => ReferenceEquals(reporter, invocation.Reporter)), "shared-reporter");
        Assert.IsTrue(invocations.All(invocation => invocation.Token.CanBeCanceled), "coordinator-cancellation-tokens");
        Assert.AreEqual(2, recorder.CreatedCount, "one-child-scope-per-admitted-request");
        Assert.IsFalse(state.IsRunning, "state-finally-idle");
        Assert.IsNull(coordinator.ActiveRequest, "matching-handle-released");
    }

    [TestMethod]
    public async Task ControlPlaneComposition_PreservesAliasesWithoutExecutorReachability()
    {
        var services = CreateControlPlaneRegistrationServices();
        services.AddProcessingControlPlaneServices();

        Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == typeof(IChildProcessingRunBackend)), "one-child-backend");
        Assert.IsFalse(services.Single(descriptor => descriptor.ServiceType == typeof(IChildProcessingRunBackend)).IsKeyedService, "unkeyed-child-backend");
        Assert.AreEqual(0, services.Count(descriptor => descriptor.ServiceType == typeof(ProcessingRunExecutor)), "no-concrete-executor");
        Assert.AreEqual(0, services.Count(descriptor => descriptor.ServiceType == typeof(IProcessingRunExecutor)), "no-executor-alias");

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var coordinator = provider.GetRequiredService<ProcessingRunCoordinator>();
        var state = provider.GetRequiredService<ProcessingState>();
        var reporter = provider.GetRequiredService<ProcessingStateEventReporter>();
        var scheduler = provider.GetRequiredService<ProcessingBackgroundService>();
        var hosted = provider.GetServices<IHostedService>().ToArray();

        Assert.AreSame(coordinator, provider.GetRequiredService<IManualProcessingRunCoordinator>(), "manual-coordinator-alias");
        Assert.AreSame(coordinator, provider.GetRequiredService<IScheduledRunTrigger>(), "scheduled-coordinator-alias");
        Assert.IsTrue(hosted.Any(service => ReferenceEquals(service, coordinator)), "coordinator-hosted-alias");
        Assert.AreSame(state, provider.GetRequiredService<ProcessingState>(), "state-singleton");
        Assert.AreSame(reporter, provider.GetRequiredService<IProcessingEventReporter>(), "reporter-alias");
        Assert.IsTrue(hosted.Any(service => ReferenceEquals(service, scheduler)), "background-hosted-alias");
    }

    private static ServiceProvider CreateDispatchProvider(
        out ChildDispatchRecorder recorder,
        out ChildCancellationFactory cancellationFactory,
        bool gateExecution = false)
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        recorder = new ChildDispatchRecorder(state, reporter, gateExecution);
        cancellationFactory = new ChildCancellationFactory();
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddSingleton(reporter);
        services.AddSingleton(recorder);
        services.AddScoped<IChildProcessingRunBackend, RecordingChildBackend>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static ProcessingRunCoordinator CreateCoordinator(
        ServiceProvider provider,
        Func<Guid> createRunId,
        ChildCancellationFactory cancellationFactory)
    {
        return new ProcessingRunCoordinator(
            provider.GetRequiredService<ProcessingState>(),
            provider.GetRequiredService<ProcessingStateEventReporter>(),
            global::ImmichReverseGeo.Tests.AlwaysHasWorkScheduledRunGate.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProcessingRunCoordinator>.Instance,
            createRunId,
            cancellationFactory,
            null,
            new WorkerJobCoordinator(WorkerJobDescriptors.Registered),
            null,
            TimeProvider.System);
    }

    private static ServiceCollection CreateControlPlaneRegistrationServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton((ConfigService)RuntimeHelpers.GetUninitializedObject(typeof(ConfigService)));
        services.AddSingleton((SkippedAssetsRepository)RuntimeHelpers.GetUninitializedObject(typeof(SkippedAssetsRepository)));
        services.AddSingleton<IProcessingWorkDetector>(global::ImmichReverseGeo.Tests.AlwaysHasWorkScheduledRunGate.Instance);
        return services;
    }

    private sealed class ChildDispatchRecorder(ProcessingState state, ProcessingStateEventReporter expectedReporter, bool gateExecution)
    {
        private readonly ConcurrentQueue<ChildInvocation> _invocations = new();
        private int _createdCount;

        public ProcessingState State { get; } = state;
        public ProcessingStateEventReporter ExpectedReporter { get; } = expectedReporter;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = CreateRelease(gateExecution);
        public IReadOnlyCollection<ChildInvocation> Invocations => _invocations.ToArray();
        public ProcessingRunResult? Result { get; private set; }
        public int CreatedCount => Volatile.Read(ref _createdCount);

        public void Created() => Interlocked.Increment(ref _createdCount);

        public async Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken token)
        {
            _invocations.Enqueue(new ChildInvocation(request, reporter, token));
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            var session = await reporter.OpenRunAsync(request, Now, CancellationToken.None).ConfigureAwait(false);
            await session.DetermineEligibilityAsync(0, CancellationToken.None).ConfigureAwait(false);
            var result = new ProcessingRunResult(request, Now, Now, 0, 0, 0, 0, ProcessingRunOutcome.Completed, null);
            await session.FinishAsync(result).ConfigureAwait(false);
            Result = result;
            return result;
        }

        private static TaskCompletionSource CreateRelease(bool gateExecution)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!gateExecution)
            {
                release.TrySetResult();
            }

            return release;
        }
    }

    private sealed record ChildInvocation(
        ProcessingRunRequest Request,
        IProcessingEventReporter Reporter,
        CancellationToken Token);

    private sealed class RecordingChildBackend : IChildProcessingRunBackend
    {
        private readonly ChildDispatchRecorder _recorder;

        public RecordingChildBackend(ChildDispatchRecorder recorder)
        {
            _recorder = recorder;
            recorder.Created();
        }

        public RecordingChildBackend(ChildDispatchRecorder recorder, PositiveScheduledWorkGate scheduledGate)
            : this(recorder)
        {
            scheduledGate.RecordChildBackendConstruction();
        }

        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            return _recorder.ExecuteAsync(request, reporter, cancellationToken);
        }
    }

    private sealed class ChildCancellationFactory : IProcessingRunCancellationFactory
    {
        private int _createCount;
        public int CreateCount => Volatile.Read(ref _createCount);
        public CancellationToken CreatedToken { get; private set; }

        public IProcessingRunCancellation Create(ProcessingRunRequest request, CancellationToken linkedToken)
        {
            _ = request;
            Interlocked.Increment(ref _createCount);
            var source = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
            CreatedToken = source.Token;
            return new ChildCancellation(source);
        }
    }

    private sealed class ChildCancellation(CancellationTokenSource source) : IProcessingRunCancellation
    {
        public CancellationToken Token => source.Token;
        public void Cancel() => source.Cancel();
        public void Dispose() => source.Dispose();
    }

    private sealed class PositiveScheduledWorkGate : IProcessingWorkDetector
    {
        public int Calls { get; private set; }
        public List<int> CallsWhenChildBackendConstructed { get; } = [];

        public Task<ProcessingWorkDetectionResult> DetectAsync(ProcessingWorkDetectionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(ProcessingWorkDetectorStub.Result(true));
        }

        public void RecordChildBackendConstruction()
        {
            CallsWhenChildBackendConstructed.Add(Calls);
        }
    }
}
