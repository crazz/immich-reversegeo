using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
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
    private readonly WorkerProcessExitOutcomeAccumulator _outcomes;

    public InternalWorkerLifecycleService(
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime applicationLifetime,
        ILogger<InternalWorkerLifecycleService> logger,
        WorkerProcessExitOutcomeAccumulator outcomes)
    {
        _scopeFactory = scopeFactory;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
        _outcomes = outcomes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Exception? primaryFailure = null;
        OutOfMemoryException? firstFatal = null;
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
            _outcomes.Add(WorkerProcessExitFact.ShutdownCancelled());
        }
        catch (OutOfMemoryException exception)
        {
            firstFatal ??= exception;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            _outcomes.Add(WorkerProcessExitFact.StartupInfrastructure());
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
            catch (OutOfMemoryException exception)
            {
                firstFatal ??= exception;
            }
            catch
            {
                primaryFailure ??= new InvalidOperationException(WorkerSafeFailure.Cleanup().Category);
                _outcomes.Add(WorkerProcessExitFact.CleanupInfrastructure());
                LogSafely("worker-cleanup-failed");
            }
            finally
            {
                try
                {
                    _applicationLifetime.StopApplication();
                }
                catch (OutOfMemoryException exception)
                {
                    firstFatal ??= exception;
                }
                catch
                {
                    primaryFailure ??= new InvalidOperationException(WorkerSafeFailure.Cleanup().Category);
                    _outcomes.Add(WorkerProcessExitFact.CleanupInfrastructure());
                    LogSafely("worker-cleanup-failed");
                }
            }
        }

        if (firstFatal is not null)
        {
            ExceptionDispatchInfo.Capture(firstFatal).Throw();
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
                _outcomes.Add(WorkerProcessExitFact.TransportInfrastructure());
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
                    _outcomes.Add(WorkerProcessExitFact.InputInvalid());
                    await CompletePreRequestAsync(
                        preRequestFinality,
                        WorkerPreRequestOutcome.CleanEndOfInput(),
                        CancellationToken.None,
                        preRequestFinalityState);
                    break;
                case InitialProcessingRunAcquisition.PreRequestFailure failure:
                    _outcomes.Add(MapSafeFailure(failure.Failure));
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
            _outcomes.Add(WorkerProcessExitFact.ShutdownCancelled());
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (Exception) when (preRequestFinality is not null && !preRequestFinalityState.Started && !requestAccepted)
        {
            _outcomes.Add(MapPreRequestException(phase));
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
        OutOfMemoryException? firstFatal = null;
        var finalityStarted = false;
        CancellationTokenSource? linkedCancellation = null;
        WorkerInputPumpFinality? inputFinality = null;

        IWorkerAcceptedRunFinality? finality = null;

        try
        {
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.CancellationToken);
            finality = services.GetRequiredService<IWorkerAcceptedRunFinality>();
            var executor = services.GetRequiredService<IProcessingRunExecutor>();
            var reporter = services.GetRequiredService<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>();
            lease.NotifyExecutionStarting();
            var result = await executor.ExecuteAsync(request, reporter, linkedCancellation.Token);
            finalityStarted = true;

            var finalityOutcome = await CompleteAcceptedFinalityAsync(finality, request, result);
            if (finalityOutcome is AcceptedRunFinalityOutcome.Completed)
            {
                _outcomes.Add(MapProcessingResult(result));
            }
            else
            {
                _outcomes.Add(WorkerProcessExitFact.ExecutionInfrastructure());
                LogSafely("worker-accepted-finality-failed");
            }
        }
        catch (OutOfMemoryException exception)
        {
            firstFatal ??= exception;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            _outcomes.Add(WorkerProcessExitFact.ExecutionInfrastructure());

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
                catch (OutOfMemoryException finalityFatal)
                {
                    firstFatal ??= finalityFatal;
                }
                catch
                {
                    _outcomes.Add(WorkerProcessExitFact.ExecutionInfrastructure());
                    LogSafely("worker-accepted-finality-failed");
                }
            }
        }
        finally
        {
            try
            {
                inputFinality = await lease.SettleAsync(CancellationToken.None);
            }
            catch (OutOfMemoryException exception)
            {
                firstFatal ??= exception;
            }
            catch
            {
                primaryFailure ??= new InvalidOperationException(WorkerSafeFailure.Cleanup().Category);
                _outcomes.Add(WorkerProcessExitFact.CleanupInfrastructure());
                LogSafely("worker-cleanup-failed");
            }

            try
            {
                linkedCancellation?.Dispose();
            }
            catch (OutOfMemoryException exception)
            {
                firstFatal ??= exception;
            }
            catch
            {
                primaryFailure ??= new InvalidOperationException(WorkerSafeFailure.Cleanup().Category);
                _outcomes.Add(WorkerProcessExitFact.CleanupInfrastructure());
                LogSafely("worker-cleanup-failed");
            }

            try
            {
                await lease.DisposeAsync();
            }
            catch (OutOfMemoryException exception)
            {
                firstFatal ??= exception;
            }
            catch
            {
                primaryFailure ??= new InvalidOperationException(WorkerSafeFailure.Cleanup().Category);
                _outcomes.Add(WorkerProcessExitFact.CleanupInfrastructure());
                LogSafely("worker-cleanup-failed");
            }
        }

        AddInputFinality(inputFinality);

        if (firstFatal is not null)
        {
            ExceptionDispatchInfo.Capture(firstFatal).Throw();
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    private void AddInputFinality(WorkerInputPumpFinality? finality)
    {
        switch (finality)
        {
            case WorkerInputPumpFinality.InputFailureFinality inputFailure:
                _outcomes.Add(MapSafeFailure(inputFailure.Failure));
                break;
            case WorkerInputPumpFinality.ReaderFailureFinality:
                _outcomes.Add(WorkerProcessExitFact.InputInfrastructure());
                break;
        }
    }

    private static WorkerProcessExitFact MapProcessingResult(ProcessingRunResult result)
    {
        return result.Outcome switch
        {
            ProcessingRunOutcome.Completed => WorkerProcessExitFact.Completed(),
            ProcessingRunOutcome.Cancelled => WorkerProcessExitFact.ShutdownCancelled(),
            ProcessingRunOutcome.Failed => WorkerProcessExitFact.ExecutionFailure(),
            _ => WorkerProcessExitFact.StartupInfrastructure()
        };
    }

    private static WorkerProcessExitFact MapSafeFailure(WorkerSafeFailure failure)
    {
        return failure.Kind switch
        {
            WorkerSafeFailureKind.InputProtocol => WorkerProcessExitFact.InputInvalid(),
            WorkerSafeFailureKind.Reader => WorkerProcessExitFact.InputInfrastructure(),
            _ => WorkerProcessExitFact.TransportInfrastructure()
        };
    }

    private static WorkerProcessExitFact MapPreRequestException(PreRequestPhase phase)
    {
        return phase switch
        {
            PreRequestPhase.Startup => WorkerProcessExitFact.StartupInfrastructure(),
            PreRequestPhase.Readiness => WorkerProcessExitFact.TransportInfrastructure(),
            PreRequestPhase.Acquisition => WorkerProcessExitFact.InputInfrastructure(),
            _ => WorkerProcessExitFact.StartupInfrastructure()
        };
    }

    private static async Task<AcceptedRunFinalityOutcome> CompleteAcceptedFinalityAsync(
        IWorkerAcceptedRunFinality finality,
        ProcessingRunRequest request,
        ProcessingRunResult result)
    {
        try
        {
            await finality.CompleteAsync(request, result, CancellationToken.None);
            return AcceptedRunFinalityOutcome.Completed;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            return AcceptedRunFinalityOutcome.InfrastructureFailure;
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

    private enum AcceptedRunFinalityOutcome
    {
        Completed,
        InfrastructureFailure
    }

    private enum PreRequestPhase
    {
        Startup,
        Readiness,
        Acquisition
    }
}
