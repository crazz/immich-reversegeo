using System.ComponentModel;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.ChildWorkerCancellation;

[TestClass]
[TestCategory("Change28")]
public sealed class ProcessAdapterTests
{
    [TestMethod]
    [DataRow("permission", (int)ChildProcessKillOutcome.PermissionDenied)]
    [DataRow("native-permission", (int)ChildProcessKillOutcome.PermissionDenied)]
    [DataRow("unsupported", (int)ChildProcessKillOutcome.Unsupported)]
    [DataRow("state", (int)ChildProcessKillOutcome.Failed)]
    [DataRow("partial-tree", (int)ChildProcessKillOutcome.Failed)]
    public void KillFailures_AreBoundedFactsWithoutRawExceptionRetention(string kind, int expected)
    {
        Exception failure = kind switch
        {
            "permission" => new UnauthorizedAccessException("private platform detail"),
            "native-permission" => new Win32Exception(OperatingSystem.IsWindows() ? 5 : 13, "private native detail"),
            "unsupported" => new PlatformNotSupportedException("private unsupported detail"),
            "partial-tree" => new AggregateException(new UnauthorizedAccessException("private descendant detail")),
            _ => new InvalidOperationException("private state detail")
        };

        Assert.AreEqual((ChildProcessKillOutcome)expected, SystemChildProcessFactory.SystemChildProcess.NormalizeKillFailure(failure));
    }

    [TestMethod]
    public void StopPolicy_HasOneFixedTenSecondGrace()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(10), ChildWorkerCancellationPolicy.Grace);
        Assert.ThrowsExactly<ArgumentNullException>(() => ChildWorkerStopRequest.Capture(null!));
    }
}
