using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Tests.WorkerProcessFixture;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

internal enum MatrixProjectionPause { FirstPipeLog, Eligibility }

internal sealed class MatrixExecutionControl(MatrixProjectionPause pause)
{
    internal FixtureReadGate Output { get; } = new();
    internal FixtureReadGate Error { get; } = new();
    internal TaskCompletionSource ProjectionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ProjectionRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal async ValueTask BeforeProjectionAsync(WorkerJobOutputMessage message, CancellationToken cancellationToken)
    {
        bool hold = pause == MatrixProjectionPause.Eligibility
            ? message.Payload is ProcessAssetsEligibilityPayload
            : message.Payload is WorkerJobLogPayload { Message: "matrix:pipe-first" };
        if (hold)
        {
            ProjectionEntered.TrySetResult();
            await ProjectionRelease.Task.WaitAsync(cancellationToken);
        }
    }

    internal void ReleaseAll()
    {
        ProjectionRelease.TrySetResult();
        Output.Release();
        Error.Release();
    }
}
