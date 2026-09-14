using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using ImmichReverseGeo.Web.WorkerEventDelivery;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.LifecycleTelemetry;

// One observation lifetime follows the existing launch/session. It owns neither
// execution decisions nor process resources; finalizers supply the final facts.
internal sealed class WorkerJobTelemetry
{
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly long? _launchTimestamp;
    private WorkerJobLogContext _context;
    private long? _processTimestamp;
    private int _ready;
    private int _terminalAccepted;
    private int _finalized;
    private WorkerJobTerminalOutcome? _terminalOutcome;
    private readonly TaskCompletionSource<long?> _escalation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ReadModelNotificationCadence.OwnerObservation? _notificationObservation;

    internal WorkerJobTelemetry(ILogger logger, TimeProvider clock, WorkerJobContext context)
    {
        _logger = logger;
        _clock = clock;
        _context = WorkerJobLogContext.Create(context, Environment.ProcessId);
        _launchTimestamp = LifecycleElapsed.Timestamp(clock);
        LifecycleEventCatalog.LaunchStarted(logger, _context);
    }

    internal bool ReadyObserved => Volatile.Read(ref _ready) != 0;
    internal Task CancellationObservation { get; private set; } = Task.CompletedTask;
    internal Task CoalescingObservation { get; private set; } = Task.CompletedTask;

    internal void BindNotificationObservation(ReadModelNotificationCadence.OwnerObservation? observation) =>
        _notificationObservation = observation;

    // Captured at the existing kill boundary. This never calls a log provider,
    // so a slow provider cannot postpone process control or reset its deadline.
    internal void EscalationAttempted() => _escalation.TrySetResult(LifecycleElapsed.Timestamp(_clock));

    internal void ObserveCancellation(ChildWorkerTerminationRequest request, WorkerCancellationPhase phase,
        Task<ChildWorkerCancellationResult> stop, Func<long?> physicalExitTimestamp)
    {
        CancellationObservation = ObserveCancellationAsync(request, phase, stop, physicalExitTimestamp);
    }

    private async Task ObserveCancellationAsync(ChildWorkerTerminationRequest request, WorkerCancellationPhase phase,
        Task<ChildWorkerCancellationResult> stop, Func<long?> physicalExitTimestamp)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            LifecycleEventCatalog.CancellationRequested(_logger, _context, request.Intent, phase);
            await Task.WhenAny(stop, _escalation.Task).ConfigureAwait(false);
            long? escalationTimestamp = null;
            if (_escalation.Task.IsCompletedSuccessfully)
            {
                escalationTimestamp = _escalation.Task.Result;
                LifecycleEventCatalog.Escalated(_logger, _context,
                    LifecycleElapsed.Milliseconds(_clock, request.Deadline.FirstStopTimestamp, escalationTimestamp));
            }

            ChildWorkerCancellationResult result = await stop.ConfigureAwait(false);
            long? exitTimestamp = physicalExitTimestamp();
            if (result.Facts.KillAttempted)
            {
                LifecycleEventCatalog.ForcedStopCompleted(_logger, _context,
                    LifecycleElapsed.Milliseconds(_clock, escalationTimestamp, exitTimestamp),
                    result.Facts.KillOutcome ?? ChildProcessKillOutcome.Failed);
            }
            else
            {
                LifecycleEventCatalog.GraceCompleted(_logger, _context,
                    LifecycleElapsed.Milliseconds(_clock, request.Deadline.FirstStopTimestamp, exitTimestamp),
                    result.Completion.JobTerminal is not null);
            }
        }
        catch
        {
            // An unsettled/faulted control operation is not a completed stop.
            // Observation never changes its result or owns a second cleanup.
        }
    }

    internal void ProcessOwned(int processId, long? processTimestamp)
    {
        _context = _context.WithProcess(processId);
        _processTimestamp = processTimestamp;
        LifecycleEventCatalog.ProcessStarted(_logger, _context,
            LifecycleElapsed.Milliseconds(_clock, _launchTimestamp, processTimestamp));
    }

    internal void ReadyAccepted()
    {
        if (Interlocked.Exchange(ref _ready, 1) != 0)
        {
            return;
        }

        long? now = LifecycleElapsed.Timestamp(_clock);
        LifecycleEventCatalog.WorkerReady(_logger, _context,
            LifecycleElapsed.Milliseconds(_clock, _processTimestamp, now),
            LifecycleElapsed.Milliseconds(_clock, _launchTimestamp, now));
    }

    internal void ProtocolFailed(WorkerProtocolFailureCode code, WorkerProtocolLogPhase phase, long? sequence = null) =>
        LifecycleEventCatalog.ProtocolViolation(_logger, _context,
            WorkerProtocolLogDirection.WorkerOutput, phase, code, sequence);

    internal void TerminalAccepted(WorkerJobTerminalOutcome outcome, long sequence)
    {
        if (Interlocked.Exchange(ref _terminalAccepted, 1) != 0)
        {
            return;
        }

        _terminalOutcome = outcome;
        LifecycleEventCatalog.TerminalObserved(_logger, _context, outcome, sequence);
    }

    internal void Finalized(WorkerLogClassification classification, int? exitCode,
        bool forcedStop, ChildWorkingSetSummary memory, WorkerEventDeliveryObservation? delivery = null)
    {
        if (Interlocked.Exchange(ref _finalized, 1) != 0)
        {
            return;
        }

        if (delivery is not null && (delivery.EnqueueWaits > 0 || delivery.ReplacedSnapshots > 0))
        {
            CoalescingObservation = ObserveCoalescingAsync(delivery);
        }

        LifecycleEventCatalog.ProcessClassified(_logger, _context, ReadyObserved,
            _terminalOutcome, classification, exitCode, forcedStop,
            LifecycleElapsed.Milliseconds(_clock, _launchTimestamp, LifecycleElapsed.Timestamp(_clock)), memory);
    }

    internal void Finalized(ChildWorkerCompletionObservation completion, WorkerRunFailureCategory category,
        WorkerRunAnomaly anomalies, ChildWorkerCancellationFacts? cancellation, bool terminalInputCloseFailed,
        ChildWorkingSetSummary memory)
    {
        var classification = WorkerClassificationLogProjection.FromFinality(ReadyObserved, _terminalOutcome,
            category, anomalies, completion, cancellation, terminalInputCloseFailed);
        Finalized(classification, completion.ExitObserved ? completion.ExitCode : null,
            cancellation?.KillAttempted == true, memory, completion.EventDelivery);
    }

    private async Task ObserveCoalescingAsync(WorkerEventDeliveryObservation delivery)
    {
        long count = _notificationObservation is null ? 0
            : await _notificationObservation.FinalOrdinaryDispatched.ConfigureAwait(false);
        LifecycleEventCatalog.CoalescingSaturated(_logger, _context, delivery, count);
    }
}
