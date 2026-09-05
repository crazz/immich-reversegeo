using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.WorkerProcessFixture;

internal sealed record ControllerInputFrame(byte[] Bytes, WorkerProtocolControllerMessage Message);

internal sealed class FixtureInputException(string message) : Exception(message);

internal sealed class ControllerInputReader
{
    private const int ReadBufferBytes = 4096;
    private readonly Stream _input;
    private readonly WorkerProtocolControllerInputValidator _validator = new();
    private readonly byte[] _readBuffer = new byte[ReadBufferBytes];
    private readonly byte[] _frameBuffer = new byte[WorkerProtocolV1.MaxMessageBytes + 2];
    private int _readOffset;
    private int _readCount;
    private int _frameCount;

    internal ControllerInputReader(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _input = input;
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
        if (accepted.Message.Type != WorkerProtocolV1.ExecuteType || accepted.Message.Payload is not ExecuteRequestPayload)
        {
            throw new FixtureInputException("The first controller frame was not execute.");
        }

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
        if (accepted.Message.Type != WorkerProtocolV1.CancelType || accepted.Message.Payload is not CancelControlPayload)
        {
            throw new FixtureInputException("The controller frame was not cancel.");
        }

        return accepted;
    }

    private ControllerInputFrame ParseAndValidate(byte[] rawFrame, WorkerProtocolExecutionPhase phase)
    {
        var parsed = WorkerProtocolCodec.ParseControllerInput(rawFrame);
        if (!parsed.IsSuccess)
        {
            throw new FixtureInputException($"Controller frame was rejected: {parsed.Failure!.Code}.");
        }

        var validated = _validator.Validate(parsed.Message!, isReady: true, phase);
        if (!validated.IsSuccess)
        {
            throw new FixtureInputException($"Controller sequence was rejected: {validated.Failure!.Code}.");
        }

        return new ControllerInputFrame(rawFrame, validated.Message!);
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
        var finalized = _validator.FinalizeInput(hasPartialFrame);
        if (!finalized.IsSuccess)
        {
            throw new FixtureInputException($"Controller input finalization failed: {finalized.Failure!.Code}.");
        }
    }
}
