using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerProtocol;

[TestClass]
public class ControllerInputFramingTests
{
    private const string Execute = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"payload\":{\"trigger\":\"manual\"}}";

    [TestMethod]
    public void ControllerInput_ExactLimitAndOneOverClassifyBeforeJson()
    {
        var prefix = Execute[..^1] + ",\"padding\":\"";
        var exact = prefix + new string('x', WorkerProtocolV1.MaxMessageBytes - System.Text.Encoding.UTF8.GetByteCount(prefix) - 2) + "\"}";
        var oversize = prefix + new string('x', WorkerProtocolV1.MaxMessageBytes - System.Text.Encoding.UTF8.GetByteCount(prefix) - 1) + "\"}";

        var accepted = WorkerProtocolCodec.ParseControllerInput(System.Text.Encoding.UTF8.GetBytes(exact));
        Assert.IsTrue(accepted.IsSuccess, "controller-input:size:exact");
        Assert.IsNotNull(accepted.Message, "controller-input:size:exact");
        Assert.IsNull(accepted.Failure, "controller-input:size:exact");
        Assert.IsNull(accepted.CancelDisposition, "controller-input:size:exact");
        AssertFailure("controller-input:size:one-over", System.Text.Encoding.UTF8.GetBytes(oversize), WorkerProtocolFailureCode.MessageTooLarge);
        AssertFailure("controller-input:size:malformed-over", System.Text.Encoding.UTF8.GetBytes(oversize + "x"), WorkerProtocolFailureCode.MessageTooLarge);
    }

    [TestMethod]
    public void ControllerInput_FramingAndEncodingFailuresAreExplicit()
    {
        AssertAccepted("controller-input:delimiter:lf", System.Text.Encoding.UTF8.GetBytes(Execute + "\n"));
        AssertAccepted("controller-input:delimiter:crlf", System.Text.Encoding.UTF8.GetBytes(Execute + "\r\n"));
        foreach (var row in new (string Label, byte[] Bytes, WorkerProtocolFailureCode Code)[]
        {
            ("controller-input:delimiter:bare-cr", System.Text.Encoding.UTF8.GetBytes(Execute + "\r"), WorkerProtocolFailureCode.InvalidFraming),
            ("controller-input:delimiter:repeated", System.Text.Encoding.UTF8.GetBytes(Execute + "\n\n"), WorkerProtocolFailureCode.InvalidFraming),
            ("controller-input:frame:empty", [], WorkerProtocolFailureCode.InvalidFraming),
            ("controller-input:encoding:bom", [0xef, 0xbb, 0xbf], WorkerProtocolFailureCode.InvalidEncoding),
            ("controller-input:encoding:invalid", [0xff], WorkerProtocolFailureCode.InvalidEncoding),
            ("controller-input:frame:multiple", System.Text.Encoding.UTF8.GetBytes(Execute + Execute), WorkerProtocolFailureCode.MalformedJson)
        })
        {
            AssertFailure(row.Label, row.Bytes, row.Code);
        }
    }

    [TestMethod]
    public void ControllerInput_SizeDelimitersAndMalformedFramesAreClassified()
    {
        foreach (var row in new (string Label, string Padding, string Delimiter)[]
        {
            ("controller-input:size:ascii:none", "x", ""), ("controller-input:size:ascii:lf", "x", "\n"), ("controller-input:size:ascii:crlf", "x", "\r\n"),
            ("controller-input:size:utf8:none", "é", ""), ("controller-input:size:utf8:lf", "é", "\n"), ("controller-input:size:utf8:crlf", "é", "\r\n")
        })
        {
            var exact = SizedFrame(row.Padding, 0) + row.Delimiter;
            AssertAccepted(row.Label + ":exact", System.Text.Encoding.UTF8.GetBytes(exact));
            var over = SizedFrame(row.Padding, 1) + row.Delimiter;
            AssertFailure(row.Label + ":over", System.Text.Encoding.UTF8.GetBytes(over), WorkerProtocolFailureCode.MessageTooLarge);
            AssertFailure(row.Label + ":malformed-over", System.Text.Encoding.UTF8.GetBytes(over + "x"), WorkerProtocolFailureCode.MessageTooLarge);
        }
    }

    [TestMethod]
    public void ControllerInput_RemainingFramingRowsAreExplicit()
    {
        foreach (var row in new (string Label, byte[] Bytes, WorkerProtocolFailureCode Code)[]
        {
            ("controller-input:frame:whitespace", " "u8.ToArray(), WorkerProtocolFailureCode.MalformedJson),
            ("controller-input:encoding:truncated", [0xc3], WorkerProtocolFailureCode.InvalidEncoding),
            ("controller-input:frame:literal-lf", System.Text.Encoding.UTF8.GetBytes(Execute.Replace("manual", "man\nual", StringComparison.Ordinal)), WorkerProtocolFailureCode.InvalidFraming),
            ("controller-input:frame:literal-cr", System.Text.Encoding.UTF8.GetBytes(Execute.Replace("manual", "man\rual", StringComparison.Ordinal)), WorkerProtocolFailureCode.InvalidFraming),
            ("controller-input:json:truncated-object", System.Text.Encoding.UTF8.GetBytes(Execute[..^1]), WorkerProtocolFailureCode.MalformedJson),
            ("controller-input:json:truncated-string", System.Text.Encoding.UTF8.GetBytes(Execute[..^3]), WorkerProtocolFailureCode.MalformedJson),
            ("controller-input:json:truncated-number", System.Text.Encoding.UTF8.GetBytes(Execute.Replace("\"sequence\":1", "\"sequence\":", StringComparison.Ordinal)), WorkerProtocolFailureCode.MalformedJson),
            ("controller-input:json:trailing", System.Text.Encoding.UTF8.GetBytes(Execute + "x"), WorkerProtocolFailureCode.MalformedJson),
            ("controller-input:json:text", "ordinary text"u8.ToArray(), WorkerProtocolFailureCode.MalformedJson),
            ("controller-input:json:prefix", System.Text.Encoding.UTF8.GetBytes("x" + Execute), WorkerProtocolFailureCode.MalformedJson),
            ("controller-input:json:suffix", System.Text.Encoding.UTF8.GetBytes(Execute + "{}"), WorkerProtocolFailureCode.MalformedJson),
            ("controller-input:semantic:unsupported", System.Text.Encoding.UTF8.GetBytes(Execute.Replace("execute", "ping", StringComparison.Ordinal)), WorkerProtocolFailureCode.UnsupportedType)
        }) { AssertFailure(row.Label, row.Bytes, row.Code); }
        AssertAccepted("controller-input:frame:escaped-lf", System.Text.Encoding.UTF8.GetBytes(Execute.Replace("\"payload\"", "\"future\":\"\\n\",\"payload\"", StringComparison.Ordinal)));
        AssertAccepted("controller-input:frame:escaped-cr", System.Text.Encoding.UTF8.GetBytes(Execute.Replace("\"payload\"", "\"future\":\"\\r\",\"payload\"", StringComparison.Ordinal)));
    }

    private static string SizedFrame(string unit, int extraBytes)
    {
        var prefix = Execute[..^1] + ",\"padding\":\"";
        var suffix = "\"}";
        var remaining = WorkerProtocolV1.MaxMessageBytes + extraBytes - System.Text.Encoding.UTF8.GetByteCount(prefix + suffix);
        var count = remaining / System.Text.Encoding.UTF8.GetByteCount(unit);
        var padding = string.Concat(Enumerable.Repeat(unit, count));
        if (System.Text.Encoding.UTF8.GetByteCount(prefix + padding + suffix) != WorkerProtocolV1.MaxMessageBytes + extraBytes)
        {
            padding += "x";
        }

        return prefix + padding + suffix;
    }

    private static void AssertAccepted(string label, byte[] bytes)
    {
        var result = WorkerProtocolCodec.ParseControllerInput(bytes);
        Assert.IsTrue(result.IsSuccess, label);
        Assert.IsNotNull(result.Message, label);
        Assert.IsNull(result.Failure, label);
        Assert.IsNull(result.CancelDisposition, label);
    }

    private static void AssertFailure(string label, byte[] bytes, WorkerProtocolFailureCode code)
    {
        var result = WorkerProtocolCodec.ParseControllerInput(bytes);
        Assert.IsFalse(result.IsSuccess, label);
        Assert.IsNull(result.Message, label);
        Assert.IsNotNull(result.Failure, label);
        Assert.IsNull(result.CancelDisposition, label);
        Assert.AreEqual(code, result.Failure.Code, label);    }
}
