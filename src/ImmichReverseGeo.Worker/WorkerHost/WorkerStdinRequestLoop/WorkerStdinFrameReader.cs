using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop;

internal sealed class WorkerStdinFrameReader : IAsyncDisposable
{
    private const int ReadBufferBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Stream _input;
    private readonly object _closeGate = new();
    private readonly byte[] _readBuffer = new byte[ReadBufferBytes];
    private readonly byte[] _frameBuffer = new byte[WorkerProtocolV1.MaxMessageBytes + 1];
    private readonly char[] _decoderCharacters = new char[2];
    private Decoder _decoder = StrictUtf8.GetDecoder();
    private int _readOffset;
    private int _readCount;
    private int _frameCount;
    private Task? _closeTask;
    private bool _disposed;

    internal WorkerStdinFrameReader(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _input = input;
    }

    internal async Task<WorkerStdinFrameReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        try
        {
            while (true)
            {
                if (_readOffset == _readCount)
                {
                    _readCount = await _input.ReadAsync(_readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _readOffset = 0;
                    if (_readCount == 0)
                    {
                        if (_frameCount == 0)
                        {
                            return WorkerStdinFrameReadResult.EndOfInput();
                        }

                        return TryCompleteUtf8()
                            ? WorkerStdinFrameReadResult.Failed(WorkerProtocolFailureCode.InvalidFraming)
                            : WorkerStdinFrameReadResult.Failed(WorkerProtocolFailureCode.InvalidEncoding);
                    }
                }

                var value = _readBuffer[_readOffset++];
                if (_frameCount > 0 && _frameBuffer[_frameCount - 1] == (byte)'\r' && value != (byte)'\n')
                {
                    return WorkerStdinFrameReadResult.Failed(
                        _frameCount == WorkerProtocolV1.MaxMessageBytes + 1
                            ? WorkerProtocolFailureCode.MessageTooLarge
                            : WorkerProtocolFailureCode.InvalidFraming);
                }

                if (value == (byte)'\n')
                {
                    if (!TryCompleteUtf8())
                    {
                        return WorkerStdinFrameReadResult.Failed(WorkerProtocolFailureCode.InvalidEncoding);
                    }

                    var contentLength = _frameCount;
                    if (contentLength > 0 && _frameBuffer[contentLength - 1] == (byte)'\r')
                    {
                        contentLength--;
                    }

                    if (contentLength == 0)
                    {
                        ResetFrame();
                        return WorkerStdinFrameReadResult.Failed(WorkerProtocolFailureCode.InvalidFraming);
                    }

                    if (contentLength >= 3 && _frameBuffer[0] == 0xef && _frameBuffer[1] == 0xbb && _frameBuffer[2] == 0xbf)
                    {
                        ResetFrame();
                        return WorkerStdinFrameReadResult.Failed(WorkerProtocolFailureCode.InvalidEncoding);
                    }

                    var frame = new byte[contentLength];
                    Array.Copy(_frameBuffer, frame, contentLength);
                    ResetFrame();
                    return WorkerStdinFrameReadResult.CompletedFrame(frame);
                }

                if (!TryAppend(value))
                {
                    return WorkerStdinFrameReadResult.Failed(WorkerProtocolFailureCode.MessageTooLarge);
                }

                if (!TryDecode(value))
                {
                    return WorkerStdinFrameReadResult.Failed(WorkerProtocolFailureCode.InvalidEncoding);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return WorkerStdinFrameReadResult.ReaderFailure();
        }
    }

    internal ValueTask CloseInputAsync()
    {
        lock (_closeGate)
        {
            if (_closeTask is null)
            {
                try
                {
                    _closeTask = _input.DisposeAsync().AsTask();
                }
                catch (Exception exception)
                {
                    _closeTask = Task.FromException(exception);
                }
            }

            return new ValueTask(_closeTask);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await CloseInputAsync().ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(_readBuffer);
            Array.Clear(_frameBuffer);
            Array.Clear(_decoderCharacters);
            _decoder.Reset();
            _readOffset = 0;
            _readCount = 0;
            _frameCount = 0;
        }
    }

    private bool TryAppend(byte value)
    {
        if (_frameCount == WorkerProtocolV1.MaxMessageBytes)
        {
            if (value != (byte)'\r')
            {
                return false;
            }
        }
        else if (_frameCount == WorkerProtocolV1.MaxMessageBytes + 1)
        {
            return false;
        }

        _frameBuffer[_frameCount++] = value;
        return true;
    }

    private bool TryDecode(byte value)
    {
        try
        {
            _decoder.GetChars(_frameBuffer, _frameCount - 1, 1, _decoderCharacters, 0, false);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private bool TryCompleteUtf8()
    {
        try
        {
            _decoder.GetChars(Array.Empty<byte>(), 0, 0, _decoderCharacters, 0, true);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private void ResetFrame()
    {
        Array.Clear(_frameBuffer, 0, _frameCount);
        _frameCount = 0;
        _decoder = StrictUtf8.GetDecoder();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class WorkerStdinFrameReadResult
{
    private WorkerStdinFrameReadResult(byte[]? frame, WorkerProtocolFailureCode? failureCode, bool endOfInput, bool readerFailure)
    {
        Frame = frame;
        FailureCode = failureCode;
        IsEndOfInput = endOfInput;
        IsReaderFailure = readerFailure;
    }

    internal byte[]? Frame { get; }
    internal WorkerProtocolFailureCode? FailureCode { get; }
    internal bool IsEndOfInput { get; }
    internal bool IsReaderFailure { get; }
    internal bool IsSuccess => Frame is not null;

    internal static WorkerStdinFrameReadResult CompletedFrame(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new WorkerStdinFrameReadResult(frame, null, false, false);
    }

    internal static WorkerStdinFrameReadResult EndOfInput() => new(null, null, true, false);
    internal static WorkerStdinFrameReadResult Failed(WorkerProtocolFailureCode code) => new(null, code, false, false);
    internal static WorkerStdinFrameReadResult ReaderFailure() => new(null, null, false, true);
}
