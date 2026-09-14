using System.Diagnostics;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Tests.CrossProcessRunExclusion;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.WorkerProcessFixture;

internal enum FixtureApplicationEntry
{
    InvalidDeployment, InvalidProtocol, InvalidPrivateArguments, WorkerV1, WorkerV2, BlockedConfiguration
}

internal sealed partial class WorkerProcessFixtureLease
{
    internal async Task<RawApplicationFixture> StartApplicationAsync(FixtureApplicationEntry entry, string? invalidValue = null)
    {
        string data = Path.Combine(Root, "data");
        string config = Path.Combine(Root, "config");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);
        if (entry == FixtureApplicationEntry.BlockedConfiguration)
        {
            await File.WriteAllTextAsync(Path.Combine(Root, "appsettings.json"), "{]owned-unusable-configuration");
        }
        IReadOnlyList<string> arguments = entry switch
        {
            FixtureApplicationEntry.InvalidDeployment => [],
            FixtureApplicationEntry.InvalidPrivateArguments => invalidValue switch
            {
                "duplicate" => ["--internal-worker", "--internal-worker"],
                "residual" => ["--internal-worker", "--matrix-secret-password-payload"],
                "assigned" => ["--internal-worker=matrix-secret-password-payload"],
                _ => throw new ArgumentOutOfRangeException(nameof(invalidValue))
            },
            _ => ["--internal-worker"]
        };
        var descriptor = new ChildProcessStartDescriptor(CrossProcessAppHostLocator.ProductionWorkerAppHostExecutable,
            arguments, entry == FixtureApplicationEntry.BlockedConfiguration ? Root : CrossProcessAppHostLocator.ProductionWorkerAppHostDirectory,
            ChildProcessEnvironmentPolicy.InheritCurrent);
        Assert.IsTrue(File.Exists(descriptor.ExecutablePath));
        var start = SystemChildProcessFactory.CreateStartInfo(descriptor);
        foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("DB_", StringComparison.Ordinal)
            || key.StartsWith("PG", StringComparison.Ordinal)).ToArray())
        {
            start.Environment.Remove(key);
        }
        start.Environment["DATA_DIR"] = data;
        start.Environment["CONFIG_DIR"] = config;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment.Remove(DeploymentModeResolver.EnvironmentVariableName);
        start.Environment.Remove(InternalWorkerProtocolVersionSelector.EnvironmentVariableName);
        if (entry == FixtureApplicationEntry.InvalidDeployment)
        {
            start.Environment[DeploymentModeResolver.EnvironmentVariableName] = invalidValue;
        }
        else if (entry == FixtureApplicationEntry.InvalidProtocol)
        {
            start.Environment[InternalWorkerProtocolVersionSelector.EnvironmentVariableName] = invalidValue;
        }
        else if (entry is FixtureApplicationEntry.WorkerV2 or FixtureApplicationEntry.BlockedConfiguration)
        {
            start.Environment[InternalWorkerProtocolVersionSelector.EnvironmentVariableName] = "2";
        }

        // The lease/root registry exists before Start. Retain this exact OS object
        // even if adapter/capture setup fails; never rediscover a process by PID.
        var native = new Process { StartInfo = start, EnableRaisingEvents = true };
        bool started = false;
        try
        {
            started = native.Start();
            Assert.IsTrue(started, "The staged application apphost must start.");
            ProcessId = native.Id;
            _borrowedProcess = native;
            var adapter = new SystemChildProcessFactory.SystemChildProcess(native);
            var registered = new RegisteredProcess(adapter, this);
            _process = registered;
            _exitTask = registered.ExitTask;
            var capture = new RawApplicationFixture(registered);
            _directDrains.Add(capture.StandardOutput);
            _directDrains.Add(capture.StandardError);
            return capture;
        }
        catch
        {
            if (_process is null)
            {
                if (started)
                {
                    if (!native.HasExited)
                    {
                        native.Kill(entireProcessTree: true);
                        ForcedCleanup = true;
                    }
                    await RawApplicationFixture.WaitAsync(native.WaitForExitAsync(), "application/setup-failure-native-reap");
                }
                native.Dispose();
            }
            throw;
        }
    }
}

internal sealed class RawApplicationFixture
{
    private const int MaximumCaptureBytes = 1_048_576;
    private readonly IChildProcess _process;
    internal RawApplicationFixture(IChildProcess process)
    {
        _process = process;
        StandardOutput = CaptureAsync(process.StandardOutput, "stdout");
        StandardError = CaptureAsync(process.StandardError, "stderr");
    }

    internal Task<byte[]> StandardOutput { get; }
    internal Task<byte[]> StandardError { get; }

    internal async Task SendAndCloseAsync(byte[] frame)
    {
        await WaitAsync(_process.StandardInput.WriteAsync(frame).AsTask(), "application/raw-controller-input-write");
        await WaitAsync(_process.StandardInput.FlushAsync(), "application/raw-controller-input-flush");
        _process.StandardInput.Close();
    }

    internal async Task<int> CompleteAsync()
    {
        await WaitAsync(_process.WaitForExitAsync(), "application/native-exit");
        await WaitAsync(Task.WhenAll(StandardOutput, StandardError), "application/both-raw-drains");
        return await _process.WaitForExitAsync();
    }

    private static async Task<byte[]> CaptureAsync(Stream stream, string name)
    {
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[8192];
        bool overflow = false;
        int count;
        while ((count = await stream.ReadAsync(buffer)) > 0)
        {
            int remaining = MaximumCaptureBytes - (int)bytes.Length;
            int retain = Math.Min(count, remaining);
            bytes.Write(buffer, 0, retain);
            overflow |= count > remaining;
        }
        Assert.IsFalse(overflow, "Application raw " + name + " exceeded its 1MiB capture bound; excess bytes were drained.");
        return bytes.ToArray();
    }

    internal static async Task WaitAsync(Task task, string phase)
    {
        try
        {
            await task.WaitAsync(WorkerProcessFixtureLease.Watchdog);
        }
        catch (TimeoutException)
        {
            Assert.Fail("Application fixture watchdog expired: " + phase + ".");
        }
    }
}
