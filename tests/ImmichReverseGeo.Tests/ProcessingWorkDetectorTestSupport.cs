using System.Collections.Concurrent;
using System.Threading.Channels;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

internal sealed class ProcessingWorkDetectorStub(
    Func<ProcessingWorkDetectionRequest, CancellationToken, Task<ProcessingWorkDetectionResult>> detect)
    : IProcessingWorkDetector
{
    private readonly ConcurrentQueue<(ProcessingWorkDetectionRequest Request, CancellationToken Token)> _calls = new();
    internal (ProcessingWorkDetectionRequest Request, CancellationToken Token)[] Calls => _calls.ToArray();

    public Task<ProcessingWorkDetectionResult> DetectAsync(
        ProcessingWorkDetectionRequest request,
        CancellationToken cancellationToken)
    {
        _calls.Enqueue((request, cancellationToken));
        return detect(request, cancellationToken);
    }

    internal static ProcessingWorkDetectionRequest Request() => new(
        ProcessingRunTrigger.Scheduled, ProcessingWorkDetectionSnapshot.Current);

    internal static ProcessingWorkDetectionResult Result(
        bool hasWork,
        ProcessingWorkDetectorKind kind = ProcessingWorkDetectorKind.CountBacked,
        bool usedFallback = false) => new(hasWork, new(
            kind, ProcessingWorkDetectionCoverage.FullEligibility, usedFallback));

    internal static ProcessingWorkDetectorStub Constant(bool hasWork) => new((_, _) => Task.FromResult(Result(hasWork)));

    internal static ProcessingWorkDetectorStub Scripted(params ProcessingWorkDetectionResult[] results)
    {
        var queue = new ConcurrentQueue<ProcessingWorkDetectionResult>(results);
        return new((_, _) => Task.FromResult(queue.TryDequeue(out ProcessingWorkDetectionResult? result)
            ? result
            : throw new AssertFailedException("Unexpected detector invocation after the script was consumed.")));
    }

    internal static ProcessingWorkDetectorStub Throwing(Exception failure) =>
        new((_, _) => Task.FromException<ProcessingWorkDetectionResult>(failure));

    internal static ProcessingWorkDetectorStub Cancelled() =>
        new((_, token) => Task.FromCanceled<ProcessingWorkDetectionResult>(token));

    internal static ProcessingWorkDetectorStub FailOnUse() =>
        new((_, _) => throw new AssertFailedException("This trigger or role must not evaluate scheduled work."));
}

internal sealed class GatedProcessingWorkDetector : IProcessingWorkDetector
{
    private readonly Channel<Invocation> _entered = Channel.CreateUnbounded<Invocation>();
    private readonly ConcurrentQueue<Invocation> _calls = new();
    internal Invocation[] Calls => _calls.ToArray();

    public Task<ProcessingWorkDetectionResult> DetectAsync(
        ProcessingWorkDetectionRequest request,
        CancellationToken cancellationToken)
    {
        var invocation = new Invocation(request, cancellationToken);
        _calls.Enqueue(invocation);
        Assert.IsTrue(_entered.Writer.TryWrite(invocation));
        return invocation.Completion.Task;
    }

    internal Task<Invocation> NextAsync() => _entered.Reader.ReadAsync().AsTask();

    internal sealed class Invocation(ProcessingWorkDetectionRequest request, CancellationToken token)
    {
        internal ProcessingWorkDetectionRequest Request { get; } = request;
        internal CancellationToken Token { get; } = token;
        internal TaskCompletionSource<ProcessingWorkDetectionResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release(ProcessingWorkDetectionResult result) => Completion.SetResult(result);
        internal void Fail(Exception failure) => Completion.SetException(failure);
        internal void Cancel() => Completion.SetCanceled(Token);
    }
}
