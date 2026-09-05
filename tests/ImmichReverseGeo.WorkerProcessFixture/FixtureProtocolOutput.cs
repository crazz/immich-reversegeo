using System.Text;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.WorkerProcessFixture;

internal sealed class FixtureProtocolOutput
{
    private static readonly byte[] LineFeed = [(byte)'\n'];
    private readonly Stream _output;
    private readonly WorkerProtocolEventStreamValidator _validator = new();

    internal FixtureProtocolOutput(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    internal async Task WriteValidAsync(WorkerProtocolEvent @event)
    {
        var accepted = _validator.Validate(@event);
        if (!accepted.IsSuccess)
        {
            throw new InvalidOperationException($"Fixture generated an invalid event: {accepted.Failure!.Code}.");
        }

        await WriteFrameAsync(WorkerProtocolCodec.Serialize(@event)).ConfigureAwait(false);
    }

    internal async Task WriteFrameAsync(ReadOnlyMemory<byte> frame)
    {
        await _output.WriteAsync(frame).ConfigureAwait(false);
        await _output.WriteAsync(LineFeed).ConfigureAwait(false);
        await _output.FlushAsync().ConfigureAwait(false);
    }

    internal async Task WriteRawAsync(ReadOnlyMemory<byte> bytes)
    {
        await _output.WriteAsync(bytes).ConfigureAwait(false);
        await _output.FlushAsync().ConfigureAwait(false);
    }

    internal async Task WriteOversizedFrameAsync()
    {
        var buffer = new byte[4096];
        Array.Fill(buffer, (byte)'x');
        var remaining = WorkerProtocolV1.MaxMessageBytes + 1;
        while (remaining > 0)
        {
            var count = Math.Min(remaining, buffer.Length);
            await _output.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
            remaining -= count;
        }

        await _output.WriteAsync(LineFeed).ConfigureAwait(false);
        await _output.FlushAsync().ConfigureAwait(false);
    }

    internal void AssertComplete()
    {
        var result = _validator.FinalizeStream();
        if (!result.IsComplete)
        {
            throw new InvalidOperationException($"Fixture generated an incomplete event stream: {result.Failure!.Code}.");
        }
    }

    internal static byte[] MutateUnknown(WorkerProtocolEvent validEvent, UnknownKind kind)
    {
        var json = Encoding.UTF8.GetString(WorkerProtocolCodec.Serialize(validEvent));
        var mutated = kind switch
        {
            UnknownKind.Version => ReplaceExactly(
                json,
                $"\"version\":{WorkerProtocolV1.Version}",
                $"\"version\":{WorkerProtocolV1.Version + 1}"),
            UnknownKind.Category => ReplaceExactly(
                json,
                $"\"category\":\"{WorkerProtocolV1.LifecycleCategory}\"",
                "\"category\":\"future-category\""),
            UnknownKind.Type => ReplaceExactly(
                json,
                $"\"type\":\"{WorkerProtocolV1.RunStartedType}\"",
                "\"type\":\"future-event\""),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        return Encoding.UTF8.GetBytes(mutated);
    }

    private static string ReplaceExactly(string source, string oldValue, string newValue)
    {
        var first = source.IndexOf(oldValue, StringComparison.Ordinal);
        if (first < 0 || source.IndexOf(oldValue, first + oldValue.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException("The shared codec envelope did not contain the expected unique mutation target.");
        }

        return source.Replace(oldValue, newValue, StringComparison.Ordinal);
    }
}
