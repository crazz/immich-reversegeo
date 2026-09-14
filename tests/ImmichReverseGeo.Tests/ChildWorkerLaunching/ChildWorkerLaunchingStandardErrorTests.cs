using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.ChildWorkerLaunching;

public sealed partial class ChildWorkerLaunchingTests
{
    [TestMethod]
    [DataRow("two-capacities-plus-offset", 131_195, 4_093)]
    [DataRow("three-capacities-multiple-wraps", 196_731, 8_191)]
    public async Task StandardError_MultipleRingWrapsAndReadOffsetsRetainExactSentinelSuffix(
        string label,
        int length,
        int chunkSize)
    {
        var bytes = Enumerable.Range(0, length).Select(index => (byte)((index * 17 + 29) % 251)).ToArray();
        var expected = Enumerable.Range(length - 65_536, 65_536).Select(index => (byte)((index * 17 + 29) % 251)).ToArray();
        var (process, _, session) = await LaunchByteSessionAsync(960);

        var feed = process.StandardError.FeedAsync(bytes, chunkSize);
        await process.StandardError.WaitForConsumedAsync(length);
        await feed;
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        Assert.AreEqual((long)length, completion.StandardErrorTail.TotalBytes, $"{label}: exact-total");
        Assert.IsFalse(completion.StandardErrorTail.TotalBytesSaturated, $"{label}: non-saturated-total");
        Assert.IsTrue(completion.StandardErrorTail.IsTruncated, $"{label}: exact-truncated-boundary");
        Assert.AreEqual(65_536, completion.StandardErrorTail.Bytes.Length, $"{label}: exact-capacity");
        CollectionAssert.AreEqual(expected, completion.StandardErrorTail.Bytes.ToArray(), $"{label}: exact-final-sentinel-suffix");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StandardError_MultibyteUtf8SplitAcrossReadsAndRingWrapRetainsExactBytesAndText()
    {
        var asciiPrefix = Enumerable.Repeat((byte)'x', 65_534).ToArray();
        var euro = new byte[] { 0xe2, 0x82, 0xac };
        var emoji = new byte[] { 0xf0, 0x9f, 0x98, 0x80 };
        var allBytes = asciiPrefix.Concat(euro).Concat(emoji).ToArray();
        var expectedBytes = Enumerable.Repeat((byte)'x', 65_529).Concat(euro).Concat(emoji).ToArray();
        var expectedText = new string('x', 65_529) + "€😀";
        var (process, _, session) = await LaunchByteSessionAsync(961);

        await process.StandardError.FeedAsync(asciiPrefix, 4_095);
        await process.StandardError.WriteAsyncForTest([0xe2]);
        await process.StandardError.WriteAsyncForTest([0x82]);
        await process.StandardError.WriteAsyncForTest([0xac, 0xf0]);
        await process.StandardError.WriteAsyncForTest([0x9f, 0x98]);
        await process.StandardError.WriteAsyncForTest([0x80]);
        await process.StandardError.WaitForConsumedAsync(allBytes.LongLength);
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        Assert.AreEqual(65_541L, completion.StandardErrorTail.TotalBytes, "stderr-multibyte-wrap: exact-total");
        Assert.IsTrue(completion.StandardErrorTail.IsTruncated, "stderr-multibyte-wrap: truncated");
        CollectionAssert.AreEqual(expectedBytes, completion.StandardErrorTail.Bytes.ToArray(), "stderr-multibyte-wrap: exact-final-bytes");
        Assert.AreEqual(expectedText, completion.StandardErrorTail.Text, "stderr-multibyte-wrap: exact-text");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StandardError_TailBeginningMidScalarUsesExactReplacementText()
    {
        var trailingAscii = Enumerable.Repeat((byte)'q', 65_534).ToArray();
        var allBytes = new byte[] { (byte)'p', 0xe2, 0x82, 0xac }.Concat(trailingAscii).ToArray();
        var expectedBytes = new byte[] { 0x82, 0xac }.Concat(trailingAscii).ToArray();
        var expectedText = "��" + new string('q', 65_534);
        var (process, _, session) = await LaunchByteSessionAsync(962);

        await process.StandardError.FeedAsync(allBytes, 4_097);
        await process.StandardError.WaitForConsumedAsync(allBytes.LongLength);
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        Assert.AreEqual(65_538L, completion.StandardErrorTail.TotalBytes, "stderr-mid-scalar: exact-total");
        Assert.IsTrue(completion.StandardErrorTail.IsTruncated, "stderr-mid-scalar: truncated");
        CollectionAssert.AreEqual(expectedBytes, completion.StandardErrorTail.Bytes.ToArray(), "stderr-mid-scalar: exact-tail-bytes");
        Assert.AreEqual(expectedText, completion.StandardErrorTail.Text, "stderr-mid-scalar: exact-replacement-text");
        await session.DisposeAsync();
    }

    [TestMethod]
    [DataRow("invalid-utf8", "f�g")]
    [DataRow("trailing-partial-utf8", "f�")]
    public async Task StandardError_InvalidUtf8UsesExactReplacementText(string label, string expectedText)
    {
        var bytes = label == "invalid-utf8"
            ? new byte[] { (byte)'f', 0x80, (byte)'g' }
            : new byte[] { (byte)'f', 0xe2, 0x82 };
        var (process, _, session) = await LaunchByteSessionAsync(963);

        await process.StandardError.FeedAsync(bytes, 1);
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        CollectionAssert.AreEqual(bytes, completion.StandardErrorTail.Bytes.ToArray(), $"{label}: exact-raw-bytes");
        Assert.AreEqual(expectedText, completion.StandardErrorTail.Text, $"{label}: exact-replacement-text");
        Assert.IsFalse(completion.StandardErrorTail.IsTruncated, $"{label}: not-truncated");
        Assert.IsFalse(completion.StandardErrorTail.TotalBytesSaturated, $"{label}: not-saturated");
        await session.DisposeAsync();
    }

    [TestMethod]
    public void StandardErrorTail_SaturatedMetadataRemainsExact()
    {
        var count = new SaturatingByteCount(long.MaxValue - 2, false);

        count = count.Add(1);
        Assert.AreEqual(long.MaxValue - 1, count.TotalBytes, "saturating-count: max-minus-two-plus-one-total");
        Assert.IsFalse(count.IsSaturated, "saturating-count: max-minus-two-plus-one-not-saturated");
        Assert.IsTrue(count.IsTruncated(65_536), "saturating-count: max-minus-two-plus-one-truncated");

        count = count.Add(1);
        Assert.AreEqual(long.MaxValue, count.TotalBytes, "saturating-count: max-minus-one-plus-one-total");
        Assert.IsFalse(count.IsSaturated, "saturating-count: exact-max-not-saturated");
        Assert.IsTrue(count.IsTruncated(65_536), "saturating-count: exact-max-truncated");

        count = count.Add(1);
        Assert.AreEqual(long.MaxValue, count.TotalBytes, "saturating-count: overflow-stays-at-max");
        Assert.IsTrue(count.IsSaturated, "saturating-count: overflow-recorded");
        Assert.IsTrue(count.IsTruncated(65_536), "saturating-count: overflow-truncated");

        var retainedSuffix = new byte[] { 0x22, 0x33 };
        var tail = new ChildWorkerStandardErrorTail(
            retainedSuffix,
            count.TotalBytes,
            count.IsSaturated,
            count.IsTruncated(65_536));

        Assert.AreEqual(long.MaxValue, tail.TotalBytes, "saturating-count: exact-long-max-total");
        Assert.IsTrue(tail.TotalBytesSaturated, "saturating-count: saturation-recorded");
        Assert.IsTrue(tail.IsTruncated, "saturating-count: saturation-implies-truncation");
        CollectionAssert.AreEqual(new byte[] { 0x22, 0x33 }, tail.Bytes.ToArray(), "saturating-count: retained-suffix-unchanged");
    }
}
