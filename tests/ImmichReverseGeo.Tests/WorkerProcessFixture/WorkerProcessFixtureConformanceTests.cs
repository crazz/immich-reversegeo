using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerProcessFixture;

[TestClass]
[TestCategory("Change26")]
public sealed class WorkerProcessFixtureConformanceTests
{
    [TestMethod]
    [DataRow("missing-value")]
    [DataRow("duplicate")]
    [DataRow("unknown-switch")]
    [DataRow("unknown-scenario")]
    [DataRow("relative-root")]
    [DataRow("capture-traversal")]
    [DataRow("exit-negative")]
    [DataRow("exit-overflow")]
    [DataRow("exit-malformed")]
    [DataRow("stderr-small")]
    [DataRow("stderr-large")]
    [DataRow("invalid-subcase")]
    [DataRow("inapplicable-option")]
    [DataRow("not-a-mismatch")]
    public async Task InvalidFixtureArguments_FailClosedBeforeProtocolOrCapture(string scenario)
    {
        await using var lease = new WorkerProcessFixtureLease();
        string[] arguments = scenario switch
        {
            "missing-value" => ["--scenario"],
            "duplicate" => [.. lease.Arguments("ready"), "--scenario", "ready"],
            "unknown-switch" => [.. lease.Arguments("ready"), "--unknown", "value"],
            "unknown-scenario" => lease.Arguments("READY"),
            "relative-root" => ["--scenario", "ready", "--resource-root", "relative-root"],
            "capture-traversal" => ["--scenario", "ready", "--resource-root", lease.Root, "--capture-name", "../request.ndjson"],
            "exit-negative" => lease.Arguments("raw-exit", false, "--exit-code", "-1"),
            "exit-overflow" => lease.Arguments("raw-exit", false, "--exit-code", "256"),
            "exit-malformed" => lease.Arguments("raw-exit", false, "--exit-code", "x"),
            "stderr-small" => lease.Arguments("stderr-flood", false, "--stderr-bytes", "1"),
            "stderr-large" => lease.Arguments("stderr-flood", false, "--stderr-bytes", "8388609"),
            "invalid-subcase" => lease.Arguments("malformed", false, "--malformed-kind", "other"),
            "inapplicable-option" => lease.Arguments("ready", false, "--exit-code", "0"),
            "not-a-mismatch" => lease.Arguments("terminal-mismatch", false, "--terminal", "completed", "--exit-code", "0"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var fixture = await lease.StartDirectAsync(arguments);
        Assert.AreEqual(WorkerProcessExitCodes.InvalidInput, await fixture.CompleteAsync());
        Assert.AreEqual(0, lease.Sink.Events.Length);
        StringAssert.StartsWith(fixture.ErrorText, "fixture-usage:");
        Assert.IsTrue(fixture.ErrorText.Length <= 256);
        Assert.AreEqual(0, Directory.GetFiles(lease.Root).Length);
        Assert.AreEqual(0, lease.WrittenInput.Length);
    }

    [TestMethod]
    public async Task ReadyBeforeExecute_AtomicCapturePrecedesFirstRunEvent()
    {
        await using var lease = new WorkerProcessFixtureLease();
        var fixture = await lease.StartDirectAsync(lease.Arguments("ready"));
        await lease.Sink.WaitForAsync(e => e.Type == WorkerProtocolV1.ReadyType);
        Assert.AreEqual(1, lease.Sink.Events.Length);
        Assert.AreEqual(0, lease.WrittenInput.Length);
        Assert.IsFalse(File.Exists(lease.CapturePath));
        await fixture.SendAsync(lease.Request);
        await lease.Sink.WaitForAsync(e => e.Type == WorkerProtocolV1.RunStartedType);
        lease.AssertExactCapture();
        Assert.AreEqual(WorkerProcessExitCodes.Completed, await fixture.CompleteAsync());
        Assert.IsInstanceOfType<CompletedPayload>(lease.Sink.Events[^1].Payload);
    }

    [TestMethod]
    [DataRow(WorkerProcessExitCodes.Completed)]
    [DataRow(WorkerProcessExitCodes.InvalidInput)]
    [DataRow(WorkerProcessExitCodes.Busy)]
    [DataRow(WorkerProcessExitCodes.ExecutorFailure)]
    [DataRow(WorkerProcessExitCodes.InfrastructureFailure)]
    [DataRow(WorkerProcessExitCodes.OutputTransportFailure)]
    [DataRow(WorkerProcessExitCodes.Cancelled)]
    [DataRow(42)]
    public async Task DirectRawExit_ReportsEverySelectedPortableCode(int exitCode)
    {
        await using var lease = new WorkerProcessFixtureLease();
        var fixture = await lease.StartDirectAsync(lease.Arguments("raw-exit", false, "--exit-code", exitCode.ToString()));
        Assert.AreEqual(exitCode, await fixture.CompleteAsync());
        Assert.AreEqual(0, lease.Sink.Events.Length);
    }

    [TestMethod]
    public async Task CooperativeFixture_AcceptsCorrelatedCancelAndEmitsOneCancelledTerminal()
    {
        await using var lease = new WorkerProcessFixtureLease();
        var fixture = await lease.StartDirectAsync(lease.Arguments("cooperative-cancel"));
        await lease.Sink.WaitForAsync(e => e.Type == WorkerProtocolV1.ReadyType);
        await fixture.SendAsync(lease.Request);
        await lease.Sink.WaitForAsync(e => e.Payload is LogEmittedPayload log && log.Message == $"fixture:cooperative-cancel:{lease.Request.RunId:D}");
        lease.AssertExactCapture();
        await fixture.SendAsync(lease.Request, cancel: true);
        Assert.AreEqual(WorkerProcessExitCodes.Cancelled, await fixture.CompleteAsync());
        Assert.AreEqual(1, lease.Sink.Events.Count(e => e.Category == WorkerProtocolV1.TerminalCategory));
        Assert.IsInstanceOfType<CancelledPayload>(lease.Sink.Events[^1].Payload);
        Assert.AreEqual(lease.Request.RunId, lease.Sink.Events[^1].RunId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnresponsiveFixture_ConfirmsCancelOrEofAndRemainsOwnedUntilKilled(bool closeInput)
    {
        var lease = new WorkerProcessFixtureLease();
        await using (lease)
        {
            var fixture = await lease.StartDirectAsync(lease.Arguments("unresponsive"));
            await lease.Sink.WaitForAsync(e => e.Type == WorkerProtocolV1.ReadyType);
            await fixture.SendAsync(lease.Request);
            await lease.Sink.WaitForAsync(e => e.Payload is LogEmittedPayload log && log.Message == $"fixture:unresponsive:{lease.Request.RunId:D}");
            if (closeInput)
            {
                lease.StandardInput.Dispose();
            }
            else
            {
                await fixture.SendAsync(lease.Request, cancel: true);
            }
            var marker = closeInput ? "input-closed" : "cancel-observed";
            await lease.Sink.WaitForAsync(e => e.Payload is LogEmittedPayload log && log.Message == $"fixture:{marker}:{lease.Request.RunId:D}");
            Assert.IsFalse(lease.HasExited, "Positive input-handled handshake must leave the unresponsive fixture alive.");
            Assert.IsFalse(fixture.OutputDrain.IsCompleted);
            Assert.IsFalse(lease.Sink.Events.Any(e => e.Category == WorkerProtocolV1.TerminalCategory));
        }
        Assert.IsTrue(lease.ForcedCleanup);
        Assert.AreEqual(1, lease.ProcessDisposeCalls);
        Assert.IsFalse(lease.IsRegistered);
    }

    [TestMethod]
    public async Task ControllerInputSequence_IsValidatedWithoutCapturingRejectedExecute()
    {
        await using var lease = new WorkerProcessFixtureLease();
        var fixture = await lease.StartDirectAsync(lease.Arguments("ready"));
        await lease.Sink.WaitForAsync(e => e.Type == WorkerProtocolV1.ReadyType);
        var invalid = new WorkerProtocolControllerMessage(WorkerProtocolV1.RequestCategory, WorkerProtocolV1.ExecuteType,
            2, DateTimeOffset.UtcNow, lease.Request.RunId, new ExecuteRequestPayload(lease.Request));
        var bytes = WorkerProtocolCodec.SerializeControllerInput(invalid).Concat(new byte[] { (byte)'\n' }).ToArray();
        await lease.StandardInput.WriteAsync(bytes);
        await lease.StandardInput.FlushAsync();
        Assert.AreEqual(WorkerProcessExitCodes.InvalidInput, await fixture.CompleteAsync());
        Assert.IsFalse(File.Exists(lease.CapturePath));
        Assert.AreEqual(1, lease.Sink.Events.Length);
    }
}
