using System.Diagnostics;
using System.Globalization;

namespace ImmichReverseGeo.WorkerProcessFixture;

/// <summary>Owns the one closed descendant mode, including failure before the parent observes its PID.</summary>
internal sealed class MatrixDescendant : IAsyncDisposable
{
    private readonly Process _process = new();
    private bool _started;

    internal static async Task<MatrixDescendant> StartAsync(string root)
    {
        var owned = new MatrixDescendant();
        try
        {
            string executable = Environment.ProcessPath
                ?? throw new FixtureInputException("The staged fixture apphost is unavailable.");
            string expectedName = OperatingSystem.IsWindows()
                ? "ImmichReverseGeo.WorkerProcessFixture.exe" : "ImmichReverseGeo.WorkerProcessFixture";
            if (!string.Equals(Path.GetFileName(executable), expectedName, StringComparison.Ordinal))
            {
                throw new FixtureInputException("The descendant requires the staged fixture apphost.");
            }

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = root
            };
            start.ArgumentList.Add("--hold-cache-candidate");
            start.ArgumentList.Add(Path.Combine(root, "descendant-candidate.tmp"));
            start.ArgumentList.Add(Path.Combine(root, "descendant-ready.marker"));
            owned._process.StartInfo = start;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(root, "descendant-ready.marker");
            watcher.Created += (_, _) => ready.TrySetResult();
            watcher.Error += (_, error) => ready.TrySetException(error.GetException());
            watcher.EnableRaisingEvents = true;
            owned._started = owned._process.Start();
            if (!owned._started)
            {
                throw new FixtureInputException("The closed descendant did not start.");
            }

            Task exited = owned._process.WaitForExitAsync();
            Task observed;
            try
            {
                observed = await Task.WhenAny(ready.Task, exited).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new FixtureInputException("Matrix descendant watchdog expired: candidate-lease-readiness.");
            }
            if (observed != ready.Task)
            {
                throw new FixtureInputException("The descendant exited before acquiring its candidate lease.");
            }
            await ready.Task.ConfigureAwait(false);
            string temporary = Path.Combine(root, "descendant-pid.pending");
            await File.WriteAllTextAsync(temporary, owned._process.Id.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            File.Move(temporary, Path.Combine(root, "descendant-pid.marker"));
            return owned;
        }
        catch
        {
            await owned.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
            try
            {
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new FixtureInputException("Matrix descendant watchdog expired: unconditional-native-reap.");
            }
        }
        _process.Dispose();
    }
}
