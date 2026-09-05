using System;

namespace ImmichReverseGeo.Web.ChildWorkerLaunching;

internal static class ChildWorkerCancellationPolicy
{
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);
}

internal sealed class ChildWorkerStopRequest
{
    private ChildWorkerStopRequest(
        TimeProvider clock,
        long firstStopTimestamp,
        DateTimeOffset firstStopAtUtc,
        bool utcObservationFailed)
    {
        Clock = clock;
        FirstStopTimestamp = firstStopTimestamp;
        FirstStopAtUtc = firstStopAtUtc;
        UtcObservationFailed = utcObservationFailed;
    }

    internal TimeProvider Clock { get; }
    internal DateTimeOffset FirstStopAtUtc { get; }
    internal long FirstStopTimestamp { get; }
    internal bool UtcObservationFailed { get; }

    internal static ChildWorkerStopRequest Capture(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new ChildWorkerStopRequest(
            clock,
            clock.GetTimestamp(),
            clock.GetUtcNow().ToUniversalTime(),
            false);
    }

    internal static ChildWorkerStopRequest CaptureFaultObservation(
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        long timestamp = clock.GetTimestamp();
        DateTimeOffset observedAtUtc;
        var utcObservationFailed = false;
        try
        {
            observedAtUtc = clock.GetUtcNow().ToUniversalTime();
        }
        catch
        {
            observedAtUtc = DateTimeOffset.UnixEpoch;
            utcObservationFailed = true;
        }

        return new ChildWorkerStopRequest(
            clock,
            timestamp,
            observedAtUtc,
            utcObservationFailed);
    }
}

internal enum ChildWorkerTerminationIntent
{
    Stop,
    Shutdown,
    FaultContainment
}

internal sealed record ChildWorkerTerminationRequest
{
    internal ChildWorkerTerminationRequest(
        ChildWorkerStopRequest deadline,
        ChildWorkerTerminationIntent intent,
        ChildWorkerFaultContainmentReason? reason = null)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        if (!Enum.IsDefined(intent))
        {
            throw new ArgumentOutOfRangeException(nameof(intent));
        }

        if (intent == ChildWorkerTerminationIntent.FaultContainment != (reason is not null))
        {
            throw new ArgumentException(
                "A containment reason is required only for fault containment.",
                nameof(reason));
        }

        Deadline = deadline;
        Intent = intent;
        Reason = reason;
    }

    internal ChildWorkerStopRequest Deadline { get; }
    internal ChildWorkerTerminationIntent Intent { get; }
    internal ChildWorkerFaultContainmentReason? Reason { get; }
}

internal enum ChildProcessExitState
{
    Alive,
    Exited,
    Unavailable
}

internal enum ChildProcessKillOutcome
{
    Requested,
    AlreadyExited,
    PermissionDenied,
    Unsupported,
    Failed
}

internal enum ChildWorkerCancelDeliveryPhase
{
    Pending,
    NotAccepted,
    InputClosed,
    DeadlineElapsed,
    AlreadyExited,
    SerializationFailed,
    WriteStarted,
    WriteCompleted,
    WriteFailed,
    FlushStarted,
    FlushFailed,
    Flushed
}

internal enum ChildWorkerCancellationExitRace
{
    None,
    BeforeControl,
    DuringWrite,
    DuringFlush,
    BeforeEscalation,
    DuringEscalation
}

internal sealed record ChildWorkerCancellationFacts(
    DateTimeOffset FirstStopAtUtc,
    DateTimeOffset DeadlineUtc,
    bool RequestAccepted,
    ChildWorkerCancelDeliveryPhase DeliveryPhase,
    ChildWorkerCancellationExitRace ExitRace,
    bool GraceExpired,
    bool KillAttempted,
    ChildProcessKillOutcome? KillOutcome,
    ChildWorkerTerminationIntent FirstIntent,
    ChildWorkerFaultContainmentReason? FirstContainmentReason,
    bool FirstStopUtcObservationFailed = false);

internal sealed record ChildWorkerCancellationResult(
    ChildWorkerCancellationFacts Facts,
    ChildWorkerCompletionObservation Completion);
