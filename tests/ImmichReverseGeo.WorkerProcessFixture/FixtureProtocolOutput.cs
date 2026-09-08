using System.Text;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.WorkerProcessFixture;

internal sealed class FixtureProtocolOutput
{
    private static readonly byte[] LineFeed = [(byte)'\n'];
    private readonly Stream _output;
    private readonly InternalWorkerProtocolVersion _protocolVersion;
    private readonly WorkerProtocolEventStreamValidator? _v1Validator;
    private WorkerJobOutputStreamValidator? _v2Validator;
    private WorkerJobOutputMessage? _v2Ready;

    internal FixtureProtocolOutput(
        Stream output,
        InternalWorkerProtocolVersion protocolVersion)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!Enum.IsDefined(protocolVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        }

        _output = output;
        _protocolVersion = protocolVersion;
        _v1Validator = protocolVersion == InternalWorkerProtocolVersion.V1
            ? new WorkerProtocolEventStreamValidator()
            : null;
    }

    internal async Task WriteValidAsync(WorkerProtocolEvent @event)
    {
        if (_protocolVersion == InternalWorkerProtocolVersion.V1)
        {
            var accepted = _v1Validator!.Validate(@event);
            if (!accepted.IsSuccess)
            {
                throw new InvalidOperationException($"Fixture generated an invalid event: {accepted.Failure!.Code}.");
            }

            await WriteFrameAsync(WorkerProtocolCodec.Serialize(@event)).ConfigureAwait(false);
            return;
        }

        var mapped = MapV2(@event);
        ValidateV2(mapped);
        await WriteFrameAsync(WorkerJobProtocolCodec.Serialize(mapped)).ConfigureAwait(false);
    }

    internal Task WriteReadyAsync()
    {
        if (_protocolVersion == InternalWorkerProtocolVersion.V1)
        {
            return WriteValidAsync(WorkerProtocolMapper.Ready(1, FixtureRunner.ReadyAtUtc));
        }

        return WriteValidAsync(WorkerJobProtocolMapper.Ready(
            1,
            FixtureRunner.ReadyAtUtc,
            new WorkerJobReadyPayload([
                WorkerJobKind.ProcessAssets,
                WorkerJobKind.CoordinateLookup
            ])));
    }

    internal async Task WriteValidAsync(WorkerJobOutputMessage message)
    {
        if (_protocolVersion != InternalWorkerProtocolVersion.V2)
        {
            throw new InvalidOperationException("Typed worker-job output requires protocol v2.");
        }

        ValidateV2(message);
        await WriteFrameAsync(WorkerJobProtocolCodec.Serialize(message)).ConfigureAwait(false);
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
        WorkerProtocolFailure? failure = _protocolVersion switch
        {
            InternalWorkerProtocolVersion.V1 => _v1Validator!.FinalizeStream().Failure,
            InternalWorkerProtocolVersion.V2 => FinalizeV2(),
            _ => throw new InvalidOperationException("The selected protocol version is not supported.")
        };
        if (failure is not null)
        {
            throw new InvalidOperationException($"Fixture generated an incomplete event stream: {failure.Code}.");
        }
    }

    private WorkerProtocolFailure? FinalizeV2()
    {
        return _v2Validator is null
            ? new WorkerProtocolFailure(
                WorkerProtocolFailureCode.InvalidLifecycle,
                "Fixture generated no correlated v2 job output.")
            : _v2Validator.FinalizeOutput(hasPartialFrame: false);
    }

    internal byte[] SerializeMapped(WorkerProtocolEvent validEvent)
    {
        return _protocolVersion switch
        {
            InternalWorkerProtocolVersion.V1 => WorkerProtocolCodec.Serialize(validEvent),
            InternalWorkerProtocolVersion.V2 => WorkerJobProtocolCodec.Serialize(MapV2(validEvent)),
            _ => throw new InvalidOperationException("The selected protocol version is not supported.")
        };
    }

    internal byte[] MutateUnknown(WorkerProtocolEvent validEvent, UnknownKind kind)
    {
        var json = Encoding.UTF8.GetString(SerializeMapped(validEvent));
        var version = (int)_protocolVersion;
        var startedType = _protocolVersion == InternalWorkerProtocolVersion.V1
            ? WorkerProtocolV1.RunStartedType
            : WorkerJobProtocolV2.JobStartedType;
        var mutated = kind switch
        {
            UnknownKind.Version => ReplaceExactly(
                json,
                $"\"version\":{version}",
                $"\"version\":{version + 9}"),
            UnknownKind.Category => ReplaceExactly(
                json,
                $"\"category\":\"{WorkerProtocolV1.LifecycleCategory}\"",
                "\"category\":\"future-category\""),
            UnknownKind.Type => ReplaceExactly(
                json,
                $"\"type\":\"{startedType}\"",
                "\"type\":\"future-event\""),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        return Encoding.UTF8.GetBytes(mutated);
    }

    private void ValidateV2(WorkerJobOutputMessage message)
    {
        if (message.Payload is WorkerJobReadyPayload)
        {
            if (_v2Ready is not null)
            {
                throw new InvalidOperationException("Fixture generated duplicate v2 ready output.");
            }

            _v2Ready = message;
            return;
        }

        if (message.JobId is not { } jobId || message.JobKind is not { } jobKind)
        {
            throw new InvalidOperationException("Fixture generated uncorrelated v2 output.");
        }

        if (_v2Validator is null)
        {
            _v2Validator = new WorkerJobOutputStreamValidator(jobId, jobKind);
            var ready = _v2Ready
                ?? throw new InvalidOperationException("Fixture generated v2 job output before ready.");
            var readyAccepted = _v2Validator.Validate(ready);
            if (!readyAccepted.IsSuccess)
            {
                throw new InvalidOperationException($"Fixture generated invalid v2 ready output: {readyAccepted.Failure!.Code}.");
            }
        }

        var accepted = _v2Validator.Validate(message);
        if (!accepted.IsSuccess)
        {
            throw new InvalidOperationException($"Fixture generated an invalid v2 event: {accepted.Failure!.Code}.");
        }
    }

    private static WorkerJobOutputMessage MapV2(WorkerProtocolEvent source)
    {
        if (source.Payload is ReadyPayload)
        {
            return WorkerJobProtocolMapper.Ready(
                source.Sequence,
                source.TimestampUtc,
                new WorkerJobReadyPayload([
                    WorkerJobKind.ProcessAssets,
                    WorkerJobKind.CoordinateLookup
                ]));
        }

        var jobId = source.RunId
            ?? throw new InvalidOperationException("Fixture v2 job output requires identity.");
        var context = new WorkerJobContext(
            jobId,
            WorkerJobKind.ProcessAssets,
            WorkerJobRequestOrigin.Manual);
        return source.Payload switch
        {
            RunStartedPayload started => WorkerJobProtocolMapper.JobStarted(
                context,
                started.Trigger,
                started.StartedAtUtc,
                source.Sequence),
            EligibilityDeterminedPayload eligibility => WorkerJobProtocolMapper.Map(
                context,
                new WorkerJobHandlerEvent(
                    source.TimestampUtc,
                    new ProcessAssetsEligibilityPayload(eligibility.EligibleCount)),
                source.Sequence),
            ProgressChangedPayload progress => WorkerJobProtocolMapper.Map(
                context,
                new WorkerJobHandlerEvent(
                    source.TimestampUtc,
                    new ProcessAssetsProgressPayload(
                        progress.ProcessedCount,
                        progress.UpdatedCount,
                        progress.SkippedCount,
                        progress.FailedCount)),
                source.Sequence),
            ActivityStartedPayload started => WorkerJobProtocolMapper.Map(
                context,
                new WorkerJobHandlerEvent(
                    source.TimestampUtc,
                    new WorkerJobActivityStartedPayload(started.ActivityId, started.Label)),
                source.Sequence),
            ActivityEndedPayload ended => WorkerJobProtocolMapper.Map(
                context,
                new WorkerJobHandlerEvent(
                    source.TimestampUtc,
                    new WorkerJobActivityEndedPayload(ended.ActivityId)),
                source.Sequence),
            LogEmittedPayload log => WorkerJobProtocolMapper.Map(
                context,
                new WorkerJobHandlerEvent(
                    source.TimestampUtc,
                    new WorkerJobLogPayload(log.Level, log.Message)),
                source.Sequence),
            TerminalPayload terminal => WorkerJobProtocolMapper.Terminal(
                context,
                MapTerminal(terminal),
                source.Sequence),
            _ => throw new InvalidOperationException("Fixture cannot map the selected v1 payload to v2.")
        };
    }

    private static WorkerJobTerminalPayload MapTerminal(TerminalPayload terminal)
    {
        WorkerJobTerminalOutcome outcome = terminal switch
        {
            CompletedPayload => WorkerJobTerminalOutcome.Completed,
            CancelledPayload => WorkerJobTerminalOutcome.Cancelled,
            FailedPayload => WorkerJobTerminalOutcome.Failed,
            _ => throw new InvalidOperationException("Fixture terminal type is not supported.")
        };
        ProcessAssetsResult? result = outcome == WorkerJobTerminalOutcome.Completed
            ? new ProcessAssetsResult(
                terminal.Trigger,
                terminal.StartedAtUtc,
                terminal.EndedAtUtc,
                terminal.ProcessedCount,
                terminal.UpdatedCount,
                terminal.SkippedCount,
                terminal.FailedCount)
            : null;
        WorkerJobSafeError? error = outcome == WorkerJobTerminalOutcome.Failed
            ? new WorkerJobSafeError(
                "fixture-failure",
                WorkerJobFailureCategory.Internal,
                terminal.FailureMessage!)
            : null;
        return new WorkerJobTerminalPayload(
            outcome,
            terminal.StartedAtUtc,
            terminal.EndedAtUtc,
            result,
            error);
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
