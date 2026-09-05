using System;

namespace ImmichReverseGeo.Web.ChildWorkerLaunching;

internal static class ChildWorkerCancellationPolicy
{
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);
}

internal sealed class ChildWorkerStopRequest
{
    private ChildWorkerStopRequest(TimeProvider clock)
    {
        Clock = clock;
        FirstStopTimestamp = clock.GetTimestamp();
        FirstStopAtUtc = clock.GetUtcNow().ToUniversalTime();
    }

    internal TimeProvider Clock { get; }
    internal DateTimeOffset FirstStopAtUtc { get; }
    internal long FirstStopTimestamp { get; }

    internal static ChildWorkerStopRequest Capture(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new ChildWorkerStopRequest(clock);
    }
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
    ChildProcessKillOutcome? KillOutcome);

internal sealed record ChildWorkerCancellationResult(
    ChildWorkerCancellationFacts Facts,
    ChildWorkerCompletionObservation Completion);
