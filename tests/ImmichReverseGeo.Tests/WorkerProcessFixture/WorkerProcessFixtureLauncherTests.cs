using System.Reflection;
using System.Text;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.WorkerProcessFixture;

[TestClass]
[TestCategory("Change26")]
public sealed class WorkerProcessFixtureLauncherTests
{
    [TestMethod]
    public void StagedFixture_HasExactApphostRuntimeAndCoreOnlyDependencies()
    {
        var directory = WorkerProcessFixtureLease.FixtureDirectory;
        Assert.IsTrue(File.Exists(WorkerProcessFixtureLease.FixtureExecutable));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "ImmichReverseGeo.WorkerProcessFixture.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "ImmichReverseGeo.WorkerProcessFixture.deps.json")));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "ImmichReverseGeo.WorkerProcessFixture.runtimeconfig.json")));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "ImmichReverseGeo.Core.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(directory, "ImmichReverseGeo.Web.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(directory, "ImmichReverseGeo.Overture.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(directory, "ImmichReverseGeo.Gadm.dll")));
        Assert.IsFalse(typeof(ChildWorkerLauncher).Assembly.GetReferencedAssemblies().Any(
            assembly => assembly.Name == "ImmichReverseGeo.WorkerProcessFixture"));
        Assert.IsFalse(Assembly.GetExecutingAssembly().GetReferencedAssemblies().Any(
            assembly => assembly.Name == "ImmichReverseGeo.WorkerProcessFixture"), "Project reference must be build-only.");
    }

    [TestMethod]
    [DataRow("ready", 0L)]
    [DataRow("no-work", 0L)]
    [DataRow("success", 1L)]
    public async Task NormalScenario_UsesRealLauncherAndPreservesExactRequestAndOrderedTerminal(string scenario, long expectedUpdated)
    {
        var lease = new WorkerProcessFixtureLease();
        await using (lease)
        {
            var session = await lease.LaunchAsync(scenario);
            var completion = await lease.CompleteAsync();
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup);
            Assert.AreEqual(WorkerProcessExitCodes.Completed, completion.ExitCode);
            Assert.IsNull(completion.FirstProtocolObservation);
            var terminal = Assert.IsInstanceOfType<CompletedPayload>(completion.Terminal!.Payload);
            Assert.AreEqual(expectedUpdated, terminal.UpdatedCount);
            Assert.AreEqual(expectedUpdated, terminal.ProcessedCount);
            var events = lease.Sink.Events;
            Assert.AreEqual(WorkerProtocolV1.ReadyType, events[0].Type);
            Assert.IsNull(events[0].RunId);
            Assert.AreEqual(WorkerProtocolV1.RunStartedType, events[1].Type);
            Assert.AreEqual(WorkerProtocolV1.EligibilityDeterminedType, events[2].Type);
            Assert.AreEqual(WorkerProtocolV1.CompletedType, events[^1].Type);
            Assert.AreEqual(scenario == "success" ? 8 : 4, events.Length);
            CollectionAssert.AreEqual(Enumerable.Range(1, events.Length).Select(x => (long)x).ToArray(), events.Select(x => x.Sequence).ToArray());
            Assert.IsTrue(events.Skip(1).All(e => e.RunId == lease.Request.RunId));
            lease.AssertExactCapture();
        }
        Assert.IsFalse(lease.ForcedCleanup, "Normal fixture must exit without a reaper kill.");
        Assert.AreEqual(1, lease.ProcessDisposeCalls);
        Assert.IsFalse(lease.IsRegistered);
        Assert.IsFalse(Directory.Exists(lease.Root));
    }

    [TestMethod]
    [DataRow(5)]
    [DataRow(42)]
    public async Task PreReadyCrash_RetainsRawExitAndDrainsBothStreams(int exitCode)
    {
        await using var lease = new WorkerProcessFixtureLease();
        var session = await lease.LaunchAsync("pre-ready-crash", false, "--exit-code", exitCode.ToString());
        var completion = await lease.CompleteAsync();
        Assert.AreEqual(exitCode, completion.ExitCode);
        Assert.IsTrue(await session.Startup is ChildWorkerStartupObservation.PreReadyExit or ChildWorkerStartupObservation.PreReadyEndOfStream);
        Assert.AreEqual(0, lease.Sink.Events.Length);
        Assert.AreEqual(0, lease.WrittenInput.Length);
        Assert.IsNull(completion.Terminal);
        Assert.AreEqual("fixture:pre-ready-crash\n", completion.StandardErrorTail.Text);
    }

    [TestMethod]
    public async Task PostReadyCrash_ProvesAcceptedExecuteBeforeMissingTerminalExit()
    {
        await using var lease = new WorkerProcessFixtureLease();
        await lease.LaunchAsync("post-ready-crash", true, "--exit-code", "42");
        var completion = await lease.CompleteAsync();
        Assert.AreEqual(42, completion.ExitCode);
        Assert.IsNull(completion.Terminal);
        Assert.IsTrue(lease.Sink.Events.Any(e => e.Payload is LogEmittedPayload log && log.Message == $"fixture:post-ready-crash:{lease.Request.RunId:D}"));
        Assert.AreEqual("fixture:post-ready-crash\n", completion.StandardErrorTail.Text);
        lease.AssertExactCapture();
    }

    [TestMethod]
    [DataRow("malformed", "--malformed-kind", "utf8", WorkerProtocolFailureCode.InvalidEncoding)]
    [DataRow("malformed", "--malformed-kind", "json", WorkerProtocolFailureCode.MalformedJson)]
    [DataRow("malformed", "--malformed-kind", "framing", WorkerProtocolFailureCode.InvalidFraming)]
    [DataRow("unknown", "--unknown-kind", "version", WorkerProtocolFailureCode.UnsupportedVersion)]
    [DataRow("unknown", "--unknown-kind", "category", WorkerProtocolFailureCode.UnsupportedType)]
    [DataRow("unknown", "--unknown-kind", "type", WorkerProtocolFailureCode.UnsupportedType)]
    [DataRow("invalid-sequence", "--sequence-fault", "gap", WorkerProtocolFailureCode.InvalidSequence)]
    [DataRow("invalid-sequence", "--sequence-fault", "replay", WorkerProtocolFailureCode.InvalidSequence)]
    public async Task InvalidOutput_PreservesFirstProtocolFactWithoutAcceptingInvalidCallback(string scenario, string option, string value, WorkerProtocolFailureCode expected)
    {
        await using var lease = new WorkerProcessFixtureLease();
        await lease.LaunchAsync(scenario, true, option, value);
        var completion = await lease.CompleteAsync();
        var failure = Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(completion.FirstProtocolObservation);
        Assert.AreEqual(expected, failure.Failure.Code);
        Assert.AreEqual(0, completion.ExitCode);
        Assert.IsNull(completion.Terminal);
        Assert.AreEqual(1, lease.Sink.Events.Length, "Only valid ready reaches the sink.");
        lease.AssertExactCapture();
    }

    [TestMethod]
    public async Task OversizeOutput_DrainsRealPipePastProtocolBound()
    {
        await using var lease = new WorkerProcessFixtureLease();
        await lease.LaunchAsync("oversize");
        var completion = await lease.CompleteAsync();
        var failure = Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(completion.FirstProtocolObservation);
        Assert.AreEqual(WorkerProtocolFailureCode.MessageTooLarge, failure.Failure.Code);
        Assert.AreEqual(0, completion.ExitCode);
        Assert.AreEqual(1, lease.Sink.Events.Length);
        lease.AssertExactCapture();
    }

    [TestMethod]
    [DataRow("completed", 4)]
    [DataRow("cancelled", 0)]
    [DataRow("failed", 0)]
    public async Task TerminalMismatch_PreservesBothFactsAndNoPostTerminalOutput(string terminal, int exitCode)
    {
        await using var lease = new WorkerProcessFixtureLease();
        await lease.LaunchAsync("terminal-mismatch", true, "--terminal", terminal, "--exit-code", exitCode.ToString());
        var completion = await lease.CompleteAsync();
        Assert.AreEqual(exitCode, completion.ExitCode);
        Assert.AreEqual(terminal, completion.Terminal!.Type);
        Assert.IsNull(completion.FirstProtocolObservation);
        Assert.AreSame(completion.Terminal, lease.Sink.Events[^1]);
        Assert.AreEqual(1, lease.Sink.Events.Count(e => e.Category == WorkerProtocolV1.TerminalCategory));
    }

    [TestMethod]
    public async Task StderrFlood_DrainsConcurrentlyAndRetainsExactBoundedSuffix()
    {
        const int total = 262_177;
        await using var lease = new WorkerProcessFixtureLease();
        await lease.LaunchAsync("stderr-flood", true, "--stderr-bytes", total.ToString());
        var completion = await lease.CompleteAsync();
        var prefix = Encoding.ASCII.GetBytes("fixture-stderr-prefix\n");
        var suffix = Encoding.ASCII.GetBytes("\nfixture-stderr-suffix\n");
        var bodyLength = total - prefix.Length - suffix.Length;
        var expected = new byte[65_536];
        for (var i = 0; i < expected.Length; i++)
        {
            var position = total - expected.Length + i;
            expected[i] = position < prefix.Length + bodyLength
                ? (byte)('a' + (position - prefix.Length) % 26)
                : suffix[position - prefix.Length - bodyLength];
        }
        Assert.AreEqual(0, completion.ExitCode);
        Assert.IsNotNull(completion.Terminal);
        Assert.IsNull(completion.FirstProtocolObservation);
        Assert.AreEqual(total, completion.StandardErrorTail.TotalBytes);
        Assert.IsTrue(completion.StandardErrorTail.IsTruncated);
        Assert.IsFalse(completion.StandardErrorTail.TotalBytesSaturated);
        CollectionAssert.AreEqual(expected, completion.StandardErrorTail.Bytes.ToArray());
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
    public async Task RawExit_ReportsExactManagedCodeWithoutInventingTerminal(int exitCode)
    {
        await using var lease = new WorkerProcessFixtureLease();
        await lease.LaunchAsync("raw-exit", false, "--exit-code", exitCode.ToString());
        var completion = await lease.CompleteAsync();
        Assert.AreEqual(exitCode, completion.ExitCode);
        Assert.IsNull(completion.Terminal);
        Assert.AreEqual(0, lease.Sink.Events.Length);
    }

    [TestMethod]
    [DataRow("cooperative-cancel")]
    [DataRow("unresponsive")]
    public async Task CancellationModes_ReachArmedHandshakeThenTestReaperOwnsCleanup(string scenario)
    {
        var lease = new WorkerProcessFixtureLease();
        await using (lease)
        {
            var session = await lease.LaunchAsync(scenario);
            await lease.Sink.WaitForAsync(e => e.Payload is LogEmittedPayload log && log.Message == $"fixture:{scenario}:{lease.Request.RunId:D}");
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup);
            Assert.IsFalse(session.Completion.IsCompleted);
            lease.AssertExactCapture();
        }
        Assert.AreEqual(1, lease.ProcessDisposeCalls);
        Assert.IsFalse(lease.IsRegistered);
    }

    [TestMethod]
    public async Task ConcurrentRunsAndEarlyAbort_AreIsolatedAndCleanupIsIdempotent()
    {
        var leases = Enumerable.Range(0, 4).Select(_ => new WorkerProcessFixtureLease()).ToArray();
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => LaunchArmAbortAndCleanupAsync(leases));
        Assert.AreEqual("Injected test abort after all owned processes are armed.", failure.Message);

        foreach (var lease in leases)
        {
            Assert.IsTrue(lease.HasExited);
            Assert.IsTrue(lease.ForcedCleanup);
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
            Assert.IsFalse(lease.IsRegistered);
            Assert.IsFalse(Directory.Exists(lease.Root));
        }
    }

    [TestMethod]
    public async Task CleanupFailure_RemainsRegisteredUntilControlledReaperRetriesUnfinishedPhases()
    {
        var lease = new WorkerProcessFixtureLease(FixtureCleanupPhase.Drain);
        try
        {
            await lease.LaunchAsync("unresponsive");
            await lease.Sink.WaitForAsync(@event =>
                @event.Payload is LogEmittedPayload log
                && log.Message == $"fixture:unresponsive:{lease.Request.RunId:D}");

            var failure = await Assert.ThrowsExactlyAsync<AggregateException>(
                () => lease.DisposeAsync().AsTask());
            StringAssert.Contains(failure.ToString(), "cleanup phase 'drain'");
            Assert.IsTrue(lease.HasExited, "Exit confirmation must survive a later drain failure.");
            Assert.IsTrue(lease.IsRegistered, "Unfinished cleanup must remain available to the reaper.");
            var completion = await lease.Session!.Settlement.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality);
            Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality);
            Assert.AreEqual(1, lease.ProcessDisposeCalls, "The session independently releases its adapter after confirmed exit and drain finality.");

            await WorkerProcessFixtureLease.ReapAsync([lease]);
            Assert.IsFalse(lease.IsRegistered);
            Assert.IsFalse(Directory.Exists(lease.Root));
            Assert.AreEqual(1, lease.ProcessDisposeCalls);

            await lease.DisposeAsync();
            Assert.AreEqual(1, lease.ProcessDisposeCalls, "Successful retry must preserve exactly-once adapter disposal.");
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task MalformedDirectOutput_FaultedDrainIsReportedAfterAdapterAndResourcesAreCleaned()
    {
        var lease = new WorkerProcessFixtureLease();
        try
        {
            var direct = await lease.StartDirectAsync(
                lease.Arguments("malformed", options: ["--malformed-kind", "json"]));
            await lease.Sink.WaitForAsync(@event => @event.Type == WorkerProtocolV1.ReadyType);
            await direct.SendAsync(lease.Request);
            var directFailure = await Assert.ThrowsExactlyAsync<AssertFailedException>(
                () => direct.CompleteAsync());
            lease.AssertExactCapture();

            var failure = await Assert.ThrowsExactlyAsync<AggregateException>(
                () => lease.DisposeAsync().AsTask());
            StringAssert.Contains(failure.ToString(), "cleanup phase 'drain'");
            StringAssert.Contains(failure.ToString(), directFailure.Message);
            Assert.IsTrue(lease.HasExited);
            Assert.AreEqual(1, lease.ProcessDisposeCalls, "A completed faulted drain must not block adapter disposal.");
            Assert.IsFalse(lease.IsRegistered, "A completed faulted drain must not leave retryable cleanup work.");
            Assert.IsFalse(Directory.Exists(lease.Root));

            await lease.DisposeAsync();
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    private static async Task LaunchArmAbortAndCleanupAsync(WorkerProcessFixtureLease[] leases)
    {
        Exception? primaryFailure = null;
        try
        {
            await Task.WhenAll(leases.Select(async lease =>
            {
                await lease.LaunchAsync("unresponsive");
                await lease.Sink.WaitForAsync(@event =>
                    @event.Payload is LogEmittedPayload log
                    && log.Message == $"fixture:unresponsive:{lease.Request.RunId:D}");
                lease.AssertExactCapture();
            }));
            Assert.AreEqual(4, leases.Select(lease => lease.ProcessId).Distinct().Count());
            Assert.AreEqual(4, leases.Select(lease => lease.Request.RunId).Distinct().Count());
            Assert.AreEqual(4, leases.Select(lease => lease.Root).Distinct().Count());
            throw new InvalidOperationException("Injected test abort after all owned processes are armed.");
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            var cleanupFailures = await DisposeLeasesAsync(leases);
            if (cleanupFailures.Count > 0)
            {
                var failures = primaryFailure is null
                    ? cleanupFailures
                    : new[] { primaryFailure }.Concat(cleanupFailures).ToArray();
                throw new AggregateException("Fixture test body and cleanup failures were preserved.", failures);
            }
        }
    }

    private static async Task<IReadOnlyList<Exception>> DisposeLeasesAsync(IEnumerable<WorkerProcessFixtureLease> leases)
    {
        var attempts = leases.Select(async lease =>
        {
            try
            {
                var first = lease.DisposeAsync().AsTask();
                var second = lease.DisposeAsync().AsTask();
                Assert.AreSame(first, second);
                await first;
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });
        var failures = await Task.WhenAll(attempts);
        return failures.Where(failure => failure is not null).Cast<Exception>().ToArray();
    }
}
