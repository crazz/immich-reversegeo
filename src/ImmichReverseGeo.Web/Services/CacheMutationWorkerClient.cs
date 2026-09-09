using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using ImmichReverseGeo.Web.WorkerFailureRecovery;

namespace ImmichReverseGeo.Web.Services;

internal abstract record CacheMutationWorkerStartResult
{
    private CacheMutationWorkerStartResult()
    {
    }

    internal sealed record Started(
        ICacheMutationWorkerSession Session,
        int? ChildProcessId = null) : CacheMutationWorkerStartResult;

    internal sealed record Unavailable(string Code, string Message) :
        CacheMutationWorkerStartResult;
}

internal abstract record CacheMutationWorkerOutcome
{
    private CacheMutationWorkerOutcome()
    {
    }

    internal sealed record Completed(CacheMutationResult Result) : CacheMutationWorkerOutcome;
    internal sealed record Cancelled : CacheMutationWorkerOutcome;
    internal sealed record Unavailable(string Code, string Message) : CacheMutationWorkerOutcome;
    internal sealed record Failed(string Code, string Message) : CacheMutationWorkerOutcome;
}

internal interface ICacheMutationWorkerSession : IAsyncDisposable
{
    Guid JobId { get; }
    WorkerJobKind JobKind { get; }
    InternalWorkerProtocolVersion ProtocolVersion { get; }
    bool IsCancellable { get; }
    Task<CacheMutationWorkerOutcome> Completion { get; }
    Task RequestStopAsync();
}

internal interface ICacheMutationWorkerClient
{
    ValueTask<CacheMutationWorkerStartResult> StartAsync(
        IWorkerJobAdmissionLease admission,
        CacheMutationRequest request,
        IWorkerJobEventSink eventSink,
        CancellationToken cancellationToken);
}

internal sealed class CacheMutationWorkerClient(
    IWorkerCommandInvocationBuilder commandBuilder,
    IChildWorkerLauncher launcher,
    TimeProvider timeProvider) : ICacheMutationWorkerClient
{
    public async ValueTask<CacheMutationWorkerStartResult> StartAsync(
        IWorkerJobAdmissionLease admission,
        CacheMutationRequest request,
        IWorkerJobEventSink eventSink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);
        if (admission.Context.JobKind != WorkerJobKind.CacheMutation
            || !ReferenceEquals(admission.Descriptor, WorkerJobDescriptors.CacheMutation))
        {
            return Unavailable("cache-admission-mismatch");
        }

        WorkerCommandInvocationResolution resolution;
        try
        {
            resolution = commandBuilder.Build(InternalWorkerProtocolVersion.V2);
        }
        catch
        {
            return Unavailable("cache-worker-command");
        }

        if (resolution is not WorkerCommandInvocationResolution.Success resolved)
        {
            return Unavailable("cache-worker-command");
        }

        ChildWorkerLaunchResult launch;
        try
        {
            launch = await launcher.LaunchAsync(
                resolved.Invocation,
                new CacheMutationWorkerJobDispatch(admission.Context.JobId, request),
                eventSink,
                new ChildWorkerLauncherOptions { TimeProvider = timeProvider },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Unavailable("cache-worker-cancelled-before-start");
        }
        catch
        {
            return Unavailable("cache-worker-start");
        }

        if (launch is not ChildWorkerLaunchResult.Started started)
        {
            return Unavailable("cache-worker-start");
        }

        return new CacheMutationWorkerStartResult.Started(
            new Session(started.Session, admission.Context),
            started.Session.ProcessId);
    }

    private static CacheMutationWorkerStartResult.Unavailable Unavailable(string code) =>
        new(
            code,
            "The isolated cache worker could not be started. Check the application logs and try again.");

    private sealed class Session : ICacheMutationWorkerSession
    {
        private readonly ChildWorkerSession _session;
        private readonly WorkerJobContext _context;

        internal Session(ChildWorkerSession session, WorkerJobContext context)
        {
            _session = session;
            _context = context;
            Completion = CompleteAsync();
        }

        public Guid JobId => _session.JobId;
        public WorkerJobKind JobKind => _session.JobKind;
        public InternalWorkerProtocolVersion ProtocolVersion => _session.ProtocolVersion;
        public bool IsCancellable => _session.IsCancellable;
        public Task<CacheMutationWorkerOutcome> Completion { get; }

        public Task RequestStopAsync() => _session.RequestStop();

        public ValueTask DisposeAsync() => _session.DisposeAsync();

        private async Task<CacheMutationWorkerOutcome> CompleteAsync()
        {
            Task containment = MonitorFaultContainmentAsync();
            ChildWorkerCompletionObservation completion =
                await _session.EvidenceFinality.ConfigureAwait(false);
            CacheMutationWorkerOutcome outcome = completion.JobTerminal?.Payload is
                WorkerJobTerminalPayload terminal
                ? FromTerminal(terminal)
                : FromNoTerminal(completion);

            await _session.Settlement.ConfigureAwait(false);
            await containment.ConfigureAwait(false);
            return outcome;
        }

        private async Task MonitorFaultContainmentAsync()
        {
            Task winner = await Task.WhenAny(
                _session.FirstTerminalPreventingObservation,
                _session.PhysicalExitConfirmed,
                _session.EvidenceFinality).ConfigureAwait(false);
            if (!ReferenceEquals(winner, _session.FirstTerminalPreventingObservation)
                || !_session.FirstTerminalPreventingObservation.IsCompletedSuccessfully
                || _session.PhysicalExitConfirmed.IsCompleted
                || _session.EvidenceFinality.IsCompleted)
            {
                return;
            }

            ChildWorkerTerminalPreventingObservation observation =
                await _session.FirstTerminalPreventingObservation.ConfigureAwait(false);
            if (observation.Reason is ChildWorkerFaultContainmentReason.TerminalInputCloseFailed)
            {
                return;
            }

            await _session.RequestTermination(new ChildWorkerTerminationRequest(
                observation.ObservedAt,
                ChildWorkerTerminationIntent.FaultContainment,
                observation.Reason)).ConfigureAwait(false);
        }

        private CacheMutationWorkerOutcome FromNoTerminal(
            ChildWorkerCompletionObservation completion)
        {
            WorkerJobNoTerminalDecision decision =
                WorkerJobNoTerminalEvidenceClassifier.Classify(new WorkerJobNoTerminalEvidence
                {
                    Context = _context,
                    IntendedProtocolVersion = InternalWorkerProtocolVersion.V2,
                    LastPhase = WorkerRunTransportPhase.EvidenceFinal,
                    Completion = completion,
                    Cancellation = _session.CancellationFacts
                });
            return decision.Outcome == WorkerJobNoTerminalOutcome.Cancelled
                ? new CacheMutationWorkerOutcome.Cancelled()
                : new CacheMutationWorkerOutcome.Failed(
                    $"cache-{decision.Category.ToString().ToLowerInvariant()}",
                    "The cache worker stopped before it produced a valid final result.");
        }

        private static CacheMutationWorkerOutcome FromTerminal(
            WorkerJobTerminalPayload terminal) =>
            terminal.Outcome switch
            {
                WorkerJobTerminalOutcome.Completed =>
                    new CacheMutationWorkerOutcome.Completed(
                        terminal.CacheMutationResult
                        ?? throw new InvalidOperationException(
                            "A completed cache terminal requires its typed result.")),
                WorkerJobTerminalOutcome.Cancelled => new CacheMutationWorkerOutcome.Cancelled(),
                WorkerJobTerminalOutcome.Failed => new CacheMutationWorkerOutcome.Failed(
                    terminal.Error?.Code ?? "cache-worker-failed",
                    terminal.Error?.Message ?? "The cache worker reported a safe failure."),
                _ => new CacheMutationWorkerOutcome.Failed(
                    "cache-worker-terminal",
                    "The cache worker returned an unsupported final state.")
            };
    }
}
