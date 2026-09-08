using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.WorkerProcessFixture;

internal sealed record ControllerInputFrame(
    byte[] Bytes,
    ProcessingRunRequest Request,
    WorkerJobDispatch? Dispatch);

internal sealed class FixtureInputException(string message) : Exception(message);

internal sealed class ControllerInputReader
{
    private const int ReadBufferBytes = 4096;
    private readonly Stream _input;
    private readonly InternalWorkerProtocolVersion _protocolVersion;
    private readonly WorkerProtocolControllerInputValidator? _v1Validator;
    private readonly WorkerJobControllerInputValidator? _v2Validator;
    private readonly byte[] _readBuffer = new byte[ReadBufferBytes];
    private readonly byte[] _frameBuffer = new byte[WorkerProtocolV1.MaxMessageBytes + 2];
    private int _readOffset;
    private int _readCount;
    private int _frameCount;

    internal ControllerInputReader(Stream input, InternalWorkerProtocolVersion protocolVersion)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Enum.IsDefined(protocolVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        }

        _input = input;
        _protocolVersion = protocolVersion;
        _v1Validator = protocolVersion == InternalWorkerProtocolVersion.V1
            ? new WorkerProtocolControllerInputValidator()
            : null;
        _v2Validator = protocolVersion == InternalWorkerProtocolVersion.V2
            ? new WorkerJobControllerInputValidator([
                WorkerJobDescriptors.ProcessAssets,
                WorkerJobDescriptors.CoordinateLookup
            ])
            : null;
    }

    internal async Task<ControllerInputFrame> ReadExecuteAsync()
    {
        var rawFrame = await ReadFrameAsync().ConfigureAwait(false);
        if (rawFrame is null)
        {
            FinalizeInput(hasPartialFrame: false);
            throw new FixtureInputException("Controller input ended before execute.");
        }

        var accepted = ParseAndValidate(rawFrame, WorkerProtocolExecutionPhase.BeforeInvocation);
        return accepted;
    }

    internal async Task<ControllerInputFrame?> ReadCancelOrEndAsync()
    {
        var rawFrame = await ReadFrameAsync().ConfigureAwait(false);
        if (rawFrame is null)
        {
            FinalizeInput(hasPartialFrame: false);
            return null;
        }

        var accepted = ParseAndValidate(rawFrame, WorkerProtocolExecutionPhase.Executing);
        return accepted;
    }

    private ControllerInputFrame ParseAndValidate(byte[] rawFrame, WorkerProtocolExecutionPhase phase)
    {
        return _protocolVersion switch
        {
            InternalWorkerProtocolVersion.V1 => ParseAndValidateV1(rawFrame, phase),
            InternalWorkerProtocolVersion.V2 => ParseAndValidateV2(rawFrame, phase),
            _ => throw new InvalidOperationException("The selected protocol version is not supported.")
        };
    }

    private ControllerInputFrame ParseAndValidateV1(
        byte[] rawFrame,
        WorkerProtocolExecutionPhase phase)
    {
        var parsed = WorkerProtocolCodec.ParseControllerInput(rawFrame);
        if (!parsed.IsSuccess)
        {
            throw new FixtureInputException($"Controller frame was rejected: {parsed.Failure!.Code}.");
        }

        var validated = _v1Validator!.Validate(parsed.Message!, isReady: true, phase);
        if (!validated.IsSuccess)
        {
            throw new FixtureInputException($"Controller sequence was rejected: {validated.Failure!.Code}.");
        }

        ProcessingRunRequest request = validated.Message!.Payload switch
        {
            ExecuteRequestPayload execute => execute.Request,
            CancelControlPayload => _v1Validator.Snapshot.Request
                ?? throw new FixtureInputException("Cancel was accepted without execute identity."),
            _ => throw new FixtureInputException("The controller frame type was not supported.")
        };
        return new ControllerInputFrame(rawFrame, request, null);
    }

    private ControllerInputFrame ParseAndValidateV2(
        byte[] rawFrame,
        WorkerProtocolExecutionPhase phase)
    {
        var parsed = WorkerJobProtocolCodec.ParseControllerInput(rawFrame);
        if (!parsed.IsSuccess)
        {
            throw new FixtureInputException($"Controller frame was rejected: {parsed.Failure!.Code}.");
        }

        WorkerJobExecutionPhase jobPhase = phase switch
        {
            WorkerProtocolExecutionPhase.BeforeInvocation => WorkerJobExecutionPhase.BeforeInvocation,
            WorkerProtocolExecutionPhase.Executing => WorkerJobExecutionPhase.Executing,
            WorkerProtocolExecutionPhase.Terminal => WorkerJobExecutionPhase.Terminal,
            _ => throw new ArgumentOutOfRangeException(nameof(phase))
        };
        var validated = _v2Validator!.Validate(parsed.Message!, isReady: true, jobPhase);
        if (!validated.IsSuccess)
        {
            throw new FixtureInputException($"Controller sequence was rejected: {validated.Failure!.Code}.");
        }

        WorkerJobDispatch dispatch = validated.Message!.Payload switch
        {
            ProcessAssetsExecutePayload execute => new ProcessAssetsWorkerJobDispatch(
                execute.Request.ProcessingRequest),
            CoordinateLookupExecutePayload execute => new CoordinateLookupWorkerJobDispatch(
                validated.Message.JobId,
                execute.Request),
            WorkerJobCancelPayload => CreateAcceptedDispatch(_v2Validator.Snapshot),
            _ => throw new FixtureInputException("The controller frame type was not supported.")
        };
        ProcessingRunRequest compatibilityRequest = dispatch switch
        {
            ProcessAssetsWorkerJobDispatch processAssets => processAssets.Request.ProcessingRequest,
            CoordinateLookupWorkerJobDispatch => new ProcessingRunRequest(
                dispatch.Context.JobId,
                ProcessingRunTrigger.Manual),
            _ => throw new FixtureInputException("The accepted job kind was not supported by the fixture.")
        };
        return new ControllerInputFrame(rawFrame, compatibilityRequest, dispatch);
    }

    private static WorkerJobDispatch CreateAcceptedDispatch(
        WorkerJobControllerInputSnapshot snapshot)
    {
        if (snapshot.JobId is not { } jobId || snapshot.Request is null)
        {
            throw new FixtureInputException("Cancel was accepted without execute identity.");
        }

        return snapshot.Request switch
        {
            ProcessAssetsRequest processAssets when processAssets.ProcessingRequest.RunId == jobId =>
                new ProcessAssetsWorkerJobDispatch(processAssets.ProcessingRequest),
            CoordinateLookupRequest coordinateLookup => new CoordinateLookupWorkerJobDispatch(jobId, coordinateLookup),
            _ => throw new FixtureInputException("The accepted request type was not supported by the fixture.")
        };
    }

    private async Task<byte[]?> ReadFrameAsync()
    {
        while (true)
        {
            if (_readOffset == _readCount)
            {
                _readCount = await _input.ReadAsync(_readBuffer.AsMemory()).ConfigureAwait(false);
                _readOffset = 0;
                if (_readCount == 0)
                {
                    if (_frameCount != 0)
                    {
                        FinalizeInput(hasPartialFrame: true);
                        throw new FixtureInputException("Controller input ended during a frame.");
                    }

                    return null;
                }
            }

            var value = _readBuffer[_readOffset++];
            if (_frameCount > 0 && _frameBuffer[_frameCount - 1] == (byte)'\r' && value != (byte)'\n')
            {
                throw new FixtureInputException("Controller input contained an invalid bare carriage return.");
            }

            if (_frameCount == _frameBuffer.Length)
            {
                throw new FixtureInputException("Controller frame exceeded the shared byte limit.");
            }

            _frameBuffer[_frameCount++] = value;
            if (value == (byte)'\n')
            {
                var contentLength = _frameCount - 1;
                if (contentLength > 0 && _frameBuffer[contentLength - 1] == (byte)'\r')
                {
                    contentLength--;
                }

                if (contentLength == 0 || contentLength > WorkerProtocolV1.MaxMessageBytes)
                {
                    throw new FixtureInputException("Controller frame violated the shared framing limit.");
                }

                var frame = new byte[_frameCount];
                Array.Copy(_frameBuffer, frame, _frameCount);
                Array.Clear(_frameBuffer, 0, _frameCount);
                _frameCount = 0;
                return frame;
            }

            if (_frameCount > WorkerProtocolV1.MaxMessageBytes
                && (_frameCount != WorkerProtocolV1.MaxMessageBytes + 1 || value != (byte)'\r'))
            {
                throw new FixtureInputException("Controller frame exceeded the shared byte limit.");
            }
        }
    }

    private void FinalizeInput(bool hasPartialFrame)
    {
        WorkerProtocolFailure? failure = _protocolVersion switch
        {
            InternalWorkerProtocolVersion.V1 => _v1Validator!.FinalizeInput(hasPartialFrame).Failure,
            InternalWorkerProtocolVersion.V2 when hasPartialFrame => new WorkerProtocolFailure(
                WorkerProtocolFailureCode.InvalidFraming,
                "Controller input ended during a frame."),
            InternalWorkerProtocolVersion.V2 => null,
            _ => throw new InvalidOperationException("The selected protocol version is not supported.")
        };
        if (failure is not null)
        {
            throw new FixtureInputException($"Controller input finalization failed: {failure.Code}.");
        }
    }
}
