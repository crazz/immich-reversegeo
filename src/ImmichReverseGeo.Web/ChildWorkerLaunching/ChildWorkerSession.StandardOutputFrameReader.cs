using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Web.ChildWorkerLaunching;

internal sealed partial class ChildWorkerSession
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private enum StandardOutputReadKind { Frame, EndOfStream, FramingFailure }

    private readonly record struct StandardOutputReadResult(StandardOutputReadKind Kind, ReadOnlyMemory<byte> Frame, WorkerProtocolFailureCode FailureCode)
    {
        internal static StandardOutputReadResult EndOfStream() => new(StandardOutputReadKind.EndOfStream, ReadOnlyMemory<byte>.Empty, default);
        internal static StandardOutputReadResult CompletedFrame(ReadOnlyMemory<byte> frame) => new(StandardOutputReadKind.Frame, frame, default);
        internal static StandardOutputReadResult Failed(WorkerProtocolFailureCode code) => new(StandardOutputReadKind.FramingFailure, ReadOnlyMemory<byte>.Empty, code);
    }

    private sealed class StandardOutputFrameReader
    {
        private readonly byte[] _readBuffer = new byte[ReadBufferBytes];
        private readonly byte[] _frameBuffer = new byte[WorkerProtocolV1.MaxMessageBytes + 1];
        private readonly char[] _decoderCharacters = new char[2];
        private Decoder _decoder = StrictUtf8.GetDecoder();
        private int _readOffset;
        private int _readCount;
        private int _frameCount;

        internal bool Failed { get; private set; }

        internal void StopParsing() => Failed = true;

        internal ValueTask<int> StartRead(Stream input) =>
            input.ReadAsync(_readBuffer.AsMemory(), CancellationToken.None);

        internal Task<StandardOutputReadResult> ReadAsync(Stream input, ValueTask<int> pendingRead) =>
            ReadAsyncCore(input, pendingRead);

        internal Task<StandardOutputReadResult> ReadAsync(Stream input) =>
            ReadAsyncCore(input, null);

        private async Task<StandardOutputReadResult> ReadAsyncCore(Stream input, ValueTask<int>? pendingRead)
        {
            while (true)
            {
                if (_readOffset == _readCount)
                {
                    if (pendingRead.HasValue)
                    {
                        _readCount = await pendingRead.Value.ConfigureAwait(false);
                        pendingRead = null;
                    }
                    else
                    {
                        _readCount = await input.ReadAsync(_readBuffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                    }

                    _readOffset = 0;
                    if (_readCount == 0)
                    {
                        if (_frameCount != 0 && !Failed)
                        {
                            return StandardOutputReadResult.Failed(TryCompleteUtf8() ? WorkerProtocolFailureCode.InvalidFraming : WorkerProtocolFailureCode.InvalidEncoding);
                        }

                        return StandardOutputReadResult.EndOfStream();
                    }
                }

                var value = _readBuffer[_readOffset++];
                if (Failed)
                {
                    continue;
                }

                if (_frameCount > 0 && _frameBuffer[_frameCount - 1] == (byte)'\r' && value != (byte)'\n')
                {
                    return StandardOutputReadResult.Failed(_frameCount > WorkerProtocolV1.MaxMessageBytes
                        ? WorkerProtocolFailureCode.MessageTooLarge
                        : WorkerProtocolFailureCode.InvalidFraming);
                }

                if (value == (byte)'\n')
                {
                    if (!TryCompleteUtf8())
                    {
                        return StandardOutputReadResult.Failed(WorkerProtocolFailureCode.InvalidEncoding);
                    }

                    var length = _frameCount > 0 && _frameBuffer[_frameCount - 1] == (byte)'\r' ? _frameCount - 1 : _frameCount;
                    if (length == 0)
                    {
                        return StandardOutputReadResult.Failed(WorkerProtocolFailureCode.InvalidFraming);
                    }

                    if (length >= 3 && _frameBuffer[0] == 0xef && _frameBuffer[1] == 0xbb && _frameBuffer[2] == 0xbf)
                    {
                        return StandardOutputReadResult.Failed(WorkerProtocolFailureCode.InvalidEncoding);
                    }

                    var frame = new ReadOnlyMemory<byte>(_frameBuffer, 0, length);
                    ResetFrame();
                    return StandardOutputReadResult.CompletedFrame(frame);
                }

                if (!TryAppend(value))
                {
                    return StandardOutputReadResult.Failed(WorkerProtocolFailureCode.MessageTooLarge);
                }

                if (!TryDecode())
                {
                    return StandardOutputReadResult.Failed(WorkerProtocolFailureCode.InvalidEncoding);
                }
            }
        }

        private bool TryAppend(byte value)
        {
            if (_frameCount == WorkerProtocolV1.MaxMessageBytes)
            {
                return value == (byte)'\r' && Append(value);
            }

            return _frameCount < WorkerProtocolV1.MaxMessageBytes && Append(value);
        }

        private bool Append(byte value) { _frameBuffer[_frameCount++] = value; return true; }
        private bool TryDecode() { try { _decoder.GetChars(_frameBuffer, _frameCount - 1, 1, _decoderCharacters, 0, false); return true; } catch (DecoderFallbackException) { return false; } }
        private bool TryCompleteUtf8() { try { _decoder.GetChars([], 0, 0, _decoderCharacters, 0, true); return true; } catch (DecoderFallbackException) { return false; } }
        private void ResetFrame() { _frameCount = 0; _decoder = StrictUtf8.GetDecoder(); }
    }
}
