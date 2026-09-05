using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using WorkerStateBridge = ImmichReverseGeo.Web.WorkerEventStateBridge.WorkerEventStateBridge;

namespace ImmichReverseGeo.Web.WorkerFailureRecovery;

internal sealed class WorkerRunFinalizer
{
    private readonly ProcessingRunRequest _request;
    private readonly ProcessingStateEventReporter _reporter;
    private readonly TimeProvider _clock;
    private readonly DateTimeOffset _admittedAtUtc;
    private readonly ChildWorkerEvidenceFinalityGate? _evidenceGate;
    private readonly TaskCompletionSource<ProcessingRunResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stateFinality = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;

    internal WorkerRunFinalizer(ProcessingRunRequest request, ProcessingStateEventReporter reporter, TimeProvider clock,
        ChildWorkerEvidenceFinalityGate? evidenceGate = null)
    {
        _request = request;
        _reporter = reporter;
        _clock = clock;
        _evidenceGate = evidenceGate;
        _admittedAtUtc = clock.GetUtcNow();
    }

    internal WorkerRunFinalityState State { get; } = new();
    internal Task<ProcessingRunResult> Completion => _completion.Task;
    internal Task StateFinality => _stateFinality.Task;
    internal WorkerRunEvidence? Evidence { get; private set; }
    internal WorkerRunDecision? Decision { get; private set; }
    internal ProcessingRunRequest Request => _request;
    internal ProcessingStateEventReporter Reporter => _reporter;
    internal TimeProvider Clock => _clock;

    internal ProcessingRunResult FinalizeNoProcess(WorkerRunFailureCategory failure)
    {
        ClaimStart();
        var evidence = new WorkerRunEvidence
        {
            Request = _request,
            LastPhase = State.Snapshot.Transport,
            NoProcessFailure = failure,
            Receipt = _reporter.GetFinalizationReceipt(_request)
        };
        State.AdvanceTransport(WorkerRunTransportPhase.EvidenceFinal);
        Evidence = evidence;
        Decision = WorkerRunEvidenceClassifier.Classify(evidence);
        var receipt = Commit(Decision);
        _evidenceGate?.Release();
        _completion.TrySetResult(receipt.Result);
        return receipt.Result;
    }

    internal Task<ProcessingRunResult> Start(ChildWorkerSession session, WorkerStateBridge bridge,
        Action<ChildWorkerTerminalPreventingObservation> requestContainment, Func<bool> shutdownRequested)
    {
        ClaimStart();
        _ = CompleteOwnedAsync(session, bridge, requestContainment, shutdownRequested);
        return Completion;
    }

    private void ClaimStart()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("This exact run already owns a finalization operation.");
        }
    }

    private async Task CompleteOwnedAsync(ChildWorkerSession session, WorkerStateBridge bridge,
        Action<ChildWorkerTerminalPreventingObservation> requestContainment, Func<bool> shutdownRequested)
    {
        try
        {
            var transport = ObserveTransportAsync(session);
            var monitor = MonitorFaultAsync(session, requestContainment);
            var raw = await session.EvidenceFinality.ConfigureAwait(false);
            await transport.ConfigureAwait(false);
            await monitor.ConfigureAwait(false);
            var lastPhase = State.Snapshot.Transport;
            State.AdvanceTransport(WorkerRunTransportPhase.EvidenceFinal);
            var evidence = new WorkerRunEvidence
            {
                Request = _request,
                LastPhase = lastPhase,
                Completion = raw,
                Receipt = _reporter.GetFinalizationReceipt(_request),
                BridgeObservation = bridge.FirstObservation,
                Cancellation = session.CancellationFacts,
                ShutdownRequested = shutdownRequested()
            };
            var decision = WorkerRunEvidenceClassifier.Classify(evidence);
            var receipt = Commit(decision);
            // The receipt proves a final UI winner even if its observer response was indeterminate.
            _evidenceGate?.Release();

            var cleanupFailed = false;
            async Task AttemptAsync(Func<Task> operation)
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch
                {
                    cleanupFailed = true;
                }
            }

            await AttemptAsync(async () => await session.Settlement.ConfigureAwait(false)).ConfigureAwait(false);
            await AttemptAsync(async () => await session.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
            await AttemptAsync(async () => await bridge.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);

            Evidence = evidence with
            {
                Receipt = receipt,
                Cancellation = session.CancellationFacts,
                CleanupFailed = cleanupFailed,
                ShutdownRequested = shutdownRequested()
            };
            var finalAnomalies = WorkerRunEvidenceClassifier.Classify(Evidence).Anomalies | decision.Anomalies;
            Decision = decision with { Outcome = receipt.Result.Outcome, TerminalResult = receipt.Result, Anomalies = finalAnomalies };
            if (finalAnomalies != WorkerRunAnomaly.None)
            {
                try
                {
                    _reporter.TryAppendPostTerminalDiagnostic(receipt, WorkerRunDiagnostics.DescribeAnomalies(finalAnomalies));
                }
                catch
                {
                    // The diagnostic receipt is already claimed; a UI subscriber cannot trigger a replay.
                }
            }
            _completion.TrySetResult(receipt.Result);
        }
        catch (Exception failure)
        {
            // Do not release an evidence hold or coordinator ownership without a recorded UI winner.
            _completion.TrySetException(failure);
        }
    }

private async Task ObserveTransportAsync(ChildWorkerSession session)
    {
        await Task.WhenAny(session.ExecuteRequestAccepted, session.FirstTerminationRequest,
            session.PhysicalExitConfirmed, session.EvidenceFinality).ConfigureAwait(false);
        if (session.ExecuteRequestAccepted.IsCompletedSuccessfully)
        {
            State.AdvanceTransport(WorkerRunTransportPhase.Accepted);
        }

        await Task.WhenAny(session.FirstTerminationRequest, session.PhysicalExitConfirmed,
            session.EvidenceFinality).ConfigureAwait(false);
        // Check both authorities after the wait: simultaneous finality must not hide acceptance.
        if (session.ExecuteRequestAccepted.IsCompletedSuccessfully)
        {
            State.AdvanceTransport(WorkerRunTransportPhase.Accepted);
        }
        if (session.FirstTerminationRequest.IsCompletedSuccessfully || session.PhysicalExitConfirmed.IsCompletedSuccessfully)
        {
            State.AdvanceTransport(WorkerRunTransportPhase.Draining);
        }
    }

    private async Task MonitorFaultAsync(ChildWorkerSession session, Action<ChildWorkerTerminalPreventingObservation> requestContainment)
    {
        await Task.WhenAny(session.FirstTerminalPreventingObservation, session.EvidenceFinality).ConfigureAwait(false);
        if (session.PhysicalExitConfirmed.IsCompleted || session.EvidenceFinality.IsCompleted
            || !session.FirstTerminalPreventingObservation.IsCompletedSuccessfully)
        {
            return;
        }
        var observation = await session.FirstTerminalPreventingObservation.ConfigureAwait(false);
        if (_reporter.GetFinalizationReceipt(_request) is null)
        {
            State.AdvanceTransport(WorkerRunTransportPhase.Draining);
            requestContainment(observation);
        }
    }

    private ProcessingRunFinalizationReceipt Commit(WorkerRunDecision decision)
    {
        var existing = _reporter.GetFinalizationReceipt(_request);
        if (existing is not null)
        {
            State.AdvanceCommit(WorkerRunCommitPhase.Committed);
            _stateFinality.TrySetResult();
            return existing;
        }

        var endedAtUtc = _admittedAtUtc;
        if (decision.TerminalResult is null)
        {
            try
            {
                endedAtUtc = _clock.GetUtcNow();
            }
            catch
            {
                // The admission timestamp remains a safe finalization boundary when
                // the wall-clock provider fails after ownership has been claimed.
            }
        }

        var candidate = decision.TerminalResult ?? _reporter.CreateAbnormalResult(_request, decision.Outcome,
            _admittedAtUtc, endedAtUtc, decision.Outcome == ProcessingRunOutcome.Failed ? WorkerRunDiagnostics.Describe(decision.Category, decision.Phase) : null);
        try
        {
            _reporter.TryFinalize(_request, candidate, decision.Authority == WorkerRunAuthority.ValidatedTerminal
                ? ProcessingRunFinalizationOrigin.WorkerTerminal : ProcessingRunFinalizationOrigin.ControlPlane);
        }
        catch when (_reporter.GetFinalizationReceipt(_request) is not null)
        {
            // Claim-before-mutation makes the receipt authoritative even when an observer throws.
        }
        var receipt = _reporter.GetFinalizationReceipt(_request)
            ?? throw new InvalidOperationException("The exact run could not record final UI state.");
        State.AdvanceCommit(WorkerRunCommitPhase.Committed);
        _stateFinality.TrySetResult();
        return receipt;
    }
}
