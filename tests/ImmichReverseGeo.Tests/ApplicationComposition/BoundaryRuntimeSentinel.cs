using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

internal sealed record BoundaryActivation(int Order, string Owner, string Category, int Count);

internal sealed class BoundaryRuntimeSentinel(BoundaryRole role)
{
    private readonly List<BoundaryActivation> _events = [];
    private readonly object _gate = new();

    internal IReadOnlyList<BoundaryActivation> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    internal BoundaryActivation Record(string owner, string category)
    {
        lock (_gate)
        {
            var activation = new BoundaryActivation(_events.Count + 1, owner, category, _events.Count(e => e.Category == category) + 1);
            _events.Add(activation);
            return activation;
        }
    }

    internal T Forbid<T>(string owner, string category)
    {
        BoundaryActivation activation = Record(owner, category);
        throw new AssertFailedException(ControlPlaneDependencyPolicy.Diagnostic("RuntimeActivation", role, owner,
            category, typeof(T).ToString(), $"order {activation.Order} -> count {activation.Count}").ToString());
    }
}

[TestClass]
[TestCategory("Change56")]
public sealed class ControlPlaneRuntimeSentinelTests
{
    [TestMethod]
    public void HiddenCallbackAndUnexpectedLaunch_FailBeforeWorkWithInstanceScopedOrderedEvidence()
    {
        var first = new BoundaryRuntimeSentinel(BoundaryRole.WebOnly);
        var other = new BoundaryRuntimeSentinel(BoundaryRole.Standard);
        foreach (string category in new[] { "native/DuckDB", "country index", "geodata file/query", "download/export/mutation", "in-process executor", "PostgreSQL", "inventory", "worker session", "country index" })
        {
            Func<object> hidden = () => first.Forbid<object>("Lookup callback", category);
            AssertFailedException failure = Assert.ThrowsExactly<AssertFailedException>(() => hidden());
            StringAssert.Contains(failure.Message, "RuntimeActivation [WebOnly]");
            StringAssert.Contains(failure.Message, "Lookup callback");
            StringAssert.Contains(failure.Message, category);
        }
        CollectionAssert.AreEqual(Enumerable.Range(1, 9).ToArray(), first.Events.Select(e => e.Order).ToArray());
        Assert.AreEqual(2, first.Events[^1].Count);
        Assert.IsEmpty(other.Events, "Runtime sentinels never share mutable counters across roles or tests.");
    }
}
