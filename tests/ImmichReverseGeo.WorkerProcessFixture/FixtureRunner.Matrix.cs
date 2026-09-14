using System.Text;
using System.Text.Json.Nodes;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.WorkerProcessFixture;

internal sealed partial class FixtureRunner
{
    internal const string MatrixCanary = "matrix-secret-51.501,-0.142-password-payload";
    internal const int MatrixPipeFrames = 4096;
    internal const int MatrixStandardErrorBytes = 262_177;

    private async Task<int> RunMatrixAsync()
    {
        var fault = _options.MatrixFault
            ?? throw new FixtureInputException("A matrix fault is required.");
        await File.WriteAllTextAsync(Path.Combine(_options.ResourceRoot, "matrix-entered.marker"),
            fault.ToString()).ConfigureAwait(false);
        if (fault == ProcessMatrixFault.NeverReady)
        {
            await HoldMatrixAsync().ConfigureAwait(false);
        }

        if (fault == ProcessMatrixFault.PreReadyCrash)
        {
            await WriteStandardErrorAsync(PreReadyCrashDiagnostic).ConfigureAwait(false);
            return 42;
        }

        if (fault == ProcessMatrixFault.MissingReady)
        {
            var request = new ProcessingRunRequest(_options.MatrixJobId!.Value,
                ProcessingRunTrigger.Manual);
            var frame = new ControllerInputFrame([], request, new ProcessAssetsWorkerJobDispatch(request));
            await _output.WriteFrameAsync(MatrixStartedFrame(frame, 1)).ConfigureAwait(false);
            return WorkerProcessExitCodes.OutputTransportFailure;
        }

        byte[] ready = MatrixReadyFrame();
        await _output.WriteFrameAsync(ready).ConfigureAwait(false);
        ControllerInputFrame execute = await _input.ReadExecuteAsync().ConfigureAwait(false);
        await CaptureExecuteAsync(execute.Bytes).ConfigureAwait(false);
        if (fault == ProcessMatrixFault.DuplicateReady)
        {
            await _output.WriteFrameAsync(ready).ConfigureAwait(false);
            return WorkerProcessExitCodes.OutputTransportFailure;
        }

        if (IsMatrixByteFault(fault))
        {
            await WriteMatrixByteFaultAsync(fault, MatrixStartedFrame(execute, 2)).ConfigureAwait(false);
            return WorkerProcessExitCodes.OutputTransportFailure;
        }

        byte[] started = MatrixStartedFrame(execute, 2);
        await _output.WriteFrameAsync(fault == ProcessMatrixFault.AdditiveProperties
            ? AddMatrixProperties(started)
            : started).ConfigureAwait(false);
        long sequence = 3;
        if (_protocolVersion == InternalWorkerProtocolVersion.V1)
        {
            await _output.WriteFrameAsync(WorkerProtocolCodec.Serialize(WorkerProtocolMapper.Map(
                new EligibilityDetermined(execute.Request, 0), sequence++, StartedAtUtc))).ConfigureAwait(false);
        }
        else if (execute.Dispatch!.Context.JobKind == WorkerJobKind.ProcessAssets)
        {
            await _output.WriteFrameAsync(WorkerJobProtocolCodec.Serialize(WorkerJobProtocolMapper.Map(
                execute.Dispatch.Context, new WorkerJobHandlerEvent(StartedAtUtc, new ProcessAssetsEligibilityPayload(0)),
                sequence++))).ConfigureAwait(false);
        }

        if (fault is ProcessMatrixFault.PostReadyCrash or ProcessMatrixFault.MissingTerminal
            or ProcessMatrixFault.OutputExit or ProcessMatrixFault.InfrastructureExit
            or ProcessMatrixFault.InvalidInputExit or ProcessMatrixFault.BusyExit or ProcessMatrixFault.CancelledExit)
        {
            return fault switch
            {
                ProcessMatrixFault.PostReadyCrash => 42,
                ProcessMatrixFault.MissingTerminal => WorkerProcessExitCodes.Completed,
                ProcessMatrixFault.OutputExit => WorkerProcessExitCodes.OutputTransportFailure,
                ProcessMatrixFault.InfrastructureExit => WorkerProcessExitCodes.InfrastructureFailure,
                ProcessMatrixFault.BusyExit => 3,
                ProcessMatrixFault.CancelledExit => WorkerProcessExitCodes.Cancelled,
                _ => WorkerProcessExitCodes.InvalidInput
            };
        }

        if (fault is ProcessMatrixFault.SequenceGap or ProcessMatrixFault.SequenceReplay or ProcessMatrixFault.ProtocolBeforeStop)
        {
            long invalidSequence = fault == ProcessMatrixFault.SequenceReplay ? sequence - 1 : sequence + 1;
            await _output.WriteFrameAsync(MatrixLogFrame(execute, invalidSequence, MatrixCanary)).ConfigureAwait(false);
            if (fault == ProcessMatrixFault.ProtocolBeforeStop)
            {
                await _input.ReadCancelOrEndAsync().ConfigureAwait(false);
                await HoldMatrixAsync().ConfigureAwait(false);
            }
            return WorkerProcessExitCodes.OutputTransportFailure;
        }

        await using var descendant = fault == ProcessMatrixFault.UnresponsiveTree
            ? await MatrixDescendant.StartAsync(_options.ResourceRoot).ConfigureAwait(false)
            : null;
        var outcome = WorkerJobTerminalOutcome.Failed;
        if (fault is ProcessMatrixFault.CooperativeCancel or ProcessMatrixFault.Unresponsive or ProcessMatrixFault.UnresponsiveQuiet
            or ProcessMatrixFault.UnresponsiveTree)
        {
            await _output.WriteFrameAsync(MatrixLogFrame(execute, sequence++, "matrix:armed")).ConfigureAwait(false);
            ControllerInputFrame? cancel = await _input.ReadCancelOrEndAsync().ConfigureAwait(false);
            if (cancel is null)
            {
                throw new FixtureInputException("Matrix input ended before correlated cancel.");
            }

            if (fault is ProcessMatrixFault.Unresponsive or ProcessMatrixFault.UnresponsiveQuiet or ProcessMatrixFault.UnresponsiveTree)
            {
                await File.WriteAllTextAsync(Path.Combine(_options.ResourceRoot, "matrix-cancel-observed.marker"),
                    "cancel").ConfigureAwait(false);
                if (fault == ProcessMatrixFault.Unresponsive)
                {
                    await _output.WriteFrameAsync(MatrixLogFrame(execute, sequence++, "matrix:cancel-observed")).ConfigureAwait(false);
                }
                await HoldMatrixAsync().ConfigureAwait(false);
            }

            outcome = WorkerJobTerminalOutcome.Cancelled;
        }

        if (fault is ProcessMatrixFault.LosslessPipeBurst or ProcessMatrixFault.LosslessPipeCorruption)
        {
            await _output.WriteFrameAsync(MatrixLogFrame(execute, sequence++, "matrix:pipes-armed")).ConfigureAwait(false);
            Task stderr = MatrixErrorBurstAsync();
            try
            {
                await WaitForMatrixReleaseAsync("stdout.release").ConfigureAwait(false);
                for (int index = 0; index < MatrixPipeFrames; index++)
                {
                    await _output.WriteFrameAsync(MatrixLogFrame(execute, sequence++,
                        index == 0 ? "matrix:pipe-first" : $"{MatrixCanary}:{index}:" + new string('x', 128))).ConfigureAwait(false);
                }
            }
            finally
            {
                await stderr.ConfigureAwait(false);
            }
            await _output.WriteFrameAsync(MatrixLogFrame(execute, sequence++, "matrix:pipes-drained")).ConfigureAwait(false);
            await WaitForMatrixReleaseAsync("terminal.release").ConfigureAwait(false);
            await WriteStandardErrorAsync(Encoding.UTF8.GetBytes("matrix:stderr-trailing\n")).ConfigureAwait(false);
        }

        byte[] terminal = MatrixTerminalFrame(execute, sequence++, outcome);
        if (fault is ProcessMatrixFault.CompletedLateProtocol or ProcessMatrixFault.CompletedContradictoryExit)
        {
            if (_protocolVersion == InternalWorkerProtocolVersion.V2
                && execute.Dispatch!.Context.JobKind != WorkerJobKind.ProcessAssets)
            {
                throw new FixtureInputException("Completed late-protocol mode requires ProcessAssets.");
            }

            var result = new ProcessingRunResult(execute.Request, StartedAtUtc, EndedAtUtc,
                0, 0, 0, 0, ProcessingRunOutcome.Completed, null);
            var completed = WorkerProtocolMapper.Map(new RunFinished(execute.Request, result), sequence - 1);
            terminal = _protocolVersion == InternalWorkerProtocolVersion.V1
                ? WorkerProtocolCodec.Serialize(completed)
                : WorkerJobProtocolCodec.Serialize(ProcessAssetsWorkerJobProjection.MapV1(execute.Request, completed));
            await _output.WriteFrameAsync(terminal).ConfigureAwait(false);
            await WaitForMatrixReleaseAsync("late-fault.release").ConfigureAwait(false);
            if (fault == ProcessMatrixFault.CompletedContradictoryExit)
            {
                return WorkerProcessExitCodes.ExecutorFailure;
            }
            await _output.WriteFrameAsync(MatrixLogFrame(execute, sequence, MatrixCanary)).ConfigureAwait(false);
            return WorkerProcessExitCodes.Completed;
        }

        await _output.WriteFrameAsync(fault == ProcessMatrixFault.AdditiveProperties
            ? AddMatrixProperties(terminal)
            : terminal).ConfigureAwait(false);
        if (fault is ProcessMatrixFault.PostTerminal or ProcessMatrixFault.LosslessPipeCorruption)
        {
            await _output.WriteFrameAsync(MatrixLogFrame(execute, sequence, MatrixCanary)).ConfigureAwait(false);
        }

        return fault == ProcessMatrixFault.TerminalMismatch
            ? WorkerProcessExitCodes.Completed
            : outcome == WorkerJobTerminalOutcome.Cancelled
                ? WorkerProcessExitCodes.Cancelled
                : WorkerProcessExitCodes.ExecutorFailure;
    }

    private static Task HoldMatrixAsync() =>
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;

    private async Task MatrixErrorBurstAsync()
    {
        await WaitForMatrixReleaseAsync("stderr.release").ConfigureAwait(false);
        // Unix ConsoleStream serializes stdout and stderr under Console.Out.
        // This closed concurrent-pipe mode must keep stderr live while stdout blocks.
        using var independentError = OperatingSystem.IsWindows() ? null
            : new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)2, ownsHandle: false),
                FileAccess.Write, bufferSize: 4096, isAsync: false);
        await WriteStandardErrorFloodAsync(independentError ?? _standardError, MatrixStandardErrorBytes).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_options.ResourceRoot, "stderr-drained.marker"), "drained").ConfigureAwait(false);
    }

    private async Task WaitForMatrixReleaseAsync(string name)
    {
        string path = Path.Combine(_options.ResourceRoot, name);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(_options.ResourceRoot, name);
        watcher.Created += (_, _) => released.TrySetResult();
        watcher.Error += (_, e) => released.TrySetException(e.GetException());
        watcher.EnableRaisingEvents = true;
        if (File.Exists(path))
        {
            released.TrySetResult();
        }

        await released.Task.ConfigureAwait(false);
    }

    private byte[] MatrixReadyFrame() => _protocolVersion == InternalWorkerProtocolVersion.V1
        ? WorkerProtocolCodec.Serialize(WorkerProtocolMapper.Ready(1, ReadyAtUtc))
        : WorkerJobProtocolCodec.Serialize(WorkerJobProtocolMapper.Ready(1, ReadyAtUtc,
            new WorkerJobReadyPayload([WorkerJobKind.ProcessAssets, WorkerJobKind.CoordinateLookup,
                WorkerJobKind.CacheMutation])));

    private byte[] MatrixStartedFrame(ControllerInputFrame execute, long sequence) =>
        _protocolVersion == InternalWorkerProtocolVersion.V1
            ? WorkerProtocolCodec.Serialize(WorkerProtocolMapper.Map(new RunStarted(execute.Request, StartedAtUtc), sequence))
            : WorkerJobProtocolCodec.Serialize(WorkerJobProtocolMapper.JobStarted(execute.Dispatch!.Context,
                execute.Request.Trigger == ProcessingRunTrigger.Scheduled ? "scheduled" : "manual", StartedAtUtc, sequence));

    private byte[] MatrixLogFrame(ControllerInputFrame execute, long sequence, string text) =>
        _protocolVersion == InternalWorkerProtocolVersion.V1
            ? WorkerProtocolCodec.Serialize(WorkerProtocolMapper.Map(new LogEmitted(execute.Request,
                ProcessingLogLevel.Information, text), sequence, StartedAtUtc))
            : WorkerJobProtocolCodec.Serialize(WorkerJobProtocolMapper.Map(execute.Dispatch!.Context,
                new WorkerJobHandlerEvent(StartedAtUtc, new WorkerJobLogPayload("information", text)), sequence));

    private byte[] MatrixTerminalFrame(ControllerInputFrame execute, long sequence, WorkerJobTerminalOutcome outcome)
    {
        if (_protocolVersion == InternalWorkerProtocolVersion.V1)
        {
            var result = new ProcessingRunResult(execute.Request, StartedAtUtc, EndedAtUtc,
                0, 0, 0, 0, outcome == WorkerJobTerminalOutcome.Cancelled
                    ? ProcessingRunOutcome.Cancelled : ProcessingRunOutcome.Failed,
                outcome == WorkerJobTerminalOutcome.Failed ? MatrixCanary : null);
            return WorkerProtocolCodec.Serialize(WorkerProtocolMapper.Map(new RunFinished(execute.Request, result), sequence));
        }

        return WorkerJobProtocolCodec.Serialize(WorkerJobProtocolMapper.Terminal(execute.Dispatch!.Context,
            new WorkerJobTerminalPayload(outcome, StartedAtUtc, EndedAtUtc, null,
                outcome == WorkerJobTerminalOutcome.Failed
                    ? new WorkerJobSafeError("matrix-domain-failure", WorkerJobFailureCategory.Internal, MatrixCanary)
                    : null), sequence));
    }

    private static bool IsMatrixByteFault(ProcessMatrixFault fault) => fault is
        ProcessMatrixFault.InvalidUtf8 or ProcessMatrixFault.MalformedJson or ProcessMatrixFault.TruncatedJson
        or ProcessMatrixFault.BlankFrame or ProcessMatrixFault.BomFrame or ProcessMatrixFault.OversizedFrame
        or ProcessMatrixFault.NonProtocolText or ProcessMatrixFault.UnknownProtocol or ProcessMatrixFault.UnknownVersion
        or ProcessMatrixFault.UnknownDirection or ProcessMatrixFault.UnknownCategory or ProcessMatrixFault.UnknownType
        or ProcessMatrixFault.UnknownJobKind or ProcessMatrixFault.CategoryTypeMismatch
        or ProcessMatrixFault.DuplicateProperty or ProcessMatrixFault.InvalidPayload or ProcessMatrixFault.WrongCorrelation
        or ProcessMatrixFault.AdditiveEnvelope or ProcessMatrixFault.AdditivePayload;

    private async Task WriteMatrixByteFaultAsync(ProcessMatrixFault fault, byte[] validFrame)
    {
        switch (fault)
        {
            case ProcessMatrixFault.InvalidUtf8:
                await _output.WriteRawAsync(new byte[] { 0xff, (byte)'\n' }).ConfigureAwait(false);
                return;
            case ProcessMatrixFault.MalformedJson:
                await _output.WriteFrameAsync(Encoding.UTF8.GetBytes("{]" + MatrixCanary)).ConfigureAwait(false);
                return;
            case ProcessMatrixFault.TruncatedJson:
                await _output.WriteRawAsync(validFrame.AsMemory(0, validFrame.Length - 1)).ConfigureAwait(false);
                return;
            case ProcessMatrixFault.BlankFrame:
                await _output.WriteRawAsync(new byte[] { (byte)'\n' }).ConfigureAwait(false);
                return;
            case ProcessMatrixFault.BomFrame:
                await _output.WriteRawAsync(new byte[] { 0xef, 0xbb, 0xbf }).ConfigureAwait(false);
                await _output.WriteFrameAsync(validFrame).ConfigureAwait(false);
                return;
            case ProcessMatrixFault.OversizedFrame:
                await _output.WriteOversizedFrameAsync().ConfigureAwait(false);
                return;
            case ProcessMatrixFault.NonProtocolText:
                await _output.WriteFrameAsync(Encoding.UTF8.GetBytes(MatrixCanary)).ConfigureAwait(false);
                return;
        }

        var node = JsonNode.Parse(validFrame)!.AsObject();
        if (fault is ProcessMatrixFault.AdditiveEnvelope or ProcessMatrixFault.AdditivePayload)
        {
            var target = fault == ProcessMatrixFault.AdditiveEnvelope ? node : node["payload"]!.AsObject();
            target["futureMatrixProperty"] = MatrixCanary;
            await _output.WriteFrameAsync(Encoding.UTF8.GetBytes(node.ToJsonString())).ConfigureAwait(false);
            return;
        }

        string key = fault switch
        {
            ProcessMatrixFault.UnknownProtocol => "protocol",
            ProcessMatrixFault.UnknownVersion => "version",
            ProcessMatrixFault.UnknownDirection => "direction",
            ProcessMatrixFault.UnknownCategory or ProcessMatrixFault.CategoryTypeMismatch => "category",
            ProcessMatrixFault.UnknownType => "type",
            ProcessMatrixFault.UnknownJobKind => "jobKind",
            ProcessMatrixFault.WrongCorrelation => _protocolVersion == InternalWorkerProtocolVersion.V1 ? "runId" : "jobId",
            _ => "payload"
        };
        if (!node.ContainsKey(key))
        {
            throw new FixtureInputException("The selected fault does not apply to this protocol.");
        }

        switch (fault)
        {
            case ProcessMatrixFault.UnknownVersion:
                node[key] = 99;
                break;
            case ProcessMatrixFault.CategoryTypeMismatch:
                node[key] = "terminal";
                break;
            case ProcessMatrixFault.WrongCorrelation:
                node[key] = "67676767-0000-0000-0000-000000000099";
                break;
            case ProcessMatrixFault.InvalidPayload:
                node[key]!["startedAtUtc"] = "invalid-" + MatrixCanary;
                break;
            case ProcessMatrixFault.DuplicateProperty:
                string json = node.ToJsonString();
                await _output.WriteFrameAsync(Encoding.UTF8.GetBytes("{\"sequence\":2," + json[1..])).ConfigureAwait(false);
                return;
            default:
                node[key] = MatrixCanary;
                break;
        }

        await _output.WriteFrameAsync(Encoding.UTF8.GetBytes(node.ToJsonString())).ConfigureAwait(false);
    }

    private static byte[] AddMatrixProperties(byte[] frame)
    {
        var node = JsonNode.Parse(frame)!.AsObject();
        node["futureMatrixProperty"] = MatrixCanary;
        node["payload"]!["futureMatrixProperty"] = MatrixCanary;
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }
}
