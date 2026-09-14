using System.Diagnostics;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Tests.CrossProcessRunExclusion;

namespace ImmichReverseGeo.Tests.RunOnceDeploymentMode;

[TestClass]
[TestCategory("Change43")]
public sealed class RunOnceProcessBoundaryTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task FixtureAppHost_SigtermAfterRealRunStartedCompletesCancelledAndCleansUp()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This test exercises the Unix SIGTERM contract.");
        }

        var root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-run-once-signal", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        var cleanupMarker = Path.Combine(root, "cleanup-complete");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = CrossProcessAppHostLocator.CrossProcessRunLockAppHostExecutable,
                WorkingDirectory = CrossProcessAppHostLocator.CrossProcessRunLockAppHostDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--run-once-signal");
        process.StartInfo.ArgumentList.Add(cleanupMarker);

        try
        {
            Assert.IsTrue(process.Start());
            Task<string?> runStarted = process.StandardOutput.ReadLineAsync();
            string? firstLine = await runStarted.WaitAsync(Bound);
            Assert.AreEqual("Run started.", firstLine);

            using var signal = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/kill",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-TERM", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            });
            Assert.IsNotNull(signal);
            await signal.WaitForExitAsync().WaitAsync(Bound);
            Assert.AreEqual(0, signal.ExitCode);

            Task<string> remainingOutputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(Bound);
            string stdout = firstLine + Environment.NewLine + await remainingOutputTask.WaitAsync(Bound);
            string stderr = await errorTask.WaitAsync(Bound);

            Assert.AreEqual(WorkerProcessExitCodes.Cancelled, process.ExitCode);
            Assert.AreEqual(1, CountOccurrences(stdout, "Run started."));
            Assert.AreEqual(1, CountOccurrences(stderr, "Run cancelled:"));
            Assert.IsTrue(File.Exists(cleanupMarker), "The real host must finish provider cleanup before process exit.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(Bound);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FixtureAppHost_AbruptKillAfterRealRunStartedKeepsPlatformStatusAndDoesNotRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-run-once-abrupt", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        var cleanupMarker = Path.Combine(root, "cleanup-complete");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = CrossProcessAppHostLocator.CrossProcessRunLockAppHostExecutable,
                WorkingDirectory = CrossProcessAppHostLocator.CrossProcessRunLockAppHostDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--run-once-signal");
        process.StartInfo.ArgumentList.Add(cleanupMarker);

        try
        {
            Assert.IsTrue(process.Start());
            string? firstLine = await process.StandardOutput.ReadLineAsync().WaitAsync(Bound);
            Assert.AreEqual("Run started.", firstLine);

            process.Kill(entireProcessTree: true);
            Task<string> remainingOutputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(Bound);
            string stdout = firstLine + Environment.NewLine + await remainingOutputTask.WaitAsync(Bound);
            string stderr = await errorTask.WaitAsync(Bound);

            Assert.IsFalse(new[]
            {
                WorkerProcessExitCodes.Completed,
                WorkerProcessExitCodes.InvalidInput,
                WorkerProcessExitCodes.Busy,
                WorkerProcessExitCodes.ExecutorFailure,
                WorkerProcessExitCodes.InfrastructureFailure,
                WorkerProcessExitCodes.Cancelled
            }.Contains(process.ExitCode), $"Abrupt process status {process.ExitCode} must remain outside managed Run-once outcomes.");
            Assert.AreEqual(1, CountOccurrences(stdout, "Run started."));
            Assert.AreEqual(0, CountOccurrences(stderr, "Run cancelled:"));
            Assert.IsFalse(File.Exists(cleanupMarker), "Forced termination must occur before managed provider finality.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(Bound);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProductionEntry_RunOnceRequiredStorageFailureIsSafeInfrastructureAndNeverStartsWeb()
    {
        var root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-run-once-red", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        var invalidDataDirectory = Path.Combine(root, "data-is-a-file");
        await File.WriteAllTextAsync(invalidDataDirectory, "not a directory");
        var secretCanary = "run-once-secret-canary";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = CrossProcessAppHostLocator.ProductionWorkerAppHostExecutable,
                WorkingDirectory = CrossProcessAppHostLocator.ProductionWorkerAppHostDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.Environment["IMMICH_REVERSEGEO_MODE"] = "run-once";
        process.StartInfo.Environment["CONFIG_DIR"] = Path.Combine(root, "config");
        process.StartInfo.Environment["DATA_DIR"] = invalidDataDirectory;
        process.StartInfo.Environment["DB_PASSWORD"] = secretCanary;
        process.StartInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:not-a-port";

        try
        {
            Assert.IsTrue(process.Start());
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(Bound);
            string stdout = await stdoutTask.WaitAsync(Bound);
            string stderr = await stderrTask.WaitAsync(Bound);

            Assert.AreEqual(WorkerProcessExitCodes.InfrastructureFailure, process.ExitCode);
            Assert.AreEqual(string.Empty, stdout);
            StringAssert.Contains(stderr, "infrastructure-failure");
            Assert.IsFalse(stderr.Contains(secretCanary, StringComparison.Ordinal));
            Assert.IsFalse(stderr.Contains(invalidDataDirectory, StringComparison.Ordinal));
            Assert.IsFalse(stderr.Contains("connection string", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(stderr.Contains("stack", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(stderr.Contains("Now listening", StringComparison.Ordinal));
            Assert.IsFalse(stderr.Contains("ready", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(stderr.TrimStart().StartsWith('{'));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(Bound);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    private static int CountOccurrences(string value, string expected)
    {
        return value.Split(expected, StringSplitOptions.None).Length - 1;
    }
}
