using System.Diagnostics;
using System.Text;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;

namespace ImmichReverseGeo.Tests.WorkerProcessFixture;

[TestClass]
[TestCategory("Change47")]
public sealed class WorkerProcessFixtureRawV2BoundaryTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("1")]
    [DataRow("02")]
    [DataRow("invalid-PRIVATE_VALUE_SENTINEL")]
    public async Task ActualProcess_InvalidPresentSelectionFailsBeforeReadyWithoutEcho(
        string selectedValue)
    {
        string root = CreateRoot();
        using var process = CreateProcess(root, selectedValue);
        try
        {
            Assert.IsTrue(process.Start(), "invalid-selection-process-started");
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(WorkerProcessFixtureLease.Watchdog);
            string stdout = await stdoutTask.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            string stderr = await stderrTask.WaitAsync(WorkerProcessFixtureLease.Watchdog);

            Assert.AreEqual(WorkerProcessExitCodes.InvalidInput, process.ExitCode);
            Assert.AreEqual(string.Empty, stdout, "invalid-selection-no-ready");
            StringAssert.StartsWith(stderr, "fixture-input:");
            Assert.IsFalse(
                stderr.Contains(
                    InternalWorkerProtocolVersionSelector.EnvironmentVariableName,
                    StringComparison.Ordinal),
                "invalid-selection-private-name-not-echoed");
            if (selectedValue.Length > 0)
            {
                Assert.IsFalse(
                    stderr.Contains(selectedValue, StringComparison.Ordinal),
                    "invalid-selection-private-value-not-echoed");
            }
        }
        finally
        {
            KillIfAlive(process);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("reserved")]
    [DataRow("malformed")]
    public async Task ActualProcess_V2RejectsRawInputAfterReadyWithoutCaptureOrTerminal(
        string row)
    {
        string root = CreateRoot();
        using var process = CreateProcess(root, "2", capture: true);
        try
        {
            Assert.IsTrue(process.Start(), "raw-v2-process-started");
            string? readyLine = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(WorkerProcessFixtureLease.Watchdog);
            Assert.IsNotNull(readyLine, "raw-v2-ready-present");
            WorkerJobProtocolParseResult ready = WorkerJobProtocolCodec.Parse(
                Encoding.UTF8.GetBytes(readyLine));
            Assert.IsTrue(ready.IsSuccess, ready.Failure?.Diagnostic);
            CollectionAssert.AreEqual(
                new[]
                {
                    WorkerJobKind.ProcessAssets,
                    WorkerJobKind.CoordinateLookup
                },
                Assert.IsInstanceOfType<WorkerJobReadyPayload>(ready.Message!.Payload)
                    .SupportedJobKinds.ToArray(),
                "raw-v2-ready-exact-registered-kinds");

            string rawInput = row switch
            {
                "reserved" =>
                    "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-09-08T13:31:00.0000000Z\",\"jobId\":\"11111111-2222-3333-4444-555555555555\",\"jobKind\":\"CacheMutation\",\"payload\":{}}\n",
                "malformed" => "{]\n",
                _ => throw new AssertFailedException("raw-v2-unknown-row")
            };
            await process.StandardInput.WriteAsync(rawInput);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();

            Task<string> remainingStdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(WorkerProcessFixtureLease.Watchdog);
            string remainingStdout = await remainingStdoutTask.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            string stderr = await stderrTask.WaitAsync(WorkerProcessFixtureLease.Watchdog);

            Assert.AreEqual(WorkerProcessExitCodes.InvalidInput, process.ExitCode, stderr);
            Assert.AreEqual(string.Empty, remainingStdout, "raw-v2-ready-only-no-terminal");
            StringAssert.StartsWith(stderr, "fixture-input:");
            Assert.AreEqual(0, Directory.GetFiles(root).Length, "raw-v2-rejected-input-not-captured");
        }
        finally
        {
            KillIfAlive(process);
            Directory.Delete(root, recursive: true);
        }
    }

    private static Process CreateProcess(
        string root,
        string selectedValue,
        bool capture = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerProcessFixtureLease.FixtureExecutable,
            WorkingDirectory = WorkerProcessFixtureLease.FixtureDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--scenario");
        startInfo.ArgumentList.Add("ready");
        startInfo.ArgumentList.Add("--resource-root");
        startInfo.ArgumentList.Add(root);
        if (capture)
        {
            startInfo.ArgumentList.Add("--capture-name");
            startInfo.ArgumentList.Add("request.ndjson");
        }

        startInfo.Environment[InternalWorkerProtocolVersionSelector.EnvironmentVariableName] =
            selectedValue;
        return new Process { StartInfo = startInfo };
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "immich-reversegeo-worker-fixture",
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void KillIfAlive(Process process)
    {
        try
        {
            if (process.Id != 0 && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
