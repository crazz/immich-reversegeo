using System.Text;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.WorkerProcessFixture;

[TestClass]
[TestCategory("Change47")]
public sealed class WorkerProcessFixtureVersionParityTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ActualProcess_SelectedVersionUsesSameReadyDispatchOrderAndCompletedResult(
        int version)
    {
        var protocolVersion = Version(version);
        await using var lease = new WorkerProcessFixtureLease();

        var session = await lease.LaunchAsync("success", protocolVersion);
        var completion = await lease.CompleteAsync();

        Assert.AreEqual(protocolVersion, session.ProtocolVersion);
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup);
        Assert.AreEqual(
            WorkerProcessExitCodes.Completed,
            completion.ExitCode,
            completion.StandardErrorTail.Text);
        Assert.IsNull(completion.FirstProtocolObservation);
        var terminal = Assert.IsInstanceOfType<CompletedPayload>(completion.Terminal!.Payload);
        Assert.AreEqual(1L, terminal.ProcessedCount);
        Assert.AreEqual(1L, terminal.UpdatedCount);
        Assert.AreEqual(0L, terminal.FailedCount);
        CollectionAssert.AreEqual(
            Enumerable.Range(1, 8).Select(value => (long)value).ToArray(),
            lease.Sink.Events.Select(@event => @event.Sequence).ToArray());
        Assert.AreEqual(WorkerProtocolV1.ReadyType, lease.Sink.Events[0].Type);
        Assert.AreEqual(WorkerProtocolV1.RunStartedType, lease.Sink.Events[1].Type);
        Assert.AreEqual(WorkerProtocolV1.CompletedType, lease.Sink.Events[^1].Type);
        Assert.IsTrue(lease.Sink.Events.Skip(1).All(@event => @event.RunId == lease.Request.RunId));
        lease.AssertExactCapture();
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ActualProcess_MalformedOutputPreservesSameFirstFailureAndFinality(
        int version)
    {
        await using var lease = new WorkerProcessFixtureLease();
        await lease.LaunchAsync(
            "malformed",
            Version(version),
            true,
            "--malformed-kind",
            "json");

        var completion = await lease.CompleteAsync();

        var failure = Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(
            completion.FirstProtocolObservation);
        Assert.AreEqual(WorkerProtocolFailureCode.MalformedJson, failure.Failure.Code);
        Assert.AreEqual(
            WorkerProcessExitCodes.Completed,
            completion.ExitCode,
            completion.StandardErrorTail.Text);
        Assert.IsNull(completion.Terminal);
        Assert.AreEqual(1, lease.Sink.Events.Length, "Only ready may reach the shared sink.");
        lease.AssertExactCapture();
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ActualProcess_PostReadyCrashPreservesAcceptedExecuteStderrAndRawExit(
        int version)
    {
        await using var lease = new WorkerProcessFixtureLease();
        await lease.LaunchAsync(
            "post-ready-crash",
            Version(version),
            true,
            "--exit-code",
            "42");

        var completion = await lease.CompleteAsync();

        Assert.AreEqual(42, completion.ExitCode);
        Assert.IsNull(completion.Terminal);
        Assert.AreEqual("fixture:post-ready-crash\n", completion.StandardErrorTail.Text);
        Assert.IsTrue(lease.Sink.Events.Any(@event =>
            @event.Payload is LogEmittedPayload log
            && log.Message == $"fixture:post-ready-crash:{lease.Request.RunId:D}"));
        lease.AssertExactCapture();
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ActualProcess_StderrFloodDrainsConcurrentlyWithCommittedTerminal(
        int version)
    {
        const int totalBytes = 131_089;
        await using var lease = new WorkerProcessFixtureLease();
        await lease.LaunchAsync(
            "stderr-flood",
            Version(version),
            true,
            "--stderr-bytes",
            totalBytes.ToString());

        var completion = await lease.CompleteAsync();

        Assert.AreEqual(
            WorkerProcessExitCodes.Completed,
            completion.ExitCode,
            completion.StandardErrorTail.Text);
        Assert.IsNotNull(completion.Terminal);
        Assert.IsNull(completion.FirstProtocolObservation);
        Assert.AreEqual(totalBytes, completion.StandardErrorTail.TotalBytes);
        Assert.IsTrue(completion.StandardErrorTail.IsTruncated);
        StringAssert.EndsWith(
            completion.StandardErrorTail.Text,
            "\nfixture-stderr-suffix\n");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ActualProcess_CooperativeCancellationUsesOneCorrelatedCancelAndExit130(
        int version)
    {
        var protocolVersion = Version(version);
        await using var lease = new WorkerProcessFixtureLease();
        var session = await lease.LaunchAsync("cooperative-cancel", protocolVersion);
        await lease.Sink.WaitForAsync(@event =>
            @event.Payload is LogEmittedPayload log
            && log.Message == $"fixture:cooperative-cancel:{lease.Request.RunId:D}");

        var firstStop = session.RequestStop();
        Assert.AreSame(firstStop, session.RequestStop());
        var result = await firstStop.WaitAsync(WorkerProcessFixtureLease.Watchdog);

        Assert.IsTrue(result.Facts.RequestAccepted);
        Assert.IsFalse(result.Facts.GraceExpired);
        Assert.IsFalse(result.Facts.KillAttempted);
        Assert.AreEqual(
            WorkerProcessExitCodes.Cancelled,
            result.Completion.ExitCode,
            result.Completion.StandardErrorTail.Text);
        Assert.AreEqual(WorkerProtocolV1.CancelledType, result.Completion.Terminal?.Type);
        Assert.AreEqual(1, lease.Sink.Events.Count(@event =>
            @event.Category == WorkerProtocolV1.TerminalCategory));
        AssertControllerFrames(lease, protocolVersion);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ActualProcess_ContradictoryExitAndRawExitRemainSeparateFacts(int version)
    {
        await using (var terminalLease = new WorkerProcessFixtureLease())
        {
            await terminalLease.LaunchAsync(
                "terminal-mismatch",
                Version(version),
                true,
                "--terminal",
                "completed",
                "--exit-code",
                WorkerProcessExitCodes.ExecutorFailure.ToString());
            var completion = await terminalLease.CompleteAsync();
            Assert.AreEqual(
                WorkerProcessExitCodes.ExecutorFailure,
                completion.ExitCode,
                completion.StandardErrorTail.Text);
            Assert.IsInstanceOfType<CompletedPayload>(completion.Terminal!.Payload);
            Assert.IsNull(completion.FirstProtocolObservation);
        }

        await using var rawExitLease = new WorkerProcessFixtureLease();
        await rawExitLease.LaunchAsync(
            "raw-exit",
            Version(version),
            false,
            "--exit-code",
            "42");
        var rawExit = await rawExitLease.CompleteAsync();
        Assert.AreEqual(42, rawExit.ExitCode);
        Assert.IsNull(rawExit.Terminal);
        Assert.AreEqual(0, rawExitLease.Sink.Events.Length);
    }

    private static InternalWorkerProtocolVersion Version(int value) =>
        (InternalWorkerProtocolVersion)value;

    private static void AssertControllerFrames(
        WorkerProcessFixtureLease lease,
        InternalWorkerProtocolVersion protocolVersion)
    {
        string[] lines = Encoding.UTF8.GetString(lease.WrittenInput.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(2, lines.Length, "One execute and one cancel must be the entire input.");
        for (var index = 0; index < lines.Length; index++)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(lines[index]);
            if (protocolVersion == InternalWorkerProtocolVersion.V1)
            {
                var parsed = WorkerProtocolCodec.ParseControllerInput(bytes);
                Assert.IsTrue(parsed.IsSuccess, parsed.Failure?.Diagnostic);
                Assert.AreEqual(index + 1L, parsed.Message!.Sequence);
                Assert.AreEqual(lease.Request.RunId, parsed.Message.RunId);
                Assert.AreEqual(
                    index == 0 ? WorkerProtocolV1.ExecuteType : WorkerProtocolV1.CancelType,
                    parsed.Message.Type);
            }
            else
            {
                var parsed = WorkerJobProtocolCodec.ParseControllerInput(bytes);
                Assert.IsTrue(parsed.IsSuccess, parsed.Failure?.Diagnostic);
                Assert.AreEqual(index + 1L, parsed.Message!.Sequence);
                Assert.AreEqual(lease.Request.RunId, parsed.Message.JobId);
                Assert.AreEqual(WorkerJobKind.ProcessAssets, parsed.Message.JobKind);
                Assert.AreEqual(
                    index == 0 ? WorkerJobProtocolV2.ExecuteType : WorkerJobProtocolV2.CancelType,
                    parsed.Message.Type);
            }
        }
    }
}
