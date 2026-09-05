using System.Collections.Concurrent;
using System.Reflection;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public sealed class ProcessingCompositionTurn2Tests
{
    [TestMethod]
    public void DependencyGraph_ResolvesExactAliasGroupsHostedOrderReverseStopAndNoCycle()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<ProcessingBackgroundService>>(NullLogger<ProcessingBackgroundService>.Instance);
        services.AddSingleton((ConfigService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ConfigService)));
        services.AddSingleton((AdministrativeAreaResolverService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AdministrativeAreaResolverService)));
        services.AddSingleton((ImmichDbRepository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ImmichDbRepository)));
        services.AddSingleton((ImmichReverseGeo.Overture.Services.OverturePlacesService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ImmichReverseGeo.Overture.Services.OverturePlacesService)));
        services.AddSingleton((SkippedAssetsRepository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SkippedAssetsRepository)));
        services.AddProcessingServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var coordinator = provider.GetRequiredService<ProcessingRunCoordinator>();
        var manual = provider.GetRequiredService<IManualProcessingRunCoordinator>();
        var scheduled = provider.GetRequiredService<IScheduledRunTrigger>();
        var scheduler = provider.GetRequiredService<ProcessingBackgroundService>();
        var hosted = provider.GetServices<IHostedService>().ToArray();

        Assert.AreSame(coordinator, manual);
        Assert.AreSame(coordinator, scheduled);
        CollectionAssert.AreEqual(new IHostedService[] { coordinator, scheduler }, hosted);
        CollectionAssert.AreEqual(new IHostedService[] { scheduler, coordinator }, hosted.Reverse().ToArray());
        Assert.IsFalse(ReferenceEquals(coordinator, scheduler));
        Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == typeof(ProcessingRunCoordinator)));
        Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == typeof(ProcessingBackgroundService)));
        CollectionAssert.AreEqual(
            new[] { typeof(IScheduledRunTrigger) },
            typeof(ProcessingBackgroundService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(field => field.FieldType == typeof(IScheduledRunTrigger))
                .Select(field => field.FieldType)
                .ToArray());
        Assert.IsFalse(typeof(ProcessingRunCoordinator)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => parameter.ParameterType == typeof(ProcessingBackgroundService)));
    }

    [TestMethod]
    public async Task RealHost_StartsCoordinatorBeforeSchedulerAndStopsSchedulerBeforeCoordinatorDrain()
    {
        var operations = new ConcurrentQueue<string>();
        var lifecycle = new LifecycleObserver(operations);
        var executor = new LifecycleExecutor(operations);
        var schedulerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton((ConfigService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ConfigService)));
                services.AddSingleton((AdministrativeAreaResolverService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AdministrativeAreaResolverService)));
                services.AddSingleton((ImmichDbRepository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ImmichDbRepository)));
                services.AddSingleton((ImmichReverseGeo.Overture.Services.OverturePlacesService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ImmichReverseGeo.Overture.Services.OverturePlacesService)));
                services.AddSingleton((SkippedAssetsRepository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SkippedAssetsRepository)));
                services.AddSingleton<IProcessingRunCoordinatorObserver>(lifecycle);
                services.AddProcessingServices();
                services.AddSingleton<IProcessingRunExecutor>(executor);
                services.AddSingleton<IProcessingScheduleConfiguration>(new DisabledSchedule());
                services.RemoveAll<ProcessingBackgroundService>();
                services.AddSingleton<ProcessingBackgroundService>(sp => new ObservedScheduler(
                    NullLogger<ProcessingBackgroundService>.Instance,
                    sp.GetRequiredService<ProcessingState>(),
                    sp.GetRequiredService<IProcessingScheduleConfiguration>(),
                    () =>
                    {
                        operations.Enqueue("scheduler:started");
                        schedulerStarted.TrySetResult();
                        return Task.CompletedTask;
                    },
                    TimeProvider.System,
                    sp.GetRequiredService<IScheduledRunTrigger>(),
                    operations));
            })
            .Build();
        using var startBound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StartAsync(startBound.Token);
        await schedulerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var coordinator = host.Services.GetRequiredService<ProcessingRunCoordinator>();
        var scheduler = (ObservedScheduler)host.Services.GetRequiredService<ProcessingBackgroundService>();
        var hosted = host.Services.GetServices<IHostedService>().ToArray();
        Assert.AreSame(coordinator, host.Services.GetRequiredService<IManualProcessingRunCoordinator>());
        Assert.AreSame(coordinator, host.Services.GetRequiredService<IScheduledRunTrigger>());
        CollectionAssert.AreEqual(new IHostedService[] { coordinator, scheduler }, hosted);
        CollectionAssert.AreEqual(new[] { "coordinator:started", "scheduler:started" }, operations.ToArray());

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await coordinator.TriggerManualAsync());
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var stopBound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stopping = host.StopAsync(stopBound.Token);
        try
        {
            Assert.AreEqual(ProcessingRunAdmissionResult.Stopping, await coordinator.TriggerManualAsync());
            await Task.WhenAll(
                    executor.CancelObserved.Task,
                    lifecycle.Stopping.Task,
                    scheduler.Stopped.Task)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(stopping.IsCompleted);
            var beforeRelease = operations.ToArray();
            CollectionAssert.AreEquivalent(
                new[] { "coordinator:started", "scheduler:started", "executor:entered", "executor:cancelled", "scheduler:stopping", "scheduler:stopped", "coordinator:stopping" },
                beforeRelease);
            AssertInOrder(beforeRelease, "coordinator:started", "scheduler:started", "executor:entered");
            AssertInOrder(beforeRelease, "coordinator:stopping", "executor:cancelled");
            AssertInOrder(beforeRelease, "scheduler:stopping", "scheduler:stopped");
        }
        finally
        {
            executor.Release.TrySetResult();
        }

        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        var completed = operations.ToArray();
        CollectionAssert.AreEquivalent(
            new[] { "coordinator:started", "scheduler:started", "executor:entered", "executor:cancelled", "scheduler:stopping", "scheduler:stopped", "coordinator:stopping", "executor:released", "coordinator:stopped" },
            completed,
            string.Join(" | ", operations));
        AssertInOrder(completed, "coordinator:started", "scheduler:started", "executor:entered");
        AssertInOrder(completed, "coordinator:stopping", "executor:cancelled", "executor:released", "coordinator:stopped");
        AssertInOrder(completed, "scheduler:stopping", "scheduler:stopped", "coordinator:stopped");
    }

    [TestMethod]
    public void ScheduledContractAndProductionSurface_AreExactWithoutRunOnceWorkerProtocolProcessCronOrLockScope()
    {
        var method = typeof(IScheduledRunTrigger).GetMethods().Single();
        Assert.AreEqual(nameof(IScheduledRunTrigger.TriggerScheduledAsync), method.Name);
        Assert.AreEqual(typeof(Task<ScheduledTriggerResult>), method.ReturnType);
        CollectionAssert.AreEqual(new[] { typeof(CancellationToken) }, method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        CollectionAssert.AreEqual(
            new[] { nameof(ScheduledTriggerResult.RejectedAlreadyRunning), nameof(ScheduledTriggerResult.AcceptedAfterTerminal) },
            Enum.GetNames<ScheduledTriggerResult>());
        Assert.IsNull(typeof(ProcessingRunCoordinator).Assembly.GetType("ImmichReverseGeo.Web.Services.ProcessingRunExecution"));
        var forbiddenNames = new[] { "RunOnce", "Worker", "Protocol", "Cron", "Npgsql", "Advisory", "Semaphore" };
        Assert.IsFalse(typeof(ProcessingRunCoordinator)
            .GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(member => forbiddenNames.Any(forbidden => member.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase))));
        Assert.IsFalse(typeof(ProcessingBackgroundService)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Any(field => field.FieldType == typeof(IProcessingRunExecutor)
                || field.FieldType == typeof(ProcessingStateEventReporter)
                || field.FieldType == typeof(CancellationTokenSource)
                || field.FieldType == typeof(SemaphoreSlim)));
    }

    private sealed class DisabledSchedule : IProcessingScheduleConfiguration
    {
        public Task<ProcessingScheduleSnapshot> GetSnapshotAsync() => Task.FromResult(new ProcessingScheduleSnapshot(false, "0 0 * * *"));
    }

    private sealed class LifecycleObserver(ConcurrentQueue<string> operations) : IProcessingRunCoordinatorObserver
    {
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void CoordinatorStarted() => operations.Enqueue("coordinator:started");
        public void CoordinatorStopping()
        {
            operations.Enqueue("coordinator:stopping");
            Stopping.TrySetResult();
        }
        public void CoordinatorStopped() => operations.Enqueue("coordinator:stopped");
        public ValueTask BeforeDisposeAsync(ProcessingRunRequest request, CancellationToken activeToken) => ValueTask.CompletedTask;
    }

    private sealed class LifecycleExecutor(ConcurrentQueue<string> operations) : IProcessingRunExecutor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken token)
        {
            using var registration = token.Register(() =>
            {
                operations.Enqueue("executor:cancelled");
                CancelObserved.TrySetResult();
            });
            operations.Enqueue("executor:entered");
            Entered.TrySetResult();
            var session = await reporter.OpenRunAsync(request, TimeProvider.System.GetUtcNow(), CancellationToken.None);
            await session.DetermineEligibilityAsync(0, CancellationToken.None);
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            operations.Enqueue("executor:released");
            throw new OperationCanceledException(token);
        }
    }

    private sealed class ObservedScheduler(
        ILogger<ProcessingBackgroundService> logger,
        ProcessingState state,
        IProcessingScheduleConfiguration configuration,
        Func<Task> initialise,
        TimeProvider timeProvider,
        IScheduledRunTrigger trigger,
        ConcurrentQueue<string> operations)
        : ProcessingBackgroundService(logger, state, configuration, initialise, timeProvider, trigger)
    {
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            operations.Enqueue("scheduler:stopping");
            await base.StopAsync(cancellationToken);
            operations.Enqueue("scheduler:stopped");
            Stopped.TrySetResult();
        }
    }

    private static void AssertInOrder(IReadOnlyList<string> actual, params string[] expected)
    {
        var actualIndex = 0;
        foreach (var operation in expected)
        {
            while (actualIndex < actual.Count && actual[actualIndex] != operation)
            {
                actualIndex++;
            }

            Assert.IsTrue(
                actualIndex < actual.Count,
                $"Missing ordered operation '{operation}' in: {string.Join(" | ", actual)}");
            actualIndex++;
        }
    }
}
