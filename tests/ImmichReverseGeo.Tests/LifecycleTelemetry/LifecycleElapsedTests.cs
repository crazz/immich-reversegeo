using System;
using ImmichReverseGeo.Web.LifecycleTelemetry;

namespace ImmichReverseGeo.Tests.LifecycleTelemetry;

[TestClass]
[TestCategory("Change66")]
public sealed class LifecycleElapsedTests
{
    [TestMethod]
    [DataRow(0L, 9999L, 10000000L, 0L)]
    [DataRow(0L, 19999L, 10000000L, 1L)]
    [DataRow(20L, 10L, 1000L, 0L)]
    [DataRow(long.MinValue, long.MaxValue, 1L, long.MaxValue)]
    [DataRow(0L, 10L, 0L, 0L)]
    public void Duration_TruncatesClampsAndSaturates(long start, long end, long frequency, long expected)
    {
        Assert.AreEqual(expected, LifecycleElapsed.Milliseconds(new Clock(frequency), start, end));
    }

    [TestMethod]
    public void Duration_UsesOnlyMonotonicTimeAndToleratesProviderFailures()
    {
        var clock = new Clock(1000);
        long? before = LifecycleElapsed.Timestamp(clock);
        clock.Current = 24;
        Assert.AreEqual(24L, LifecycleElapsed.Milliseconds(clock, before, LifecycleElapsed.Timestamp(clock)));
        clock.ThrowTimestamp = true;
        Assert.IsNull(LifecycleElapsed.Timestamp(clock));
        Assert.AreEqual(0L, LifecycleElapsed.Milliseconds(clock, null, 24));
        clock.ThrowFrequency = true;
        Assert.AreEqual(0L, LifecycleElapsed.Milliseconds(clock, 0, 24));
    }

    private sealed class Clock(long frequency) : TimeProvider
    {
        internal long Current { get; set; }
        internal bool ThrowTimestamp { get; set; }
        internal bool ThrowFrequency { get; set; }
        public override long TimestampFrequency => ThrowFrequency ? throw new InvalidOperationException("secret-frequency") : frequency;
        public override long GetTimestamp() => ThrowTimestamp ? throw new InvalidOperationException("secret-time") : Current;
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Wall clock must never be used for durations.");
    }
}
