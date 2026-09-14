namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

internal static class MatrixFileSignal
{
    internal static async Task WaitAsync(string root, string name, string phase)
    {
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(root, name);
        watcher.Created += (_, _) => created.TrySetResult();
        watcher.Renamed += (_, _) => created.TrySetResult();
        watcher.Error += (_, e) => created.TrySetException(e.GetException());
        watcher.EnableRaisingEvents = true;
        if (File.Exists(Path.Combine(root, name)))
        {
            created.TrySetResult();
        }

        await MatrixWait.ForAsync(created.Task, phase);
    }
}
