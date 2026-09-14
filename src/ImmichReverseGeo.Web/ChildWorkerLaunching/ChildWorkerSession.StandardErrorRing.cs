using System;

namespace ImmichReverseGeo.Web.ChildWorkerLaunching;

internal readonly record struct SaturatingByteCount
{
    internal SaturatingByteCount(long totalBytes, bool isSaturated)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
        TotalBytes = totalBytes;
        IsSaturated = isSaturated;
    }

    internal long TotalBytes { get; }
    internal bool IsSaturated { get; }

    internal SaturatingByteCount Add(int addedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(addedBytes);
        if (IsSaturated || addedBytes > long.MaxValue - TotalBytes)
        {
            return new SaturatingByteCount(long.MaxValue, true);
        }

        return new SaturatingByteCount(TotalBytes + addedBytes, false);
    }

    internal bool IsTruncated(int retainedCapacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retainedCapacityBytes);
        return IsSaturated || TotalBytes > retainedCapacityBytes;
    }
}

internal sealed partial class ChildWorkerSession
{
    private const int StandardErrorCapacityBytes = 65_536;

    private sealed class StandardErrorRing
    {
        private readonly byte[] _bytes = new byte[StandardErrorCapacityBytes];
        private int _start;
        private int _count;
        private SaturatingByteCount _total;

        internal void Append(ReadOnlySpan<byte> source)
        {
            _total = _total.Add(source.Length);
            foreach (var value in source)
            {
                if (_count < _bytes.Length)
                {
                    _bytes[(_start + _count) % _bytes.Length] = value;
                    _count++;
                }
                else
                {
                    _bytes[_start] = value;
                    _start = (_start + 1) % _bytes.Length;
                }
            }
        }

        internal ChildWorkerStandardErrorTail Snapshot()
        {
            var result = new byte[_count];
            var first = Math.Min(_count, _bytes.Length - _start);
            Array.Copy(_bytes, _start, result, 0, first);
            Array.Copy(_bytes, 0, result, first, _count - first);
            return new ChildWorkerStandardErrorTail(
                result,
                _total.TotalBytes,
                _total.IsSaturated,
                _total.IsTruncated(StandardErrorCapacityBytes));
        }
    }
}
