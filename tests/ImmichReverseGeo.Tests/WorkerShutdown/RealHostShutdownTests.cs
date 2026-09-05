using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.WorkerShutdown;

[TestClass]
[TestCategory("Change29")]
public class RealHostShutdownTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    [TestMethod]
    public async Task ApplicationStopping_FencesBeforeLaterHostedServiceStopAndExpiredHostTokenCannotReleaseRun()
    {
        var execution = new OwnedExecution();
        var laterService = new LaterService();
        using var host = CreateHost(execution, laterService);
        await host.StartAsync();
        var coordinator = host.Services.GetRequiredService<ProcessingRunCoordinator>();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await coordinator.TriggerManualAsync());

        using var expiredHostToken = new CancellationTokenSource();
        expiredHostToken.Cancel();
        var hostStop = host.StopAsync(expiredHostToken.Token);
        try
        {
            await laterService.StopEntered.Task.WaitAsync(Bound);
            await execution.CancellationObserved.Task.WaitAsync(Bound);
            var shutdown = coordinator.BeginShutdown();

            Assert.AreEqual(ProcessingRunAdmissionResult.Stopping, await coordinator.TriggerManualAsync());
            Assert.IsNotNull(coordinator.ActiveRequest);
            Assert.IsFalse(shutdown.IsCompleted);
            Assert.AreSame(shutdown, coordinator.StopAsync(expiredHostToken.Token));
            Assert.IsFalse(hostStop.IsCompleted);
        }
        finally
        {
            execution.Finish();
            laterService.StopRelease.TrySetResult();
            await hostStop.WaitAsync(Bound);
        }

        Assert.IsNull(coordinator.ActiveRequest);
        Assert.AreEqual(1, execution.InvocationCount);
        Assert.IsNull(host.Services.GetRequiredService<ProcessingState>().LastError);
        Assert.IsNull(host.Services.GetRequiredService<ProcessingState>().LastRunCompleted);
    }

    [TestMethod]
    public async Task LaterHostedStartupFailure_ProviderDisposalJoinsTheCapturedActiveRun()
    {
        var execution = new OwnedExecution();
        var laterService = new LaterService { FailAfterAdmission = true };
        var host = CreateHost(execution, laterService);
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.AreSame(laterService.StartFailure, failure);
        var coordinator = host.Services.GetRequiredService<ProcessingRunCoordinator>();

        var disposal = Task.Run(host.Dispose);
        try
        {
            await execution.CancellationObserved.Task.WaitAsync(Bound);
            Assert.IsFalse(disposal.IsCompleted);
            Assert.IsNotNull(coordinator.ActiveRequest);
            Assert.AreEqual(ProcessingRunAdmissionResult.Stopping, await coordinator.TriggerManualAsync());
        }
        finally
        {
            execution.Finish();
            await disposal.WaitAsync(Bound);
        }

        Assert.IsNull(coordinator.ActiveRequest);
        Assert.AreEqual(1, execution.InvocationCount);
    }

    private static IHost CreateHost(OwnedExecution execution, LaterService laterService)
    {
        return new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton<ProcessingState>();
            services.AddSingleton<ProcessingStateEventReporter>();
            services.AddSingleton(sp => new ProcessingRunCoordinator(
                sp.GetRequiredService<ProcessingState>(),
                sp.GetRequiredService<ProcessingStateEventReporter>(),
                execution,
                NullLogger<ProcessingRunCoordinator>.Instance,
                Guid.NewGuid,
                observer: null,
                applicationLifetime: sp.GetRequiredService<IHostApplicationLifetime>(),
                timeProvider: TimeProvider.System));
            services.AddHostedService(sp => sp.GetRequiredService<ProcessingRunCoordinator>());
            services.AddHostedService(sp =>
            {
                laterService.Coordinator = sp.GetRequiredService<ProcessingRunCoordinator>();
                return laterService;
            });
        }).Build();
    }

    private sealed class OwnedExecution : IProcessingRunExecutor
    {
        private readonly TaskCompletionSource<ProcessingRunResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationToken _token;
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int InvocationCount { get; private set; }

        public Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            InvocationCount++;
            _token = cancellationToken;
            cancellationToken.Register(() => CancellationObserved.TrySetResult());
            return _completion.Task;
        }

        internal void Finish()
        {
            _completion.TrySetException(new OperationCanceledException(_token));
        }
    }

    private sealed class LaterService : IHostedService
    {
        internal ProcessingRunCoordinator Coordinator { get; set; } = null!;
        internal bool FailAfterAdmission { get; init; }
        internal InvalidOperationException StartFailure { get; } = new("Hosted startup failed.");
        internal TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StopRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (FailAfterAdmission)
            {
                Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await Coordinator.TriggerManualAsync());
                throw StartFailure;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopEntered.TrySetResult();
            return StopRelease.Task;
        }
    }
}
