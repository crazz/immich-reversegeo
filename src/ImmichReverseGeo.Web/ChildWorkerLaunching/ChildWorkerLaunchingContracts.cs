using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using WorkerInvocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Web.ChildWorkerLaunching;

internal interface IChildWorkerLauncher
{
    ValueTask<ChildWorkerLaunchResult> LaunchAsync(
        WorkerInvocation invocation,
        ProcessingRunRequest request,
        IWorkerProtocolEventSink eventSink,
        ChildWorkerLauncherOptions options,
        CancellationToken cancellationToken);
}

internal interface IWorkerProtocolEventSink
{
    ValueTask AcceptAsync(WorkerProtocolEvent @event, CancellationToken cancellationToken);
}

internal sealed record ChildWorkerLauncherOptions
{
    internal static ChildWorkerLauncherOptions Default { get; } = new();
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    internal TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        Validate(this);
    }

    private static void Validate(ChildWorkerLauncherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.TimeProvider, nameof(options));
        if (options.ReadyTimeout != Timeout.InfiniteTimeSpan && options.ReadyTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }
}

internal abstract class ChildWorkerLaunchResult
{
    private ChildWorkerLaunchResult()
    {
    }

    internal sealed class StartFailed(ChildWorkerStartFailureCategory category) : ChildWorkerLaunchResult
    {
        internal ChildWorkerStartFailureCategory Category { get; } = category;
    }

    internal sealed class Started(ChildWorkerSession session) : ChildWorkerLaunchResult
    {
        internal ChildWorkerSession Session { get; } = session;
    }
}

internal enum ChildWorkerStartFailureCategory
{
    ProcessStartFailed
}

internal abstract class ChildWorkerStartupObservation
{
    private ChildWorkerStartupObservation()
    {
    }

    internal sealed class Pending : ChildWorkerStartupObservation
    {
        internal static Pending Instance { get; } = new();
    }

    internal sealed class ReadyAccepted : ChildWorkerStartupObservation
    {
        internal static ReadyAccepted Instance { get; } = new();
    }

    internal sealed class ReadyTimedOut : ChildWorkerStartupObservation
    {
        internal static ReadyTimedOut Instance { get; } = new();
    }

    internal sealed class PreReadyEndOfStream : ChildWorkerStartupObservation
    {
        internal static PreReadyEndOfStream Instance { get; } = new();
    }

    internal sealed class PreReadyExit : ChildWorkerStartupObservation
    {
        internal static PreReadyExit Instance { get; } = new();
    }

    internal sealed class PreReadyExitObservationFailed : ChildWorkerStartupObservation
    {
        internal static PreReadyExitObservationFailed Instance { get; } = new();
    }

    internal sealed class PreReadyReadFailed : ChildWorkerStartupObservation
    {
        internal static PreReadyReadFailed Instance { get; } = new();
    }

    internal sealed class ProtocolFailure(WorkerProtocolFailure failure) : ChildWorkerStartupObservation
    {
        internal WorkerProtocolFailure Failure { get; } = failure;
    }

    internal sealed class SinkFailed : ChildWorkerStartupObservation
    {
        internal static SinkFailed Instance { get; } = new();
    }

    internal sealed class RequestSerializationFailed : ChildWorkerStartupObservation
    {
        internal static RequestSerializationFailed Instance { get; } = new();
    }

    internal sealed class RequestWriteFailed : ChildWorkerStartupObservation
    {
        internal static RequestWriteFailed Instance { get; } = new();
    }

    internal sealed class RequestFlushFailed : ChildWorkerStartupObservation
    {
        internal static RequestFlushFailed Instance { get; } = new();
    }

    internal sealed class Disposed : ChildWorkerStartupObservation
    {
        internal static Disposed Instance { get; } = new();
    }
}

internal abstract class ChildWorkerProtocolObservation
{
    private ChildWorkerProtocolObservation()
    {
    }

    internal sealed class ProtocolFailure(WorkerProtocolFailure failure) : ChildWorkerProtocolObservation
    {
        internal WorkerProtocolFailure Failure { get; } = failure;
    }

    internal sealed class SinkFailure : ChildWorkerProtocolObservation
    {
        internal static SinkFailure Instance { get; } = new();
    }
}

internal abstract class ChildWorkerStreamFinality
{
    private ChildWorkerStreamFinality()
    {
    }

    internal sealed class EndOfStream : ChildWorkerStreamFinality
    {
        internal static EndOfStream Instance { get; } = new();
    }

    internal sealed class ReadFailed : ChildWorkerStreamFinality
    {
        internal static ReadFailed Instance { get; } = new();
    }
}

internal sealed class ChildWorkerStandardErrorTail
{
    private readonly byte[] _bytes;

    internal ChildWorkerStandardErrorTail(ReadOnlySpan<byte> bytes, long totalBytes, bool totalBytesSaturated, bool isTruncated)
    {
        _bytes = bytes.ToArray();
        TotalBytes = totalBytes;
        TotalBytesSaturated = totalBytesSaturated;
        IsTruncated = isTruncated;
    }

    internal ReadOnlyMemory<byte> Bytes => _bytes;
    internal long TotalBytes { get; }
    internal bool TotalBytesSaturated { get; }
    internal bool IsTruncated { get; }
    internal string Text => System.Text.Encoding.UTF8.GetString(_bytes);
}

internal sealed record ChildWorkerCompletionObservation(
    int ProcessId,
    Guid RunId,
    ChildWorkerStartupObservation Startup,
    bool ExitObserved,
    int? ExitCode,
    ChildWorkerStreamFinality StandardOutputFinality,
    ChildWorkerStreamFinality StandardErrorFinality,
    WorkerProtocolEvent? Terminal,
    ChildWorkerProtocolObservation? FirstProtocolObservation,
    ChildWorkerStandardErrorTail StandardErrorTail);

internal interface IChildProcessFactory
{
    ValueTask<IChildProcess?> StartAsync(ChildProcessStartDescriptor descriptor, CancellationToken cancellationToken);
}

internal interface IChildProcess : IAsyncDisposable
{
    int ProcessId { get; }
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    Task<int> WaitForExitAsync();
}
