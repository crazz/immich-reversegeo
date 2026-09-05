using System.Text;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.WorkerProcessFixture;

internal sealed class FixtureRunner
{
    internal const string StandardErrorPrefix = "fixture-stderr-prefix\n";
    internal const string StandardErrorSuffix = "\nfixture-stderr-suffix\n";

    private static readonly DateTimeOffset ReadyAtUtc = new(2000, 1, 2, 3, 4, 4, TimeSpan.Zero);
    private static readonly DateTimeOffset StartedAtUtc = new(2000, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset EndedAtUtc = new(2000, 1, 2, 3, 4, 6, TimeSpan.Zero);
    private static readonly byte[] PreReadyCrashDiagnostic = Encoding.UTF8.GetBytes("fixture:pre-ready-crash\n");
    private static readonly byte[] PostReadyCrashDiagnostic = Encoding.UTF8.GetBytes("fixture:post-ready-crash\n");
    private static readonly byte[] MalformedUtf8 = [0xff, (byte)'\n'];
    private static readonly byte[] MalformedJson = Encoding.UTF8.GetBytes("{]\n");
    private static readonly byte[] MalformedFraming = [(byte)'\n'];

    private readonly FixtureOptions _options;
    private readonly ControllerInputReader _input;
    private readonly FixtureProtocolOutput _output;
    private readonly Stream _standardError;

    internal FixtureRunner(FixtureOptions options, Stream standardInput, Stream standardOutput, Stream standardError)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        _options = options;
        _input = new ControllerInputReader(standardInput);
        _output = new FixtureProtocolOutput(standardOutput);
        _standardError = standardError;
    }

    internal async Task<int> RunAsync()
    {
        if (_options.Scenario == FixtureScenario.PreReadyCrash)
        {
            await WriteStandardErrorAsync(PreReadyCrashDiagnostic).ConfigureAwait(false);
            return _options.ExitCode!.Value;
        }

        if (_options.Scenario == FixtureScenario.RawExit)
        {
            return _options.ExitCode!.Value;
        }

        await _output.WriteValidAsync(WorkerProtocolMapper.Ready(1, ReadyAtUtc)).ConfigureAwait(false);
        var executeFrame = await _input.ReadExecuteAsync().ConfigureAwait(false);
        await CaptureExecuteAsync(executeFrame.Bytes).ConfigureAwait(false);
        var request = ((ExecuteRequestPayload)executeFrame.Message.Payload).Request;

        return _options.Scenario switch
        {
            FixtureScenario.Ready => await RunNoWorkAsync(request).ConfigureAwait(false),
            FixtureScenario.Success => await RunSuccessAsync(request, "success").ConfigureAwait(false),
            FixtureScenario.NoWork => await RunNoWorkAsync(request).ConfigureAwait(false),
            FixtureScenario.PostReadyCrash => await RunPostReadyCrashAsync(request).ConfigureAwait(false),
            FixtureScenario.Malformed => await RunMalformedAsync().ConfigureAwait(false),
            FixtureScenario.Oversize => await RunOversizeAsync().ConfigureAwait(false),
            FixtureScenario.Unknown => await RunUnknownAsync(request).ConfigureAwait(false),
            FixtureScenario.InvalidSequence => await RunInvalidSequenceAsync(request).ConfigureAwait(false),
            FixtureScenario.TerminalMismatch => await RunTerminalMismatchAsync(request).ConfigureAwait(false),
            FixtureScenario.StandardErrorFlood => await RunStandardErrorFloodAsync(request).ConfigureAwait(false),
            FixtureScenario.CooperativeCancel => await RunCooperativeCancelAsync(request).ConfigureAwait(false),
            FixtureScenario.Unresponsive => await RunUnresponsiveAsync(request).ConfigureAwait(false),
            _ => throw new InvalidOperationException("The selected fixture scenario is not executable.")
        };
    }

    private async Task<int> RunNoWorkAsync(ProcessingRunRequest request)
    {
        await EmitStartedAndEligibilityAsync(request, 0).ConfigureAwait(false);
        await EmitTerminalAsync(request, ProcessingRunOutcome.Completed, 4, 0, 0, 0, 0).ConfigureAwait(false);
        _output.AssertComplete();
        return WorkerProcessExitCodes.Completed;
    }

    private async Task<int> RunSuccessAsync(ProcessingRunRequest request, string scenarioToken)
    {
        await EmitStartedAndEligibilityAsync(request, 1).ConfigureAwait(false);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new ActivityStarted(request, request.RunId, "fixture-activity"),
            4,
            StartedAtUtc.AddTicks(2))).ConfigureAwait(false);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new LogEmitted(request, ProcessingLogLevel.Information, Marker(scenarioToken, request.RunId)),
            5,
            StartedAtUtc.AddTicks(3))).ConfigureAwait(false);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new ProgressChanged(request, new ProcessingProgress(1, 1, 0, 0)),
            6,
            StartedAtUtc.AddTicks(4))).ConfigureAwait(false);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new ActivityEnded(request, request.RunId),
            7,
            StartedAtUtc.AddTicks(5))).ConfigureAwait(false);
        await EmitTerminalAsync(request, ProcessingRunOutcome.Completed, 8, 1, 1, 0, 0).ConfigureAwait(false);
        _output.AssertComplete();
        return WorkerProcessExitCodes.Completed;
    }

    private async Task<int> RunPostReadyCrashAsync(ProcessingRunRequest request)
    {
        await EmitStartedAndEligibilityAsync(request, 0).ConfigureAwait(false);
        await EmitLogAsync(request, 4, Marker("post-ready-crash", request.RunId)).ConfigureAwait(false);
        await WriteStandardErrorAsync(PostReadyCrashDiagnostic).ConfigureAwait(false);
        return _options.ExitCode!.Value;
    }

    private async Task<int> RunMalformedAsync()
    {
        var bytes = _options.SelectedMalformedKind!.Value switch
        {
            MalformedKind.Utf8 => MalformedUtf8,
            MalformedKind.Json => MalformedJson,
            MalformedKind.Framing => MalformedFraming,
            _ => throw new InvalidOperationException("The selected malformed-output kind is not supported.")
        };

        await _output.WriteRawAsync(bytes).ConfigureAwait(false);
        return WorkerProcessExitCodes.Completed;
    }

    private async Task<int> RunOversizeAsync()
    {
        await _output.WriteOversizedFrameAsync().ConfigureAwait(false);
        return WorkerProcessExitCodes.Completed;
    }

    private async Task<int> RunUnknownAsync(ProcessingRunRequest request)
    {
        var valid = WorkerProtocolMapper.Map(new RunStarted(request, StartedAtUtc), 2);
        var unknown = FixtureProtocolOutput.MutateUnknown(valid, _options.SelectedUnknownKind!.Value);
        await _output.WriteFrameAsync(unknown).ConfigureAwait(false);
        return WorkerProcessExitCodes.Completed;
    }

    private async Task<int> RunInvalidSequenceAsync(ProcessingRunRequest request)
    {
        var invalidSequence = _options.SelectedSequenceFault == SequenceFault.Gap ? 3 : 1;
        var invalid = WorkerProtocolMapper.Map(new RunStarted(request, StartedAtUtc), invalidSequence);
        await _output.WriteFrameAsync(WorkerProtocolCodec.Serialize(invalid)).ConfigureAwait(false);
        return WorkerProcessExitCodes.Completed;
    }

    private async Task<int> RunTerminalMismatchAsync(ProcessingRunRequest request)
    {
        await EmitStartedAndEligibilityAsync(request, 0).ConfigureAwait(false);
        var outcome = _options.SelectedTerminalKind!.Value switch
        {
            TerminalKind.Completed => ProcessingRunOutcome.Completed,
            TerminalKind.Cancelled => ProcessingRunOutcome.Cancelled,
            TerminalKind.Failed => ProcessingRunOutcome.Failed,
            _ => throw new InvalidOperationException("The selected terminal kind is not supported.")
        };

        await EmitTerminalAsync(request, outcome, 4, 0, 0, 0, 0).ConfigureAwait(false);
        _output.AssertComplete();
        return _options.ExitCode!.Value;
    }

    private async Task<int> RunStandardErrorFloodAsync(ProcessingRunRequest request)
    {
        await EmitStartedAndEligibilityAsync(request, 1).ConfigureAwait(false);
        await WriteStandardErrorFloodAsync(_options.StandardErrorBytes!.Value).ConfigureAwait(false);

        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new ActivityStarted(request, request.RunId, "fixture-activity"),
            4,
            StartedAtUtc.AddTicks(2))).ConfigureAwait(false);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new LogEmitted(request, ProcessingLogLevel.Information, Marker("stderr-flood", request.RunId)),
            5,
            StartedAtUtc.AddTicks(3))).ConfigureAwait(false);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new ProgressChanged(request, new ProcessingProgress(1, 1, 0, 0)),
            6,
            StartedAtUtc.AddTicks(4))).ConfigureAwait(false);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new ActivityEnded(request, request.RunId),
            7,
            StartedAtUtc.AddTicks(5))).ConfigureAwait(false);
        await EmitTerminalAsync(request, ProcessingRunOutcome.Completed, 8, 1, 1, 0, 0).ConfigureAwait(false);
        _output.AssertComplete();
        return WorkerProcessExitCodes.Completed;
    }

    private async Task<int> RunCooperativeCancelAsync(ProcessingRunRequest request)
    {
        await EmitStartedAndEligibilityAsync(request, 0).ConfigureAwait(false);
        await EmitLogAsync(request, 4, Marker("cooperative-cancel", request.RunId)).ConfigureAwait(false);

        var cancel = await _input.ReadCancelOrEndAsync().ConfigureAwait(false);
        if (cancel is null)
        {
            throw new FixtureInputException("Controller input ended before cooperative cancel.");
        }

        await EmitTerminalAsync(request, ProcessingRunOutcome.Cancelled, 5, 0, 0, 0, 0).ConfigureAwait(false);
        _output.AssertComplete();
        return WorkerProcessExitCodes.Cancelled;
    }

    private async Task<int> RunUnresponsiveAsync(ProcessingRunRequest request)
    {
        await EmitStartedAndEligibilityAsync(request, 0).ConfigureAwait(false);
        await EmitLogAsync(request, 4, Marker("unresponsive", request.RunId)).ConfigureAwait(false);

        var cancel = await _input.ReadCancelOrEndAsync().ConfigureAwait(false);
        var observation = cancel is null
            ? $"fixture:input-closed:{request.RunId:D}"
            : $"fixture:cancel-observed:{request.RunId:D}";
        await EmitLogAsync(request, 5, observation).ConfigureAwait(false);

        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await never.Task.ConfigureAwait(false);
        return WorkerProcessExitCodes.InfrastructureFailure;
    }

    private async Task EmitStartedAndEligibilityAsync(ProcessingRunRequest request, long eligibleCount)
    {
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(new RunStarted(request, StartedAtUtc), 2)).ConfigureAwait(false);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new EligibilityDetermined(request, eligibleCount),
            3,
            StartedAtUtc.AddTicks(1))).ConfigureAwait(false);
    }

    private async Task EmitLogAsync(ProcessingRunRequest request, long sequence, string message)
    {
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(
            new LogEmitted(request, ProcessingLogLevel.Information, message),
            sequence,
            StartedAtUtc.AddTicks(sequence - 2))).ConfigureAwait(false);
    }

    private async Task EmitTerminalAsync(
        ProcessingRunRequest request,
        ProcessingRunOutcome outcome,
        long sequence,
        long processedCount,
        long updatedCount,
        long skippedCount,
        long failedCount)
    {
        var failureMessage = outcome == ProcessingRunOutcome.Failed ? "fixture failure" : null;
        var result = new ProcessingRunResult(
            request,
            StartedAtUtc,
            EndedAtUtc,
            processedCount,
            updatedCount,
            skippedCount,
            failedCount,
            outcome,
            failureMessage);
        await _output.WriteValidAsync(WorkerProtocolMapper.Map(new RunFinished(request, result), sequence)).ConfigureAwait(false);
    }

    private async Task CaptureExecuteAsync(byte[] frame)
    {
        if (_options.CaptureName is null)
        {
            return;
        }

        var finalPath = Path.Combine(_options.ResourceRoot, _options.CaptureName);
        var temporaryPath = Path.Combine(
            _options.ResourceRoot,
            $".{_options.CaptureName}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                }))
            {
                await stream.WriteAsync(frame).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, finalPath, overwrite: false);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
            }

            throw;
        }
    }

    private async Task WriteStandardErrorFloodAsync(int totalBytes)
    {
        var prefix = Encoding.UTF8.GetBytes(StandardErrorPrefix);
        var suffix = Encoding.UTF8.GetBytes(StandardErrorSuffix);
        var bodyBytes = totalBytes - prefix.Length - suffix.Length;
        if (bodyBytes < 0)
        {
            throw new InvalidOperationException("The requested stderr size cannot contain its fixed markers.");
        }

        await _standardError.WriteAsync(prefix).ConfigureAwait(false);
        var buffer = new byte[4096];
        var bodyOffset = 0;
        while (bodyOffset < bodyBytes)
        {
            var count = Math.Min(buffer.Length, bodyBytes - bodyOffset);
            for (var index = 0; index < count; index++)
            {
                buffer[index] = (byte)('a' + (bodyOffset + index) % 26);
            }

            await _standardError.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
            bodyOffset += count;
        }

        await _standardError.WriteAsync(suffix).ConfigureAwait(false);
        await _standardError.FlushAsync().ConfigureAwait(false);
    }

    private async Task WriteStandardErrorAsync(ReadOnlyMemory<byte> bytes)
    {
        await _standardError.WriteAsync(bytes).ConfigureAwait(false);
        await _standardError.FlushAsync().ConfigureAwait(false);
    }

    private static string Marker(string scenario, Guid runId)
    {
        return $"fixture:{scenario}:{runId:D}";
    }
}
