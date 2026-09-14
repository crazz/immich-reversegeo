using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.ChildWorkerLaunching;

public sealed partial class ChildWorkerLaunchingTests
{
    private const string ReadyObjectLiteral = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"ready\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":null,\"payload\":{}}";
    private static readonly byte[] DrainSentinel = [0x53, 0x54, 0x44, 0x45, 0x52, 0x52];

    [TestMethod]
    [DataRow("literal-lf", "\n")]
    [DataRow("literal-crlf", "\r\n")]
    public async Task Framing_ValidLiteralDelimiter_AcceptsReadyWithExactFinalityAndDrain(string label, string delimiter)
    {
        var bytes = Encoding.UTF8.GetBytes(ReadyObjectLiteral + delimiter);
        var (process, sink, session) = await LaunchByteSessionAsync(900);

        process.StandardOutput.Write(bytes);
        var startup = await session.Startup;
        process.StandardError.Write(DrainSentinel.ToArray());
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(startup, $"{label}: exact-startup");
        Assert.AreEqual(1, sink.AcceptCalls, $"{label}: exact-callback-count");
        Assert.AreEqual(WorkerProtocolV1.ReadyType, sink.Events[0].Type, $"{label}: exact-callback-type");
        Assert.IsNull(completion.FirstProtocolObservation, $"{label}: no-first-failure");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, $"{label}: stdout-finality");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, $"{label}: stderr-finality");
        CollectionAssert.AreEqual(DrainSentinel, completion.StandardErrorTail.Bytes.ToArray(), $"{label}: independent-drain");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Framing_CrlfAndDelimiterSplitAcrossReads_AcceptsReadyExactlyOnce()
    {
        var objectBytes = Encoding.UTF8.GetBytes(ReadyObjectLiteral);
        var (process, sink, session) = await LaunchByteSessionAsync(901);

        await process.StandardOutput.FeedAsync(objectBytes, 37);
        await process.StandardOutput.WriteAsyncForTest([(byte)'\r']);
        await process.StandardOutput.WriteAsyncForTest([(byte)'\n']);
        await sink.FirstAccepted;
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup, "crlf-split: exact-startup");
        process.StandardError.Write(DrainSentinel.ToArray());
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        Assert.AreEqual(1, sink.AcceptCalls, "crlf-split: exact-callback-count");
        Assert.IsNull(completion.FirstProtocolObservation, "crlf-split: no-first-failure");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, "crlf-split: stdout-finality");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, "crlf-split: stderr-finality");
        CollectionAssert.AreEqual(DrainSentinel, completion.StandardErrorTail.Bytes.ToArray(), "crlf-split: drain-sentinel");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Framing_OneByteChunks_AcceptsLiteralReadyExactlyOnce()
    {
        var frame = Encoding.UTF8.GetBytes(ReadyObjectLiteral + "\n");
        var (process, sink, session) = await LaunchByteSessionAsync(902);

        await process.StandardOutput.FeedAsync(frame, 1);
        await sink.FirstAccepted;
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup, "one-byte: exact-startup");
        process.StandardError.Write(DrainSentinel.ToArray());
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        Assert.AreEqual(frame.Length, process.StandardOutput.ConsumedBytes, "one-byte: every-literal-byte-consumed");
        Assert.AreEqual(1, sink.AcceptCalls, "one-byte: exact-callback-count");
        Assert.IsNull(completion.FirstProtocolObservation, "one-byte: no-first-failure");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, "one-byte: stdout-finality");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, "one-byte: stderr-finality");
        CollectionAssert.AreEqual(DrainSentinel, completion.StandardErrorTail.Bytes.ToArray(), "one-byte: drain-sentinel");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Framing_MultipleFramesInOneRead_DeliversCompleteLifecycleInOrder()
    {
        var request = CreateRequest();
        var literal = ReadyFrame() + RunStartedFrame(request.RunId) + EligibilityFrame(request.RunId) + CompletedFrame(request.RunId);
        var (process, sink, session) = await LaunchByteSessionAsync(903, request);

        process.StandardOutput.Write(Encoding.UTF8.GetBytes(literal));
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup, "multiple-one-read: exact-startup");
        process.StandardError.Write(DrainSentinel.ToArray());
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        CollectionAssert.AreEqual(
            new[] { WorkerProtocolV1.ReadyType, WorkerProtocolV1.RunStartedType, WorkerProtocolV1.EligibilityDeterminedType, WorkerProtocolV1.CompletedType },
            sink.Events.Select(@event => @event.Type).ToArray(),
            "multiple-one-read: exact-callback-order");
        Assert.AreEqual(1, sink.MaximumConcurrency, "multiple-one-read: callbacks-serialized");
        Assert.AreSame(sink.Events[3], completion.Terminal, "multiple-one-read: exact-terminal-reference");
        Assert.IsNull(completion.FirstProtocolObservation, "multiple-one-read: no-first-failure");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, "multiple-one-read: stdout-finality");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, "multiple-one-read: stderr-finality");
        CollectionAssert.AreEqual(DrainSentinel, completion.StandardErrorTail.Bytes.ToArray(), "multiple-one-read: drain-sentinel");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Framing_MultibyteUtf8SplitAtEveryScalarBoundary_RemainsValid()
    {
        const string literal = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"ready\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":null,\"note\":\"¢€😀\",\"payload\":{}}\n";
        var bytes = Encoding.UTF8.GetBytes(literal);
        var marker = Encoding.UTF8.GetBytes("¢€😀");
        var markerOffset = FindSubsequence(bytes, marker);
        var chunks = new List<byte[]>();
        chunks.Add(bytes[..(markerOffset + 1)]);
        chunks.Add(bytes[(markerOffset + 1)..(markerOffset + 3)]);
        chunks.Add(bytes[(markerOffset + 3)..(markerOffset + 6)]);
        chunks.Add(bytes[(markerOffset + 6)..(markerOffset + 9)]);
        chunks.Add(bytes[(markerOffset + 9)..]);
        var (process, sink, session) = await LaunchByteSessionAsync(904);

        foreach (var chunk in chunks)
        {
            await process.StandardOutput.WriteAsyncForTest(chunk);
        }

        await sink.FirstAccepted;
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup, "multibyte-boundaries: exact-startup");
        process.StandardError.Write(DrainSentinel.ToArray());
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        Assert.AreEqual(bytes.Length, process.StandardOutput.ConsumedBytes, "multibyte-boundaries: every-byte-consumed");
        Assert.AreEqual(1, sink.AcceptCalls, "multibyte-boundaries: ready-delivered-once");
        Assert.IsNull(completion.FirstProtocolObservation, "multibyte-boundaries: no-first-failure");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, "multibyte-boundaries: stdout-finality");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, "multibyte-boundaries: stderr-finality");
        CollectionAssert.AreEqual(DrainSentinel, completion.StandardErrorTail.Bytes.ToArray(), "multibyte-boundaries: drain-sentinel");
        await session.DisposeAsync();
    }

    [TestMethod]
    [DataRow("utf8-bom", "bom", WorkerProtocolFailureCode.InvalidEncoding, null)]
    [DataRow("invalid-leading-byte", "leading", WorkerProtocolFailureCode.InvalidEncoding, null)]
    [DataRow("invalid-continuation-at-delimiter", "continuation", WorkerProtocolFailureCode.InvalidEncoding, null)]
    [DataRow("truncated-utf8-at-delimiter", "truncated-delimiter", WorkerProtocolFailureCode.InvalidEncoding, null)]
    [DataRow("truncated-utf8-at-eof", "truncated-eof", WorkerProtocolFailureCode.InvalidEncoding, null)]
    [DataRow("unterminated-frame-at-eof", "unterminated", WorkerProtocolFailureCode.InvalidFraming, null)]
    [DataRow("bare-cr", "bare-cr", WorkerProtocolFailureCode.InvalidFraming, null)]
    [DataRow("empty-crlf-frame", "empty-crlf", WorkerProtocolFailureCode.InvalidFraming, "The standard-output frame was invalid.")]
    [DataRow("empty-lf-frame", "empty-lf", WorkerProtocolFailureCode.InvalidFraming, "The standard-output frame was invalid.")]
    [DataRow("malformed-json", "malformed", WorkerProtocolFailureCode.MalformedJson, null)]
    public async Task Framing_InvalidLiteralBytes_RetainsExactFirstFactAndDrains(
        string label,
        string fixture,
        WorkerProtocolFailureCode expectedCode,
        string? expectedDiagnostic)
    {
        byte[] bytes = fixture switch
        {
            "bom" => [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(ReadyObjectLiteral), (byte)'\n'],
            "leading" => [0x80, (byte)'\n'],
            "continuation" => [0xc2, 0x20, (byte)'\n'],
            "truncated-delimiter" => [0xe2, 0x82, (byte)'\n'],
            "truncated-eof" => [0xe2, 0x82],
            "unterminated" => Encoding.UTF8.GetBytes(ReadyObjectLiteral),
            "bare-cr" => [(byte)'{', (byte)'\r', (byte)'X', (byte)'\n'],
            "empty-crlf" => [(byte)'\r', (byte)'\n'],
            "empty-lf" => [(byte)'\n'],
            "malformed" => [(byte)'{', (byte)'\n'],
            _ => throw new InvalidOperationException(label)
        };
        var (process, sink, session) = await LaunchByteSessionAsync(905);

        process.StandardOutput.Write(bytes);
        process.StandardOutput.Complete();
        var startup = await session.Startup;
        process.StandardError.Write(DrainSentinel.ToArray());
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        var first = AssertPreReadyProtocolFailure(label, startup, completion, expectedCode);
        if (expectedDiagnostic is not null)
        {
            Assert.AreEqual(expectedDiagnostic, first.Diagnostic, $"{label}: exact-stable-diagnostic");
        }

        Assert.AreEqual(0, sink.AcceptCalls, $"{label}: invalid-never-delivered");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, $"{label}: stdout-finality");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, $"{label}: stderr-finality");
        CollectionAssert.AreEqual(DrainSentinel, completion.StandardErrorTail.Bytes.ToArray(), $"{label}: independent-drain");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Framing_Exact1048576ByteValidObjectPlusCrlf_IsAccepted()
    {
        var objectBytes = CreateExactLengthReadyObject(1_048_576);
        var frame = new byte[1_048_578];
        objectBytes.CopyTo(frame, 0);
        frame[^2] = (byte)'\r';
        frame[^1] = (byte)'\n';
        var (process, sink, session) = await LaunchByteSessionAsync(906);

        await process.StandardOutput.FeedAsync(frame, 16_384);
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup, "exact-limit-plus-crlf: exact-startup");
        process.StandardError.Write(DrainSentinel.ToArray());
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        Assert.AreEqual(1_048_576, objectBytes.Length, "exact-limit-plus-crlf: independent-object-byte-count");
        Assert.AreEqual(1_048_578, process.StandardOutput.ConsumedBytes, "exact-limit-plus-crlf: independent-delimiter-bytes-consumed");
        Assert.AreEqual(1, sink.AcceptCalls, "exact-limit-plus-crlf: accepted-once");
        Assert.IsNull(completion.FirstProtocolObservation, "exact-limit-plus-crlf: no-protocol-failure");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, "exact-limit-plus-crlf: stdout-finality");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, "exact-limit-plus-crlf: stderr-finality");
        CollectionAssert.AreEqual(DrainSentinel, completion.StandardErrorTail.Bytes.ToArray(), "exact-limit-plus-crlf: drain-sentinel");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Framing_Exact1048576ByteValidObjectPlusLf_IsAccepted()
    {
        const int objectByteLength = 1_048_576;
        var literalPrefix = Encoding.UTF8.GetBytes(ReadyObjectLiteral[..^1]);
        var objectBytes = new byte[objectByteLength];
        literalPrefix.CopyTo(objectBytes, 0);
        objectBytes.AsSpan(literalPrefix.Length, objectByteLength - literalPrefix.Length - 1).Fill((byte)' ');
        objectBytes[^1] = (byte)'}';
        var frame = new byte[objectByteLength + 1];
        objectBytes.CopyTo(frame, 0);
        frame[^1] = (byte)'\n';
        var (process, sink, session) = await LaunchByteSessionAsync(910);

        await process.StandardOutput.FeedAsync(frame, 16_384);
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup, "exact-limit-plus-lf: exact-startup");
        process.StandardError.Write(DrainSentinel.ToArray());
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        Assert.AreEqual(objectByteLength, objectBytes.Length, "exact-limit-plus-lf: independent-object-byte-count");
        Assert.AreEqual(frame.Length, process.StandardOutput.ConsumedBytes, "exact-limit-plus-lf: exact-bytes-consumed");
        Assert.AreEqual(1, sink.AcceptCalls, "exact-limit-plus-lf: accepted-once");
        Assert.IsNull(completion.FirstProtocolObservation, "exact-limit-plus-lf: no-protocol-failure");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, "exact-limit-plus-lf: stdout-finality");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, "exact-limit-plus-lf: stderr-finality");
        CollectionAssert.AreEqual(DrainSentinel, completion.StandardErrorTail.Bytes.ToArray(), "exact-limit-plus-lf: drain-sentinel");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Framing_1048577ByteObject_IsMessageTooLargeAndNeverDelivered()
    {
        var bytes = Enumerable.Repeat((byte)' ', 1_048_577).Append((byte)'\n').ToArray();
        await AssertOversizedScenarioAsync("limit-plus-one", bytes, 907);
    }

    [TestMethod]
    public async Task Framing_ExactLimitThenCrThenNonLf_IsMessageTooLarge()
    {
        var bytes = Enumerable.Repeat((byte)' ', 1_048_576).Concat([(byte)'\r', (byte)'X', (byte)'\n']).ToArray();
        await AssertOversizedScenarioAsync("max-cr-non-lf", bytes, 908);
    }

    [TestMethod]
    public async Task Framing_OversizedWithoutLf_ContinuesBoundedDrainAndNeverParsesLaterReady()
    {
        var oversized = Enumerable.Repeat((byte)'x', 1_048_577).ToArray();
        var laterReady = Encoding.UTF8.GetBytes("\n" + ReadyObjectLiteral + "\n");
        var (process, sink, session) = await LaunchByteSessionAsync(909);
        var allBytes = oversized.LongLength + laterReady.LongLength;

        var oversizedFeed = process.StandardOutput.FeedAsync(oversized, 8_192);
        await process.StandardOutput.WaitForConsumedAsync(oversized.LongLength);
        await oversizedFeed;
        var laterFeed = process.StandardOutput.FeedAsync(laterReady, 31);
        await process.StandardOutput.WaitForConsumedAsync(allBytes);
        await laterFeed;
        process.StandardOutput.Complete();
        var startup = await session.Startup;
        process.StandardError.Write(DrainSentinel.ToArray());
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        AssertPreReadyProtocolFailure("oversized-no-lf", startup, completion, WorkerProtocolFailureCode.MessageTooLarge);
        Assert.AreEqual(allBytes, process.StandardOutput.ConsumedBytes, "oversized-no-lf: explicit-full-consumption-barrier");
        Assert.AreEqual(0, sink.AcceptCalls, "oversized-no-lf: later-ready-never-parsed");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, "oversized-no-lf: stdout-finality");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, "oversized-no-lf: stderr-finality");
        CollectionAssert.AreEqual(DrainSentinel, completion.StandardErrorTail.Bytes.ToArray(), "oversized-no-lf: stderr-drained");
        await session.DisposeAsync();
    }

    private static WorkerProtocolFailure AssertPreReadyProtocolFailure(
        string label,
        ChildWorkerStartupObservation startup,
        ChildWorkerCompletionObservation completion,
        WorkerProtocolFailureCode expectedCode)
    {
        var startupFailure = Assert.IsInstanceOfType<ChildWorkerStartupObservation.ProtocolFailure>(startup, $"{label}: startup-is-protocol-failure");
        var firstObservation = Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(completion.FirstProtocolObservation, $"{label}: exact-first-fact");
        Assert.AreEqual(expectedCode, startupFailure.Failure.Code, $"{label}: startup-failure-code");
        Assert.AreEqual(expectedCode, firstObservation.Failure.Code, $"{label}: completion-failure-code");
        Assert.AreSame(startupFailure.Failure, firstObservation.Failure, $"{label}: startup-and-completion-share-exact-failure");
        return startupFailure.Failure;
    }

    private static async Task AssertOversizedScenarioAsync(string label, byte[] bytes, int processId)
    {
        var (process, sink, session) = await LaunchByteSessionAsync(processId);
        await process.StandardOutput.FeedAsync(bytes, 16_384);
        await process.StandardOutput.WaitForConsumedAsync(bytes.LongLength);
        process.StandardOutput.Complete();
        var startup = await session.Startup;
        process.StandardError.Write(DrainSentinel.ToArray());
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        AssertPreReadyProtocolFailure(label, startup, completion, WorkerProtocolFailureCode.MessageTooLarge);
        Assert.AreEqual(bytes.LongLength, process.StandardOutput.ConsumedBytes, $"{label}: every-byte-drained");
        Assert.AreEqual(0, sink.AcceptCalls, $"{label}: invalid-never-delivered");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, $"{label}: stdout-finality");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, $"{label}: stderr-finality");
        CollectionAssert.AreEqual(DrainSentinel, completion.StandardErrorTail.Bytes.ToArray(), $"{label}: stderr-drained");
        await session.DisposeAsync();
    }

    private static byte[] CreateExactLengthReadyObject(int exactLength)
    {
        var prefix = Encoding.UTF8.GetBytes(ReadyObjectLiteral[..^1]);
        var result = new byte[exactLength];
        prefix.CopyTo(result, 0);
        result.AsSpan(prefix.Length, exactLength - prefix.Length - 1).Fill((byte)' ');
        result[^1] = (byte)'}';
        return result;
    }

    private static int FindSubsequence(byte[] source, byte[] value)
    {
        for (var index = 0; index <= source.Length - value.Length; index++)
        {
            if (source.AsSpan(index, value.Length).SequenceEqual(value))
            {
                return index;
            }
        }

        throw new InvalidOperationException("Literal scalar marker was not found.");
    }

    private static async Task<(ByteProcess Process, RecordingSink Sink, ChildWorkerSession Session)> LaunchByteSessionAsync(
        int processId,
        ImmichReverseGeo.Core.Models.ProcessingRunRequest? request = null,
        RecordingSink? sink = null,
        ChildWorkerLauncherOptions? options = null)
    {
        var process = new ByteProcess(processId);
        var recordingSink = sink ?? new RecordingSink();
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), request ?? CreateRequest(), recordingSink, options ?? TestOptions(), CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, $"process-{processId}: started-session").Session;
        await Task.WhenAll(process.StandardOutput.ReadStarted.Task, process.StandardError.ReadStarted.Task, process.WaitStarted.Task);
        return (process, recordingSink, session);
    }
}
