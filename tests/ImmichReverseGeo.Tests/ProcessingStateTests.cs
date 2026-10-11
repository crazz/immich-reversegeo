using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ProcessingStateTests
{
    [TestMethod]
    public void RecentLog_KeepsUtcTimestampsAndReturnsIndependentSnapshots()
    {
        var state = new ProcessingState();
        state.AppendLog("First message");
        var entries = state.GetRecentLogEntries();
        state.AppendLog("Second message");

        Assert.HasCount(1, entries);
        Assert.AreEqual(TimeSpan.Zero, entries[0].Timestamp.Offset);
        Assert.AreEqual("First message", entries[0].Message);
        Assert.AreEqual($"[{entries[0].Timestamp.UtcDateTime:O}] First message", state.GetRecentLog()[0]);
    }

    [TestMethod]
    public void RecentLog_RemainsBoundedAndOrdered()
    {
        var state = new ProcessingState();
        for (var i = 0; i < 105; i++)
        {
            state.AppendLog($"Message {i}");
        }

        var entries = state.GetRecentLogEntries();
        Assert.HasCount(100, entries);
        Assert.AreEqual("Message 5", entries[0].Message);
        Assert.AreEqual("Message 104", entries[^1].Message);
    }

    [TestMethod]
    public void BeginActivity_KeepsActivityVisibleUntilLastScopeEnds()
    {
        var state = new ProcessingState();

        var scope1 = state.BeginActivity("Downloading Overture divisions for Spain (ESP)...");
        var scope2 = state.BeginActivity("Downloading Overture divisions for Spain (ESP)...");

        Assert.AreEqual("Downloading Overture divisions for Spain (ESP)...", state.CurrentActivity);

        scope1.Dispose();
        Assert.AreEqual("Downloading Overture divisions for Spain (ESP)...", state.CurrentActivity);

        scope2.Dispose();
        Assert.IsNull(state.CurrentActivity);
    }
}
