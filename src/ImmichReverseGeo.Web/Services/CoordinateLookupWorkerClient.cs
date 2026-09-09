using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using ImmichReverseGeo.Web.WorkerFailureRecovery;

namespace ImmichReverseGeo.Web.Services;

internal abstract record CoordinateLookupWorkerStartResult
{
    private CoordinateLookupWorkerStartResult()
    {
    }

    internal sealed record Started(
        ICoordinateLookupWorkerSession Session,
        int? ChildProcessId = null) :
        CoordinateLookupWorkerStartResult;
    internal sealed record Unavailable(string Code, string Message) :
        CoordinateLookupWorkerStartResult;
}

internal abstract record CoordinateLookupWorkerOutcome
{
    private CoordinateLookupWorkerOutcome()
    {
    }

    internal sealed record Completed(CoordinateLookupResult Result) : CoordinateLookupWorkerOutcome;
    internal sealed record Cancelled : CoordinateLookupWorkerOutcome;
    internal sealed record Failed(string Code, string Message) : CoordinateLookupWorkerOutcome;
}

internal interface ICoordinateLookupWorkerSession : IAsyncDisposable
{
    Guid JobId { get; }
    WorkerJobKind JobKind { get; }
    InternalWorkerProtocolVersion ProtocolVersion { get; }
    bool IsCancellable { get; }
    Task<CoordinateLookupWorkerOutcome> Completion { get; }
    Task RequestStopAsync();
}

internal interface ICoordinateLookupWorkerClient
{
    ValueTask<CoordinateLookupWorkerStartResult> StartAsync(
        IWorkerJobAdmissionLease admission,
        CoordinateLookupRequest request,
        IWorkerJobEventSink eventSink,
        CancellationToken cancellationToken);
}

internal sealed class CoordinateLookupWorkerClient(
    IWorkerCommandInvocationBuilder commandBuilder,
    IChildWorkerLauncher launcher,
    TimeProvider timeProvider) : ICoordinateLookupWorkerClient
{
    public async ValueTask<CoordinateLookupWorkerStartResult> StartAsync(
        IWorkerJobAdmissionLease admission,
        CoordinateLookupRequest request,
        IWorkerJobEventSink eventSink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);
        if (admission.Context.JobKind != WorkerJobKind.CoordinateLookup
            || !ReferenceEquals(admission.Descriptor, WorkerJobDescriptors.CoordinateLookup))
        {
            return Unavailable("lookup-admission-mismatch");
        }

        WorkerCommandInvocationResolution resolution;
        try
        {
            resolution = commandBuilder.Build(InternalWorkerProtocolVersion.V2);
        }
        catch
        {
            return Unavailable("lookup-worker-command");
        }

        if (resolution is not WorkerCommandInvocationResolution.Success resolved)
        {
            return Unavailable("lookup-worker-command");
        }

        ChildWorkerLaunchResult launch;
        try
        {
            launch = await launcher.LaunchAsync(
                resolved.Invocation,
                new CoordinateLookupWorkerJobDispatch(admission.Context.JobId, request),
                eventSink,
                new ChildWorkerLauncherOptions { TimeProvider = timeProvider },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Unavailable("lookup-worker-cancelled-before-start");
        }
        catch
        {
            return Unavailable("lookup-worker-start");
        }

        if (launch is not ChildWorkerLaunchResult.Started started)
        {
            return Unavailable("lookup-worker-start");
        }

        return new CoordinateLookupWorkerStartResult.Started(
            new Session(started.Session, admission.Context),
            started.Session.ProcessId);
    }

    private static CoordinateLookupWorkerStartResult.Unavailable Unavailable(string code)
    {
        return new CoordinateLookupWorkerStartResult.Unavailable(
            code,
            "The isolated lookup worker could not be started. Check the application logs and try again.");
    }

    private sealed class Session : ICoordinateLookupWorkerSession
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
        public Task<CoordinateLookupWorkerOutcome> Completion { get; }

        public async Task RequestStopAsync()
        {
            await _session.RequestStop().ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => _session.DisposeAsync();

        private async Task<CoordinateLookupWorkerOutcome> CompleteAsync()
        {
            Task containment = MonitorFaultContainmentAsync();
            ChildWorkerCompletionObservation completion =
                await _session.EvidenceFinality.ConfigureAwait(false);
            CoordinateLookupWorkerOutcome outcome = completion.JobTerminal?.Payload is
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
            if (!ReferenceEquals(
                    winner,
                    _session.FirstTerminalPreventingObservation)
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

        private CoordinateLookupWorkerOutcome FromNoTerminal(
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
                ? new CoordinateLookupWorkerOutcome.Cancelled()
                : new CoordinateLookupWorkerOutcome.Failed(
                    FailureCode(decision.Category),
                    FailureMessage(decision.Category));
        }

        private static CoordinateLookupWorkerOutcome FromTerminal(
            WorkerJobTerminalPayload terminal)
        {
            return terminal.Outcome switch
            {
                WorkerJobTerminalOutcome.Completed =>
                    new CoordinateLookupWorkerOutcome.Completed(
                        terminal.CoordinateLookupResult
                        ?? throw new InvalidOperationException(
                            "A completed coordinate lookup terminal requires its typed result.")),
                WorkerJobTerminalOutcome.Cancelled => new CoordinateLookupWorkerOutcome.Cancelled(),
                WorkerJobTerminalOutcome.Failed => new CoordinateLookupWorkerOutcome.Failed(
                    terminal.Error?.Code ?? "lookup-worker-failed",
                    terminal.Error?.Message ?? "The lookup worker reported a safe failure."),
                _ => new CoordinateLookupWorkerOutcome.Failed(
                    "lookup-worker-terminal",
                    "The lookup worker returned an unsupported final state.")
            };
        }

        private static string FailureCode(WorkerRunFailureCategory category)
        {
            return $"lookup-{category.ToString().ToLowerInvariant()}";
        }

        private static string FailureMessage(WorkerRunFailureCategory category)
        {
            return category switch
            {
                WorkerRunFailureCategory.Correlation =>
                    "The lookup worker returned output for a different job.",
                WorkerRunFailureCategory.ReadyTimeout or
                WorkerRunFailureCategory.PreReadyEndOfStream or
                WorkerRunFailureCategory.StartupCrash =>
                    "The lookup worker did not start successfully.",
                WorkerRunFailureCategory.MissingTerminal =>
                    "The lookup worker stopped without a final result.",
                WorkerRunFailureCategory.OutputTransport =>
                    "The lookup worker output could not be read safely.",
                _ => "The lookup worker stopped before it produced a valid final result."
            };
        }
    }
}
