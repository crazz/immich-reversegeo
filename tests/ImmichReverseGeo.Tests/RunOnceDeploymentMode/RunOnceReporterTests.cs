using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.RunOnce;

namespace ImmichReverseGeo.Tests.RunOnceDeploymentMode;

[TestClass]
[TestCategory("Change43")]
public sealed class RunOnceReporterTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Reporter_UsesValidatedSessionAndProjectsOnlyBoundedSafeOrdinaryLines()
    {
        const string secret = "postgres://secret-user:secret-password@private-host/db";
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var reporter = new RunOnceProcessingEventReporter(stdout, stderr);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        var session = await reporter.OpenRunAsync(request, Started);

        await session.DetermineEligibilityAsync(1);
        await session.ReportLogAsync(ProcessingLogLevel.Error, secret);
        await session.ReportFailedAsync();
        var result = new ProcessingRunResult(
            request,
            Started,
            Started.AddSeconds(1),
            1,
            0,
            0,
            1,
            ProcessingRunOutcome.Failed,
            secret);
        await session.FinishAsync(result);

        string combined = stdout + stderr.ToString();
        Assert.IsFalse(combined.Contains(secret, StringComparison.Ordinal));
        StringAssert.Contains(stdout.ToString(), "Run started.");
        StringAssert.Contains(stdout.ToString(), "Eligible assets: 1.");
        StringAssert.Contains(stderr.ToString(), "Processing reported an error message.");
        StringAssert.Contains(stderr.ToString(), "Run failed: processed=1 updated=0 skipped=0 failed=1.");
        Assert.IsFalse(combined.TrimStart().StartsWith('{'));
        Assert.IsFalse(combined.Contains("run-started", StringComparison.Ordinal));
        Assert.IsFalse(combined.Contains("sequence", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(combined.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).All(line => line.Length <= 128));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await session.DetermineEligibilityAsync(1));
    }

    [TestMethod]
    public async Task Reporter_WriteFailuresAreBestEffortAndDoNotBreakOrRetryTheSession()
    {
        var writer = new ThrowingWriter();
        var reporter = new RunOnceProcessingEventReporter(writer, writer);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        var session = await reporter.OpenRunAsync(request, Started);
        await session.DetermineEligibilityAsync(0);
        var result = new ProcessingRunResult(
            request,
            Started,
            Started,
            0,
            0,
            0,
            0,
            ProcessingRunOutcome.Completed,
            null);
        await session.FinishAsync(result);

        Assert.AreEqual(3, writer.WriteCalls);
    }

    private sealed class ThrowingWriter : StringWriter
    {
        internal int WriteCalls { get; private set; }

        public override Task WriteLineAsync(string? value)
        {
            WriteCalls++;
            return Task.FromException(new IOException("writer unavailable"));
        }
    }
}
