using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using ImmichReverseGeo.Web.WorkerEventStateBridge;
using WorkerStateBridge = ImmichReverseGeo.Web.WorkerEventStateBridge.WorkerEventStateBridge;

namespace ImmichReverseGeo.Web.WorkerFailureRecovery;

/// <summary>Explicit child execution composition. Backend selection remains with the calling executor.</summary>
internal sealed class WorkerRunControlPlane
{
    private readonly IWorkerCommandInvocationBuilder _builder;
    private readonly IChildWorkerLauncher _launcher;
    private readonly ProcessingStateEventReporter _reporter;
    private readonly TimeProvider _clock;

    internal WorkerRunControlPlane(IWorkerCommandInvocationBuilder builder, IChildWorkerLauncher launcher,
        ProcessingStateEventReporter reporter, TimeProvider clock)
    {
        _builder = builder;
        _launcher = launcher;
        _reporter = reporter;
        _clock = clock;
    }

    internal async Task<ProcessingRunResult> ExecuteAsync(ProcessingRunCoordinator coordinator, ProcessingRunRequest request)
    {
        var evidenceGate = new ChildWorkerEvidenceFinalityGate();
        var finalizer = new WorkerRunFinalizer(request, _reporter, _clock, evidenceGate);
        if (!coordinator.TryClaimChildExecution(request, finalizer))
        {
            throw new InvalidOperationException("The exact admitted request cannot claim child execution.");
        }

        var bridge = new WorkerEventStateBridgeFactory(_reporter).Create(request);
        finalizer.State.AdvanceTransport(WorkerRunTransportPhase.Resolving);
        WorkerCommandInvocationResolution resolution;
        try
        {
            resolution = _builder.Build();
        }
        catch
        {
            resolution = WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.RuntimeObservationFailure);
        }

        if (resolution is not WorkerCommandInvocationResolution.Success resolved)
        {
            var result = finalizer.FinalizeNoProcess(WorkerRunFailureCategory.CommandResolution);
            await bridge.DisposeAsync().ConfigureAwait(false);
            return result;
        }

        finalizer.State.AdvanceTransport(WorkerRunTransportPhase.Starting);
        var launch = await _launcher.LaunchAsync(resolved.Invocation, request, new TrackingSink(bridge, finalizer),
            new ChildWorkerLauncherOptions { TimeProvider = _clock, EvidenceFinalityGate = evidenceGate }, CancellationToken.None).ConfigureAwait(false);
        if (launch is not ChildWorkerLaunchResult.Started started)
        {
            var result = finalizer.FinalizeNoProcess(WorkerRunFailureCategory.ProcessStart);
            await bridge.DisposeAsync().ConfigureAwait(false);
            return result;
        }

        finalizer.State.AdvanceTransport(WorkerRunTransportPhase.PreReady);
        if (!coordinator.TryAttachChildSession(request, started.Session, bridge, finalizer))
        {
            // An unadmitted session has no right to mutate this or a replacement run's state.
            // It still owns its process and pumps until physical finality and exact-session cleanup.
            var termination = started.Session.RequestTermination(new ChildWorkerTerminationRequest(
                ChildWorkerStopRequest.Capture(_clock), ChildWorkerTerminationIntent.FaultContainment, ChildWorkerFaultContainmentReason.ReadyRejected.Instance));
            await started.Session.EvidenceFinality.ConfigureAwait(false);
            evidenceGate.Release();
            await termination.ConfigureAwait(false);
            await started.Session.DisposeAsync().ConfigureAwait(false);
            await bridge.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("The exact admitted request could not attach its worker.");
        }

        return await finalizer.Completion.ConfigureAwait(false);
    }

    private sealed class TrackingSink(WorkerStateBridge bridge, WorkerRunFinalizer finalizer) : IWorkerProtocolEventSink
    {
        public async ValueTask AcceptAsync(WorkerProtocolEvent @event, CancellationToken cancellationToken)
        {
            try
            {
                await bridge.AcceptAsync(@event, cancellationToken).ConfigureAwait(false);
                if (@event.Type == WorkerProtocolV1.ReadyType)
                {
                    finalizer.State.AdvanceTransport(WorkerRunTransportPhase.Ready);
                }
                if (WorkerProtocolV1.IsTerminal(@event.Type))
                {
                    finalizer.State.AdvanceCommit(WorkerRunCommitPhase.TerminalValidated);
                }
            }
            finally
            {
                if (bridge.Reporter.GetFinalizationReceipt(bridge.Request) is not null)
                {
                    finalizer.State.AdvanceCommit(WorkerRunCommitPhase.Committed);
                }
            }
        }
    }
}
