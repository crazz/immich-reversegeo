using ImmichReverseGeo.Web.ProcessingRunLocking;
using ImmichReverseGeo.Web.WorkerHost;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.CrossProcessRunLockTestAppHost;

internal sealed record SignalRunOnceFixtureOptions(string CleanupMarkerPath)
{
    internal static bool TryParse(string[] arguments, out SignalRunOnceFixtureOptions? options)
    {
        options = null;
        if (arguments is not ["--run-once-signal", var suppliedMarker]
            || !Path.IsPathFullyQualified(suppliedMarker))
        {
            return false;
        }

        var markerPath = Path.GetFullPath(suppliedMarker);
        var parent = Path.GetDirectoryName(markerPath);
        if (parent is null
            || !Directory.Exists(parent)
            || !Guid.TryParseExact(Path.GetFileName(parent), "D", out _)
            || File.Exists(markerPath))
        {
            return false;
        }

        options = new SignalRunOnceFixtureOptions(markerPath);
        return true;
    }
}

internal sealed class SignalRunOnceStartupInitializer : IWorkerStartupInitializer
{
    public Task InitialiseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class SignalRunOnceProcessingRunLock : IProcessingRunLock
{
    public async ValueTask<ProcessingRunLockAcquisition> AcquireAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The signal fixture lock gate completed without cancellation.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ProcessingRunLockAcquisition.Cancelled();
        }
    }
}

internal sealed class SignalRunOnceCleanupMarker(SignalRunOnceFixtureOptions options) : IHostedService, IDisposable
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        File.WriteAllText(options.CleanupMarkerPath, "cleanup-complete");
    }
}
