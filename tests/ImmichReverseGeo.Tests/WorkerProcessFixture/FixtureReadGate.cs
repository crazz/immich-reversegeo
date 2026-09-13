namespace ImmichReverseGeo.Tests.WorkerProcessFixture;

/// <summary>Pauses one real native read after it completed, without inventing bytes, EOF, or exit.</summary>
internal sealed class FixtureReadGate
{
    private int _armed;
    private readonly TaskCompletionSource<int> _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<int> Held => _held.Task;
    internal void Arm() => Interlocked.Exchange(ref _armed, 1);
    internal void Release() => _release.TrySetResult();
    internal Stream Wrap(Stream inner) => new GatedStream(inner, this);

    private sealed class GatedStream(Stream inner, FixtureReadGate owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken);
            if (Interlocked.Exchange(ref owner._armed, 0) != 0)
            {
                owner._held.TrySetResult(read);
                await owner._release.Task.WaitAsync(cancellationToken);
            }
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                owner.Release();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
