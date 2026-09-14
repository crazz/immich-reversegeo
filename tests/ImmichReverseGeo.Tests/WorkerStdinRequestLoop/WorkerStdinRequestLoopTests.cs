using System.Text;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.WorkerHost;
using ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop;

namespace ImmichReverseGeo.Tests.WorkerStdinRequestLoop;

[TestClass]
public sealed class WorkerStdinRequestLoopTests
{
    private const int ExpectedMaxObjectBytes = 1_048_576;
    private const string RunIdText = "84b94b54-b7c5-49cf-a7dd-2b4a006ba1ae";
    private const string Timestamp = "2026-01-02T03:04:05.0000000Z";
    private static readonly byte[] ExecuteFrame = Utf8("{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-01-02T03:04:05.0000000Z\",\"runId\":\"84b94b54-b7c5-49cf-a7dd-2b4a006ba1ae\",\"payload\":{\"trigger\":\"run-once\"}}");
    private static readonly byte[] CancelFrame = Utf8("{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"control\",\"type\":\"cancel\",\"sequence\":2,\"timestampUtc\":\"2026-01-02T03:04:05.0000000Z\",\"runId\":\"84b94b54-b7c5-49cf-a7dd-2b4a006ba1ae\",\"payload\":{}}");

    [TestMethod]
    public async Task Reader_AllByteBoundariesAcceptCanonicalExecuteAndCancel()
    {
        foreach (var frame in new[] { ExecuteFrame, CancelFrame })
        {
            for (var split = 0; split <= frame.Length; split++)
            {
                var input = new ChunkedStream(frame.Concat("\r\n"u8.ToArray()).ToArray(), [Math.Max(1, split), 1, 3, 2, 5]);
                await using var reader = new WorkerStdinFrameReader(input);
                var result = await reader.ReadAsync(CancellationToken.None);
                AssertSuccess(result, frame, "stdin-byte-boundary-" + frame.Length + "-" + split);
            }
        }
    }

    [TestMethod]
    public async Task Reader_HandlesMultibyteCrLfAndIrregularChunks()
    {
        var frame = Utf8("{\"text\":\"é雪\"}");
        var crIndex = frame.Length;
        var input = new ChunkedStream(frame.Concat("\r\n"u8.ToArray()).ToArray(), [2, 1, 7, 3, 1, 11, crIndex, 1, 4]);
        await using var reader = new WorkerStdinFrameReader(input);
        var result = await reader.ReadAsync(CancellationToken.None);
        AssertSuccess(result, frame, "stdin-irregular-multibyte-crlf");
    }

    [TestMethod]
    public async Task Reader_PreservesAllSameReadSuffixFrames()
    {
        var input = new ChunkedStream(ExecuteFrame.Concat("\n"u8.ToArray()).Concat(CancelFrame).Concat("\n{}\n"u8.ToArray()).ToArray(), [4096]);
        await using var reader = new WorkerStdinFrameReader(input);
        var first = await reader.ReadAsync(CancellationToken.None);
        var second = await reader.ReadAsync(CancellationToken.None);
        var third = await reader.ReadAsync(CancellationToken.None);
        AssertSuccess(first, ExecuteFrame, "stdin-suffix-execute");
        AssertSuccess(second, CancelFrame, "stdin-suffix-cancel");
        AssertSuccess(third, "{}"u8.ToArray(), "stdin-suffix-third");
        Assert.AreEqual(1, input.ReadCount, "stdin-suffix-one-read");
    }

    [TestMethod]
    public async Task Reader_RejectsOneDefectFramingAndEncodingRows()
    {
        var rows = new (string Label, byte[] Bytes, WorkerProtocolFailureCode Code)[]
        {
            ("empty-lf", "\n"u8.ToArray(), WorkerProtocolFailureCode.InvalidFraming),
            ("empty-crlf", "\r\n"u8.ToArray(), WorkerProtocolFailureCode.InvalidFraming),
            ("bare-cr", "{}\rX\n"u8.ToArray(), WorkerProtocolFailureCode.InvalidFraming),
            ("embedded-cr", "{\r}\n"u8.ToArray(), WorkerProtocolFailureCode.InvalidFraming),
            ("bom", new byte[] { 0xef, 0xbb, 0xbf, (byte)'{', (byte)'}', (byte)'\n' }, WorkerProtocolFailureCode.InvalidEncoding),
            ("invalid-utf8", new byte[] { (byte)'{', 0xc3, (byte)'}', (byte)'\n' }, WorkerProtocolFailureCode.InvalidEncoding),
            ("truncated-utf8-lf", new byte[] { (byte)'{', 0xe2, (byte)'\n' }, WorkerProtocolFailureCode.InvalidEncoding),
            ("truncated-utf8-eof", new byte[] { (byte)'{', 0xe2 }, WorkerProtocolFailureCode.InvalidEncoding)
        };

        foreach (var row in rows)
        {
            await using var reader = new WorkerStdinFrameReader(new ChunkedStream(row.Bytes, [1, 4, 2]));
            var result = await reader.ReadAsync(CancellationToken.None);
            AssertFailure(result, row.Code, "stdin-negative-" + row.Label);
        }
    }

    [TestMethod]
    public async Task Reader_UsesByteLimitIncludingMultibyteAndPendingCr()
    {
#pragma warning disable MSTEST0032
        Assert.AreEqual(ExpectedMaxObjectBytes, WorkerProtocolV1.MaxMessageBytes, "stdin-limit-production-constant");
#pragma warning restore MSTEST0032
        var prefix = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-01-02T03:04:05.0000000Z\",\"runId\":\"84b94b54-b7c5-49cf-a7dd-2b4a006ba1ae\",\"payload\":{\"trigger\":\"run-once\",\"padding\":\"";
        var suffix = "\"}}";
        var exact = Encoding.ASCII.GetBytes(prefix + new string('x', ExpectedMaxObjectBytes - Encoding.ASCII.GetByteCount(prefix) - Encoding.ASCII.GetByteCount(suffix)) + suffix);
        Assert.AreEqual(ExpectedMaxObjectBytes, exact.Length, "stdin-limit-handcrafted-exact-length");
        await using var exactReader = new WorkerStdinFrameReader(new ChunkedStream(exact.Concat("\r\n"u8.ToArray()).ToArray(), [4096]));
        var exactResult = await exactReader.ReadAsync(CancellationToken.None);
        AssertSuccess(exactResult, exact, "stdin-limit-exact-success");
        Assert.IsTrue(WorkerProtocolCodec.ParseControllerInput(exactResult.Frame!).IsSuccess, "stdin-limit-exact-codec");

        var over = exact.Concat(new byte[] { (byte)'x' }).ToArray();
        var guarded = new ChunkedStream(over, [4096], throwOnReadAfter: 257);
        await using var overflowReader = new WorkerStdinFrameReader(guarded);
        var overflow = await overflowReader.ReadAsync(CancellationToken.None);
        AssertFailure(overflow, WorkerProtocolFailureCode.MessageTooLarge, "stdin-limit-overflow");
        Assert.AreEqual(257, guarded.ReadCount, "stdin-limit-overflow-no-further-read");

        await using var pendingCrReader = new WorkerStdinFrameReader(new ChunkedStream(exact.Concat("\r\n"u8.ToArray()).ToArray(), [4096]));
        AssertSuccess(await pendingCrReader.ReadAsync(CancellationToken.None), exact, "stdin-limit-pending-cr-eligible");
    }

    [TestMethod]
    public async Task Reader_DistinguishesCompleteEofPartialEofAndReaderFault()
    {
        await using var cleanReader = new WorkerStdinFrameReader(new ChunkedStream([], [1]));
        AssertEof(await cleanReader.ReadAsync(CancellationToken.None), "stdin-clean-eof");
        await using var partialReader = new WorkerStdinFrameReader(new ChunkedStream("{"u8.ToArray(), [1]));
        AssertFailure(await partialReader.ReadAsync(CancellationToken.None), WorkerProtocolFailureCode.InvalidFraming, "stdin-partial-eof");
        await using var faultReader = new WorkerStdinFrameReader(new ChunkedStream([], [1], throwOnReadAfter: 0));
        AssertReaderFailure(await faultReader.ReadAsync(CancellationToken.None), "stdin-reader-failure");
    }

    [TestMethod]
    public async Task Source_AcquiresExactlyOnceWithAllTriggersAndStableLeaseReference()
    {
        foreach (var trigger in new[] { "manual", "scheduled", "run-once" })
        {
            var frame = Execute(trigger);
            var factory = new CountingInputFactory(new ChunkedStream(frame.Concat("\n"u8.ToArray()).ToArray(), [4096]));
            await using var source = new WorkerStdinRequestSource(factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkerStdinRequestSource>.Instance);
            Assert.AreEqual(0, factory.OpenCount, "stdin-source-lazy-" + trigger);
            var first = await source.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            var second = await source.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            var accepted = first as InitialProcessingRunAcquisition.Accepted;
            var repeated = second as InitialProcessingRunAcquisition.Accepted;
            Assert.IsNotNull(accepted, "stdin-source-accepted-" + trigger);
            Assert.IsNotNull(repeated, "stdin-source-repeated-accepted-" + trigger);
            Assert.AreSame(accepted.Lease, repeated.Lease, "stdin-source-one-lease-" + trigger);
            Assert.AreSame(accepted.Lease.Request, repeated.Lease.Request, "stdin-source-request-reference-" + trigger);
            Assert.AreEqual(Guid.Parse(RunIdText), accepted.Lease.Request.RunId, "stdin-source-run-id-" + trigger);
            Assert.AreEqual(ParseTrigger(trigger), accepted.Lease.Request.Trigger, "stdin-source-trigger-" + trigger);
            Assert.AreEqual(1, factory.OpenCount, "stdin-source-one-open-" + trigger);
        }
    }

    [TestMethod]
    public async Task Source_FailsClosedForPreRequestRows()
    {
        var rows = new (string Label, byte[] Frame)[]
        {
            ("malformed", "{\n"u8.ToArray()),
            ("cancel-first", CancelFrame.Concat("\n"u8.ToArray()).ToArray()),
            ("sequence-gap", Utf8("{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":2,\"timestampUtc\":\"2026-01-02T03:04:05.0000000Z\",\"runId\":\"84b94b54-b7c5-49cf-a7dd-2b4a006ba1ae\",\"payload\":{\"trigger\":\"run-once\"}}\n")),
            ("unsupported-direction", Utf8("{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-01-02T03:04:05.0000000Z\",\"runId\":\"84b94b54-b7c5-49cf-a7dd-2b4a006ba1ae\",\"payload\":{\"trigger\":\"run-once\"}}\n"))
        };
        foreach (var row in rows)
        {
            await using var source = new WorkerStdinRequestSource(
                new CountingInputFactory(new ChunkedStream(row.Frame, [4096])),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkerStdinRequestSource>.Instance);
            var result = await source.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            var failure = result as InitialProcessingRunAcquisition.PreRequestFailure;
            Assert.IsNotNull(failure, "stdin-source-failure-" + row.Label);
            Assert.IsFalse(result is InitialProcessingRunAcquisition.Accepted, "stdin-source-no-lease-" + row.Label);
        }
    }

    private static byte[] Execute(string trigger)
    {
        return Utf8($"{{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"{Timestamp}\",\"runId\":\"{RunIdText}\",\"payload\":{{\"trigger\":\"{trigger}\"}}}}");
    }

    private static byte[] Utf8(string value)
    {
        return Encoding.UTF8.GetBytes(value);
    }

    private static ProcessingRunTrigger ParseTrigger(string value)
    {
        return value switch
        {
            "manual" => ProcessingRunTrigger.Manual,
            "scheduled" => ProcessingRunTrigger.Scheduled,
            _ => ProcessingRunTrigger.RunOnce
        };
    }

    private static void AssertSuccess(WorkerStdinFrameReadResult result, byte[] expected, string label)
    {
        Assert.IsTrue(result.IsSuccess, label + ":success");
        Assert.IsNotNull(result.Frame, label + ":frame");
        CollectionAssert.AreEqual(expected, result.Frame, label + ":bytes");
        Assert.IsNull(result.FailureCode, label + ":no-failure");
        Assert.IsFalse(result.IsEndOfInput, label + ":no-eof");
        Assert.IsFalse(result.IsReaderFailure, label + ":no-reader-failure");
    }

    private static void AssertFailure(WorkerStdinFrameReadResult result, WorkerProtocolFailureCode code, string label)
    {
        Assert.IsFalse(result.IsSuccess, label + ":not-success");
        Assert.IsNull(result.Frame, label + ":no-frame");
        Assert.AreEqual(code, result.FailureCode, label + ":code");
        Assert.IsFalse(result.IsEndOfInput, label + ":no-eof");
        Assert.IsFalse(result.IsReaderFailure, label + ":no-reader-failure");
    }

    private static void AssertEof(WorkerStdinFrameReadResult result, string label)
    {
        Assert.IsFalse(result.IsSuccess, label + ":not-success");
        Assert.IsNull(result.Frame, label + ":no-frame");
        Assert.IsNull(result.FailureCode, label + ":no-failure");
        Assert.IsTrue(result.IsEndOfInput, label + ":eof");
        Assert.IsFalse(result.IsReaderFailure, label + ":no-reader-failure");
    }

    private static void AssertReaderFailure(WorkerStdinFrameReadResult result, string label)
    {
        Assert.IsFalse(result.IsSuccess, label + ":not-success");
        Assert.IsNull(result.Frame, label + ":no-frame");
        Assert.IsNull(result.FailureCode, label + ":no-code");
        Assert.IsFalse(result.IsEndOfInput, label + ":no-eof");
        Assert.IsTrue(result.IsReaderFailure, label + ":reader-failure");
    }

    private sealed class CountingInputFactory(Stream input) : IWorkerStandardInputStreamFactory
    {
        internal int OpenCount { get; private set; }

        public Stream OpenStandardInput()
        {
            OpenCount++;
            return input;
        }
    }

    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly int[] _chunks;
        private readonly int _throwOnReadAfter;
        private int _offset;
        private int _chunkIndex;

        internal ChunkedStream(byte[] bytes, int[] chunks, int throwOnReadAfter = int.MaxValue)
        {
            _bytes = bytes;
            _chunks = chunks;
            _throwOnReadAfter = throwOnReadAfter;
        }

        internal int ReadCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _bytes.Length;

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadCount >= _throwOnReadAfter)
            {
                throw new InvalidOperationException("read-failure");
            }

            ReadCount++;
            if (_offset == _bytes.Length)
            {
                return ValueTask.FromResult(0);
            }

            var requested = _chunks[Math.Min(_chunkIndex++, _chunks.Length - 1)];
            var length = Math.Min(Math.Min(requested, buffer.Length), _bytes.Length - _offset);
            _bytes.AsSpan(_offset, length).CopyTo(buffer.Span);
            _offset += length;
            return ValueTask.FromResult(length);
        }
    }
}
