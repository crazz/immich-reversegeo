using ImmichReverseGeo.Core.WorkerJobs;

namespace ImmichReverseGeo.Tests.WorkerMemorySoak;

[TestClass]
[TestCategory("Change68")]
[TestCategory("Performance")]
public sealed class WorkerMemorySoakConfigurationTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void SeededMix_PreservesExactProportionsAndSuccessfulPeers(bool cancellation, bool failure)
    {
        var config = WorkerMemorySoakConfiguration.Create(cancellation: cancellation, failure: failure);
        var first = config.CreateSequence();
        CollectionAssert.AreEqual(first.ToArray(), config.CreateSequence().ToArray());
        Assert.AreEqual(15, first.Count);
        CollectionAssert.AreEqual(Enumerable.Range(1, 15).ToArray(), first.Select(i => i.Order).ToArray());
        foreach (var phase in new[] { SoakPhase.Warmup, SoakPhase.Measured })
        {
            var rows = first.Where(i => i.Phase == phase).ToArray();
            int rounds = phase == SoakPhase.Warmup ? 1 : 2;
            Assert.AreEqual(3 * rounds, rows.Count(i => i.Kind == WorkerJobKind.ProcessAssets));
            Assert.AreEqual(rounds, rows.Count(i => i.Kind == WorkerJobKind.CoordinateLookup));
            Assert.AreEqual(rounds, rows.Count(i => i.Kind == WorkerJobKind.CacheMutation));
            foreach (var kind in new[] { WorkerJobKind.ProcessAssets, WorkerJobKind.CoordinateLookup, WorkerJobKind.CacheMutation })
            {
                Assert.IsTrue(rows.Any(i => i.Kind == kind && i.Cycle == SoakCycle.Success));
            }
        }
        Assert.IsTrue(first.Where(i => i.Phase == SoakPhase.Warmup).All(i => i.Cycle == SoakCycle.Success));
        Assert.AreEqual(cancellation ? 2 : 0, first.Count(i => i.Cycle == SoakCycle.CooperativeCancellation));
        Assert.AreEqual(failure ? 1 : 0, first.Count(i => i.Cycle == SoakCycle.PrePublicationFailure));
    }

    [TestMethod]
    public void DifferentSeeds_ChangeOrderWithoutChangingCoverage()
    {
        var first = WorkerMemorySoakConfiguration.Create(seed: 6801).CreateSequence();
        var second = WorkerMemorySoakConfiguration.Create(seed: 6802).CreateSequence();
        Assert.IsFalse(first.SequenceEqual(second));
        CollectionAssert.AreEquivalent(first.Select(i => (i.Phase, i.Kind)).ToArray(),
            second.Select(i => (i.Phase, i.Kind)).ToArray());
    }

    [TestMethod]
    [DataRow(0, 10)]
    [DataRow(-5, 10)]
    [DataRow(5, 0)]
    [DataRow(5, 100005)]
    [DataRow(1, 10)]
    [DataRow(5, 11)]
    public void InvalidCounts_CannotOmitKindsOrCreateUnboundedWork(int warmup, int measured)
    {
        var failure = Assert.ThrowsExactly<ArgumentException>(() =>
            WorkerMemorySoakConfiguration.Create(warmupIterations: warmup, measuredIterations: measured));
        StringAssert.StartsWith(failure.Message, "soak-config/");
    }

    [TestMethod]
    [DataRow(0, 1, 1)]
    [DataRow(3, 0, 1)]
    [DataRow(3, 1, 0)]
    [DataRow(2, 1, 1)]
    [DataRow(int.MaxValue, 1, 1)]
    public void InvalidWeights_CannotLoseMajorityOrEitherV2Kind(int processing, int lookup, int cache)
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            WorkerMemorySoakConfiguration.Create(weights: new(processing, lookup, cache)));
    }

    [TestMethod]
    public void FailureCycle_RequiresAnotherSuccessfulMeasuredCacheAttempt()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            WorkerMemorySoakConfiguration.Create(measuredIterations: 5, failure: true));
    }
}
