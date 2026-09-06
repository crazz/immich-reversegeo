using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.WorkerBackendSelection;

[TestClass]
[TestCategory("Change33")]
public sealed class SelectionProcessingBackendTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void AddProcessingServices_DefaultSelectionIsChildWorker()
    {
        var services = CreateProductionRegistrationServices();
        services.AddProcessingServices();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var selection = provider.GetRequiredService<TemporaryProcessingBackendSelection>();

        Assert.AreEqual(ProcessingBackendKind.ChildWorker, selection.Backend, "default-selection");
    }

    [TestMethod]
    public void AddProcessingServices_ExplicitChildWorkerSelectionIsRetained()
    {
        var services = CreateProductionRegistrationServices();
        services.AddProcessingServices(ProcessingBackendKind.ChildWorker);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var selection = provider.GetRequiredService<TemporaryProcessingBackendSelection>();

        Assert.AreEqual(ProcessingBackendKind.ChildWorker, selection.Backend, "explicit-child-selection");
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(2)]
    [DataRow(int.MinValue)]
    [DataRow(int.MaxValue)]
    public void AddProcessingServices_UndefinedSelectionFailsBeforeCompositionEffects(int rawValue)
    {
        var services = new ServiceCollection();

        var failure = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            services.AddProcessingServices((ProcessingBackendKind)rawValue));

        Assert.AreEqual("backend", failure.ParamName, "invalid-selection-parameter");
        Assert.AreEqual(0, services.Count, "invalid-selection-registers-nothing");
    }

    [TestMethod]
    public async Task Coordinator_SelectedBackendReceivesExactDispatchArgumentsForEachKey()
    {
        foreach (var backend in new[] { ProcessingBackendKind.InProcess, ProcessingBackendKind.ChildWorker })
        {
            await using var provider = CreateDispatchProvider(out var recorder, out var cancellationFactory);
            var coordinator = CreateCoordinator(provider, backend, () => Guid.Parse("10000000-0000-0000-0000-000000000001"), cancellationFactory);

            var admission = await coordinator.TriggerManualAsync();
            await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);

            Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, admission, backend + "-admission");
            Assert.AreEqual(1, recorder.Invocations.Count, backend + "-dispatch-count");
            var invocation = recorder.Invocations.Single();
            Assert.AreEqual(backend, invocation.Backend, backend + "-selected-key");
            Assert.AreEqual(1, recorder.CreatedFor(backend), backend + "-selected-adapter-created");
            Assert.AreEqual(0, recorder.CreatedFor(Other(backend)), backend + "-unselected-adapter-not-created");
            Assert.AreEqual(ProcessingRunTrigger.Manual, invocation.Request.Trigger, backend + "-request-trigger");
            Assert.AreEqual(Guid.Parse("10000000-0000-0000-0000-000000000001"), invocation.Request.RunId, backend + "-request-id");
            Assert.AreSame(recorder.Result!.Request, invocation.Request, backend + "-result-request-identity");
            Assert.AreSame(recorder.ExpectedReporter, invocation.Reporter, backend + "-reporter-identity");
            Assert.AreEqual(cancellationFactory.CreatedToken, invocation.Token, backend + "-coordinator-token");
            Assert.AreEqual(ProcessingRunOutcome.Completed, recorder.Result.Outcome, backend + "-success-result");
            Assert.IsFalse(recorder.State.IsRunning, backend + "-returns-idle");
        }
    }

    [TestMethod]
    public async Task Coordinator_ActiveRunRejectsManualAndScheduledTriggersWithoutCreatingAnotherSelectionScope()
    {
        const ProcessingBackendKind backend = ProcessingBackendKind.ChildWorker;
        await using var provider = CreateDispatchProvider(out var recorder, out var cancellationFactory, gateExecution: true);
        var ids = new Queue<Guid>(new[]
        {
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            Guid.Parse("20000000-0000-0000-0000-000000000002")
        });
        var coordinator = CreateCoordinator(provider, backend, ids.Dequeue, cancellationFactory);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await coordinator.TriggerManualAsync(), "first-admission");
        await recorder.Entered.Task.WaitAsync(TestTimeout);

        Assert.AreEqual(ProcessingRunAdmissionResult.AlreadyRunning, await coordinator.TriggerManualAsync(), "duplicate-manual");
        Assert.AreEqual(
            ScheduledTriggerResult.RejectedAlreadyRunning,
            await ((IScheduledRunTrigger)coordinator).TriggerScheduledAsync(CancellationToken.None),
            "duplicate-scheduled");
        Assert.AreEqual(1, cancellationFactory.CreateCount, "duplicate-does-not-create-cts");
        Assert.AreEqual(1, recorder.CreatedFor(backend), "duplicate-does-not-create-second-scope-adapter");
        Assert.AreEqual(1, recorder.Invocations.Count, "duplicate-does-not-resolve-or-dispatch");
        Assert.AreEqual(1, ids.Count, "duplicate-does-not-create-run-id");

        recorder.Release.TrySetResult();
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task AddProcessingServices_DefaultChildWorkerDispatchesManualAndEligibleScheduledRequests()
    {
        var services = CreateProductionRegistrationServices();
        var scheduledGate = new PositiveScheduledWorkGate();
        services.AddProcessingServices();
        ReplaceKeyedBackend(
            services,
            ProcessingBackendKind.InProcess,
            (_, _) => throw new InvalidOperationException("The unselected in-process backend must stay lazy."));
        ReplaceKeyedBackend(
            services,
            ProcessingBackendKind.ChildWorker,
            (sp, _) => new DefaultSelectionChildWorkerBackend(
                sp.GetRequiredService<SelectionDispatchRecorder>(),
                sp.GetRequiredService<PositiveScheduledWorkGate>()));
        services.AddSingleton<IScheduledRunWorkGate>(scheduledGate);
        services.AddSingleton(scheduledGate);
        services.AddSingleton(sp => new SelectionDispatchRecorder(
            sp.GetRequiredService<ProcessingState>(),
            sp.GetRequiredService<ProcessingStateEventReporter>(),
            gateExecution: false));

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var coordinator = provider.GetRequiredService<ProcessingRunCoordinator>();
        var recorder = provider.GetRequiredService<SelectionDispatchRecorder>();
        var reporter = provider.GetRequiredService<ProcessingStateEventReporter>();
        var state = provider.GetRequiredService<ProcessingState>();

        Assert.AreEqual(ProcessingBackendKind.ChildWorker,
            provider.GetRequiredService<TemporaryProcessingBackendSelection>().Backend,
            "ordinary-composition-default");
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await coordinator.TriggerManualAsync(), "manual-admission");
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal,
            await ((IScheduledRunTrigger)coordinator).TriggerScheduledAsync(CancellationToken.None),
            "scheduled-admission");
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);

        var invocations = recorder.Invocations.ToArray();
        Assert.AreEqual(2, invocations.Length, "one-backend-per-admitted-trigger");
        Assert.AreEqual(1, scheduledGate.Calls, "eligible-scheduled-detector-once");
        CollectionAssert.AreEqual(new[] { 0, 1 }, scheduledGate.CallsWhenChildBackendConstructed.ToArray(), "detector-before-selected-backend-resolution");
        CollectionAssert.AreEqual(
            new[] { ProcessingRunTrigger.Manual, ProcessingRunTrigger.Scheduled },
            invocations.Select(invocation => invocation.Request.Trigger).ToArray(),
            "manual-then-scheduled-dispatch");
        Assert.IsTrue(invocations.All(invocation => invocation.Backend == ProcessingBackendKind.ChildWorker), "selected-child-only");
        Assert.IsTrue(invocations.All(invocation => ReferenceEquals(reporter, invocation.Reporter)), "shared-reporter");
        Assert.IsTrue(invocations.All(invocation => invocation.Token.CanBeCanceled), "coordinator-cancellation-tokens");
        Assert.AreEqual(2, recorder.CreatedFor(ProcessingBackendKind.ChildWorker), "one-child-scope-per-admitted-request");
        Assert.AreEqual(0, recorder.CreatedFor(ProcessingBackendKind.InProcess), "unselected-in-process-never-created");
        Assert.IsFalse(state.IsRunning, "state-finally-idle");
        Assert.IsNull(coordinator.ActiveRequest, "matching-handle-released");
    }

    [TestMethod]
    public async Task AddProcessingServices_PreservesSingletonCoordinatorControlPlaneAliases()
    {
        var services = CreateProductionRegistrationServices();
        services.AddProcessingServices();

        // Removal sequence: blocks 34/35 choose ChildWorker internally, block 36 can resolve
        // neither key, block 37 flips this default, and block 38 removes the temporary keys.
        Assert.AreEqual(2, services.Count(descriptor => descriptor.ServiceType == typeof(IProcessingRunBackend)), "temporary-keyed-registrations");

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var coordinator = provider.GetRequiredService<ProcessingRunCoordinator>();
        var state = provider.GetRequiredService<ProcessingState>();
        var reporter = provider.GetRequiredService<ProcessingStateEventReporter>();
        var selection = provider.GetRequiredService<TemporaryProcessingBackendSelection>();
        var executor = provider.GetRequiredService<ProcessingRunExecutor>();
        var scheduler = provider.GetRequiredService<ProcessingBackgroundService>();
        var hosted = provider.GetServices<IHostedService>().ToArray();

        Assert.AreSame(coordinator, provider.GetRequiredService<IManualProcessingRunCoordinator>(), "manual-coordinator-alias");
        Assert.AreSame(coordinator, provider.GetRequiredService<IScheduledRunTrigger>(), "scheduled-coordinator-alias");
        Assert.IsTrue(hosted.Any(service => ReferenceEquals(service, coordinator)), "coordinator-hosted-alias");
        Assert.AreSame(state, provider.GetRequiredService<ProcessingState>(), "state-singleton");
        Assert.AreSame(reporter, provider.GetRequiredService<IProcessingEventReporter>(), "reporter-alias");
        Assert.AreSame(selection, provider.GetRequiredService<TemporaryProcessingBackendSelection>(), "selection-singleton");
        Assert.AreSame(executor, provider.GetRequiredService<IProcessingRunExecutor>(), "executor-alias");
        Assert.IsTrue(hosted.Any(service => ReferenceEquals(service, scheduler)), "background-hosted-alias");
    }

    private static ServiceProvider CreateDispatchProvider(
        out SelectionDispatchRecorder recorder,
        out SelectionCancellationFactory cancellationFactory,
        bool gateExecution = false)
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        recorder = new SelectionDispatchRecorder(state, reporter, gateExecution);
        cancellationFactory = new SelectionCancellationFactory();
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddSingleton(reporter);
        services.AddSingleton(recorder);
        services.AddKeyedScoped<IProcessingRunBackend, SelectionInProcessBackend>(ProcessingBackendKind.InProcess);
        services.AddKeyedScoped<IProcessingRunBackend, SelectionChildWorkerBackend>(ProcessingBackendKind.ChildWorker);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static ProcessingRunCoordinator CreateCoordinator(
        ServiceProvider provider,
        ProcessingBackendKind backend,
        Func<Guid> createRunId,
        SelectionCancellationFactory cancellationFactory)
    {
        return new ProcessingRunCoordinator(
            provider.GetRequiredService<ProcessingState>(),
            provider.GetRequiredService<ProcessingStateEventReporter>(),
            global::ImmichReverseGeo.Tests.AlwaysHasWorkScheduledRunGate.Instance,
            new TemporaryProcessingBackendSelection(backend),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProcessingRunCoordinator>.Instance,
            createRunId,
            cancellationFactory,
            null,
            null,
            TimeProvider.System);
    }

    private static ProcessingBackendKind Other(ProcessingBackendKind backend)
    {
        return backend == ProcessingBackendKind.InProcess
            ? ProcessingBackendKind.ChildWorker
            : ProcessingBackendKind.InProcess;
    }

    private static void ReplaceKeyedBackend(
        IServiceCollection services,
        ProcessingBackendKind backend,
        Func<IServiceProvider, object?, IProcessingRunBackend> factory)
    {
        foreach (var descriptor in services
                     .Where(descriptor => descriptor.ServiceType == typeof(IProcessingRunBackend)
                         && Equals(descriptor.ServiceKey, backend))
                     .ToArray())
        {
            services.Remove(descriptor);
        }

        services.AddKeyedScoped<IProcessingRunBackend>(backend, factory);
    }

    private static ServiceCollection CreateProductionRegistrationServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ProcessingBackgroundService>>(NullLogger<ProcessingBackgroundService>.Instance);
        services.AddSingleton((ConfigService)RuntimeHelpers.GetUninitializedObject(typeof(ConfigService)));
        services.AddSingleton((AdministrativeAreaResolverService)RuntimeHelpers.GetUninitializedObject(typeof(AdministrativeAreaResolverService)));
        services.AddSingleton((ImmichDbRepository)RuntimeHelpers.GetUninitializedObject(typeof(ImmichDbRepository)));
        services.AddSingleton((ImmichReverseGeo.Overture.Services.OverturePlacesService)RuntimeHelpers.GetUninitializedObject(typeof(ImmichReverseGeo.Overture.Services.OverturePlacesService)));
        services.AddSingleton((SkippedAssetsRepository)RuntimeHelpers.GetUninitializedObject(typeof(SkippedAssetsRepository)));
        services.AddSingleton<IScheduledRunWorkGate>(global::ImmichReverseGeo.Tests.AlwaysHasWorkScheduledRunGate.Instance);
        return services;
    }

    private sealed class SelectionDispatchRecorder(ProcessingState state, ProcessingStateEventReporter expectedReporter, bool gateExecution)
    {
        private readonly ConcurrentQueue<SelectionInvocation> _invocations = new();
        private readonly ConcurrentDictionary<ProcessingBackendKind, int> _created = new();

        public ProcessingState State { get; } = state;
        public ProcessingStateEventReporter ExpectedReporter { get; } = expectedReporter;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = CreateRelease(gateExecution);
        public IReadOnlyCollection<SelectionInvocation> Invocations => _invocations.ToArray();
        public ProcessingRunResult? Result { get; private set; }

        public void Created(ProcessingBackendKind backend) => _created.AddOrUpdate(backend, 1, (_, count) => count + 1);
        public int CreatedFor(ProcessingBackendKind backend) => _created.TryGetValue(backend, out var count) ? count : 0;

        public async Task<ProcessingRunResult> ExecuteAsync(
            ProcessingBackendKind backend,
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken token)
        {
            _invocations.Enqueue(new SelectionInvocation(backend, request, reporter, token));
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

    private sealed record SelectionInvocation(
        ProcessingBackendKind Backend,
        ProcessingRunRequest Request,
        IProcessingEventReporter Reporter,
        CancellationToken Token);

    private sealed class SelectionInProcessBackend : IProcessingRunBackend
    {
        private readonly SelectionDispatchRecorder _recorder;

        public SelectionInProcessBackend(SelectionDispatchRecorder recorder)
        {
            _recorder = recorder;
            recorder.Created(ProcessingBackendKind.InProcess);
        }

        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            ImmichReverseGeo.Core.Processing.IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            return _recorder.ExecuteAsync(ProcessingBackendKind.InProcess, request, reporter, cancellationToken);
        }
    }

    private sealed class SelectionChildWorkerBackend : IProcessingRunBackend
    {
        private readonly SelectionDispatchRecorder _recorder;

        public SelectionChildWorkerBackend(SelectionDispatchRecorder recorder)
        {
            _recorder = recorder;
            recorder.Created(ProcessingBackendKind.ChildWorker);
        }

        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            ImmichReverseGeo.Core.Processing.IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            return _recorder.ExecuteAsync(ProcessingBackendKind.ChildWorker, request, reporter, cancellationToken);
        }
    }

    private sealed class DefaultSelectionChildWorkerBackend : IProcessingRunBackend
    {
        private readonly SelectionDispatchRecorder _recorder;

        public DefaultSelectionChildWorkerBackend(
            SelectionDispatchRecorder recorder,
            PositiveScheduledWorkGate scheduledGate)
        {
            _recorder = recorder;
            scheduledGate.RecordChildBackendConstruction();
            recorder.Created(ProcessingBackendKind.ChildWorker);
        }

        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            ImmichReverseGeo.Core.Processing.IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            return _recorder.ExecuteAsync(ProcessingBackendKind.ChildWorker, request, reporter, cancellationToken);
        }
    }

    private sealed class SelectionCancellationFactory : IProcessingRunCancellationFactory
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
            return new SelectionCancellation(source);
        }
    }

    private sealed class SelectionCancellation(CancellationTokenSource source) : IProcessingRunCancellation
    {
        public CancellationToken Token => source.Token;
        public void Cancel() => source.Cancel();
        public void Dispose() => source.Dispose();
    }

    private sealed class PositiveScheduledWorkGate : IScheduledRunWorkGate
    {
        public int Calls { get; private set; }

        public List<int> CallsWhenChildBackendConstructed { get; } = [];

        public Task<bool> HasWorkAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(true);
        }

        public void RecordChildBackendConstruction()
        {
            CallsWhenChildBackendConstructed.Add(Calls);
        }
    }
}
