using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.WorkerHost;

internal sealed class InternalWorkerLifecycleService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<InternalWorkerLifecycleService> _logger;

    public InternalWorkerLifecycleService(
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime applicationLifetime,
        ILogger<InternalWorkerLifecycleService> logger)
    {
        _scopeFactory = scopeFactory;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Exception? primaryFailure = null;
        IAsyncDisposable? scopeDisposal = null;

        try
        {
            await WaitForApplicationStartedAsync(stoppingToken);
            var scope = _scopeFactory.CreateAsyncScope();
            scopeDisposal = scope;
            await RunOneWorkerLifecycleAsync(scope.ServiceProvider, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            try
            {
                if (scopeDisposal is not null)
                {
                    await scopeDisposal.DisposeAsync();
                }
            }
            catch
            {
                primaryFailure ??= new InvalidOperationException(WorkerSafeFailure.Cleanup().Category);
                LogSafely("worker-cleanup-failed");
            }
            finally
            {
                try
                {
                    _applicationLifetime.StopApplication();
                }
                catch
                {
                    primaryFailure ??= new InvalidOperationException(WorkerSafeFailure.Cleanup().Category);
                    LogSafely("worker-cleanup-failed");
                }
            }
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    private async Task RunOneWorkerLifecycleAsync(IServiceProvider services, CancellationToken stoppingToken)
    {
        IWorkerPreRequestFinality? preRequestFinality = null;
        var preRequestFinalityState = new PreRequestFinalityState();
        var requestAccepted = false;
        var phase = PreRequestPhase.Startup;

        try
        {
            preRequestFinality = services.GetRequiredService<IWorkerPreRequestFinality>();
            var initializer = services.GetRequiredService<IWorkerStartupInitializer>();
            var availability = services.GetRequiredService<IWorkerTransportAvailability>();

            await initializer.InitialiseAsync(stoppingToken);

            if (!availability.IsConfigured)
            {
                await CompletePreRequestAsync(
                    preRequestFinality,
                    WorkerPreRequestOutcome.TransportNotConfigured(),
                    CancellationToken.None,
                    preRequestFinalityState);
                return;
            }

            phase = PreRequestPhase.Readiness;
            var readiness = services.GetRequiredService<IWorkerReadinessPublisher>();
            await readiness.PublishAsync(stoppingToken);

            phase = PreRequestPhase.Acquisition;
            var acquirer = services.GetRequiredService<IInitialProcessingRunAcquirer>();
            var acquisition = await acquirer.AcquireAsync(stoppingToken);

            switch (acquisition)
            {
                case InitialProcessingRunAcquisition.Accepted accepted:
                    requestAccepted = true;
                    await ExecuteAcceptedAsync(services, accepted.Lease, stoppingToken);
                    break;
                case InitialProcessingRunAcquisition.PreRequestEof:
                    await CompletePreRequestAsync(
                        preRequestFinality,
                        WorkerPreRequestOutcome.CleanEndOfInput(),
                        CancellationToken.None,
                        preRequestFinalityState);
                    break;
                case InitialProcessingRunAcquisition.PreRequestFailure failure:
                    await CompletePreRequestAsync(
                        preRequestFinality,
                        WorkerPreRequestOutcome.Failure(failure.Failure),
                        CancellationToken.None,
                        preRequestFinalityState);
                    break;
                default:
                    throw new InvalidOperationException("Unknown initial processing-run acquisition outcome.");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch when (preRequestFinality is not null && !preRequestFinalityState.Started && !requestAccepted)
        {
            await CompletePreRequestAsync(
                preRequestFinality,
                WorkerPreRequestOutcome.Failure(CreateFailure(phase)),
                CancellationToken.None,
                preRequestFinalityState);
        }
    }

    private async Task ExecuteAcceptedAsync(
        IServiceProvider services,
        IProcessingRunLease lease,
        CancellationToken stoppingToken)
    {
        var request = lease.Request;
        Exception? primaryFailure = null;
        var finalityStarted = false;
        CancellationTokenSource? linkedCancellation = null;

        IWorkerAcceptedRunFinality? finality = null;

        try
        {
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.CancellationToken);
            finality = services.GetRequiredService<IWorkerAcceptedRunFinality>();
            var executor = services.GetRequiredService<IProcessingRunExecutor>();
            var reporter = services.GetRequiredService<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>();
            var result = await executor.ExecuteAsync(request, reporter, linkedCancellation.Token);
            finalityStarted = true;
            await finality.CompleteAsync(request, result, CancellationToken.None);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;

            if (finality is not null
                && !finalityStarted
                && !(exception is OperationCanceledException && linkedCancellation?.IsCancellationRequested == true))
            {
                finalityStarted = true;

                try
                {
                    await finality.FailAsync(
                        request,
                        WorkerSafeFailure.AcceptedInfrastructure(),
                        CancellationToken.None);
                }
                catch
                {
                    LogSafely("worker-accepted-finality-failed");
                }
            }
        }
        finally
        {
            try
            {
                await lease.SettleAsync(CancellationToken.None);
            }
            catch
            {
                primaryFailure ??= new InvalidOperationException(WorkerSafeFailure.Cleanup().Category);
                LogSafely("worker-cleanup-failed");
            }

            try
            {
                linkedCancellation?.Dispose();
            }
            catch
            {
                primaryFailure ??= new InvalidOperationException(WorkerSafeFailure.Cleanup().Category);
                LogSafely("worker-cleanup-failed");
            }

            try
            {
                await lease.DisposeAsync();
            }
            catch
            {
                primaryFailure ??= new InvalidOperationException(WorkerSafeFailure.Cleanup().Category);
                LogSafely("worker-cleanup-failed");
            }
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    private void LogSafely(string category)
    {
        try
        {
            _logger.LogWarning(category);
        }
        catch
        {
        }
    }

    private static async Task CompletePreRequestAsync(
        IWorkerPreRequestFinality preRequestFinality,
        WorkerPreRequestOutcome outcome,
        CancellationToken cancellationToken,
        PreRequestFinalityState state)
    {
        state.Started = true;
        await preRequestFinality.CompleteAsync(outcome, cancellationToken);
    }

    private async Task WaitForApplicationStartedAsync(CancellationToken stoppingToken)
    {
        var applicationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startedRegistration = _applicationLifetime.ApplicationStarted.Register(() => applicationStarted.TrySetResult());
        using var stoppingRegistration = stoppingToken.Register(() => applicationStarted.TrySetCanceled(stoppingToken));
        await applicationStarted.Task.WaitAsync(stoppingToken);
    }

    private static WorkerSafeFailure CreateFailure(PreRequestPhase phase)
    {
        return phase switch
        {
            PreRequestPhase.Startup => WorkerSafeFailure.Startup(),
            PreRequestPhase.Readiness => WorkerSafeFailure.Readiness(),
            PreRequestPhase.Acquisition => WorkerSafeFailure.Acquisition(),
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
        };
    }

    private sealed class PreRequestFinalityState
    {
        public bool Started { get; set; }
    }

    private enum PreRequestPhase
    {
        Startup,
        Readiness,
        Acquisition
    }
}
