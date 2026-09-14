using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ImmichReverseGeo.Core.WorkerJobs;

public interface ICacheCandidateLease : IDisposable
{
    string CandidatePath { get; }
}

public interface ICacheCandidateOwnership
{
    ICacheCandidateLease Acquire(string candidatePath);

    bool TryCleanupAbandoned(string candidatePath);
}

public sealed class CacheCandidateOwnershipException : IOException
{
    internal CacheCandidateOwnershipException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class CacheCandidateOwnership : ICacheCandidateOwnership
{
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int LockUnlock = 8;
    private const string OwnerSuffix = ".owner";
    private const string Marker = "immich-reversegeo-cache-candidate-v1\n";
    private static readonly byte[] MarkerBytes = Encoding.ASCII.GetBytes(Marker);
    private readonly Func<SafeFileHandle, bool> _tryAcquireUnixLock;
    private readonly Action<SafeFileHandle> _releaseUnixLock;
    private readonly Func<string, bool> _tryDeleteCandidate;

    public CacheCandidateOwnership()
        : this(TryAcquireUnixLock, ReleaseUnixLock)
    {
    }

    internal CacheCandidateOwnership(
        Func<SafeFileHandle, bool> tryAcquireUnixLock,
        Action<SafeFileHandle>? releaseUnixLock = null,
        Func<string, bool>? tryDeleteCandidate = null)
    {
        _tryAcquireUnixLock = tryAcquireUnixLock
            ?? throw new ArgumentNullException(nameof(tryAcquireUnixLock));
        _releaseUnixLock = releaseUnixLock ?? (static _ => { });
        _tryDeleteCandidate = tryDeleteCandidate ?? TryDelete;
    }

    public ICacheCandidateLease Acquire(string candidatePath)
    {
        string fullCandidatePath = RequireCandidatePath(candidatePath);
        string ownerPath = fullCandidatePath + OwnerSuffix;
        FileStream? stream = null;
        bool unixLockHeld = false;
        bool created = false;
        try
        {
            stream = OpenOwner(ownerPath, FileMode.CreateNew);
            created = true;
            unixLockHeld = AcquirePlatformLock(stream.SafeFileHandle);
            stream.Write(MarkerBytes);
            stream.Flush(flushToDisk: true);
            return new Lease(
                fullCandidatePath,
                ownerPath,
                stream,
                unixLockHeld,
                _releaseUnixLock);
        }
        catch (Exception exception)
        {
            if (unixLockHeld && stream is not null)
            {
                ReleaseSafely(stream.SafeFileHandle, _releaseUnixLock);
            }

            stream?.Dispose();
            if (created)
            {
                TryDelete(ownerPath);
            }

            if (exception is OutOfMemoryException)
            {
                throw;
            }

            throw new CacheCandidateOwnershipException(
                "The cache candidate ownership lease could not be established.",
                exception);
        }
    }

    public bool TryCleanupAbandoned(string candidatePath)
    {
        string fullCandidatePath;
        try
        {
            fullCandidatePath = RequireCandidatePath(candidatePath);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }

        string ownerPath = fullCandidatePath + OwnerSuffix;
        FileStream? stream = null;
        bool unixLockHeld = false;
        bool recognized = false;
        bool candidateDeleted = false;
        try
        {
            stream = OpenOwner(ownerPath, FileMode.Open);
            unixLockHeld = AcquirePlatformLock(stream.SafeFileHandle);
            recognized = HasMarker(stream);
            if (!recognized)
            {
                return false;
            }

            candidateDeleted = _tryDeleteCandidate(fullCandidatePath);
            return candidateDeleted;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            if (unixLockHeld && stream is not null)
            {
                ReleaseSafely(stream.SafeFileHandle, _releaseUnixLock);
            }

            stream?.Dispose();
            if (recognized && candidateDeleted)
            {
                TryDelete(ownerPath);
            }
        }
    }

    internal static string GetOwnerPath(string candidatePath) =>
        RequireCandidatePath(candidatePath) + OwnerSuffix;

    internal static byte[] GetMarkerBytes() => [.. MarkerBytes];

    private bool AcquirePlatformLock(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (!_tryAcquireUnixLock(handle))
            {
                throw new IOException("The cache candidate ownership lock is unavailable.");
            }

            return true;
        }

        throw new PlatformNotSupportedException(
            "Cache candidate ownership is supported only on Windows, Linux, and macOS.");
    }

    private static FileStream OpenOwner(string path, FileMode mode) =>
        new(
            path,
            mode,
            FileAccess.ReadWrite,
            OperatingSystem.IsWindows() ? FileShare.None : FileShare.ReadWrite,
            bufferSize: 1,
            FileOptions.None);

    private static bool HasMarker(FileStream stream)
    {
        if (stream.Length != MarkerBytes.Length)
        {
            return false;
        }

        stream.Position = 0;
        byte[] actual = new byte[MarkerBytes.Length];
        stream.ReadExactly(actual);
        return actual.AsSpan().SequenceEqual(MarkerBytes);
    }

    private static string RequireCandidatePath(string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            throw new ArgumentException("A cache candidate path is required.", nameof(candidatePath));
        }

        string fullPath = Path.GetFullPath(candidatePath);
        if (!fullPath.EndsWith(".tmp", StringComparison.Ordinal)
            && !fullPath.EndsWith(".gpkg.download", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The path must identify a recognized cache candidate.",
                nameof(candidatePath));
        }

        return fullPath;
    }

    private static bool TryAcquireUnixLock(SafeFileHandle handle)
    {
        int result = Flock(
            checked((int)handle.DangerousGetHandle()),
            LockExclusive | LockNonBlocking);
        return result == 0;
    }

    private static void ReleaseUnixLock(SafeFileHandle handle)
    {
        if (Flock(checked((int)handle.DangerousGetHandle()), LockUnlock) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static void ReleaseSafely(
        SafeFileHandle handle,
        Action<SafeFileHandle> release)
    {
        try
        {
            release(handle);
        }
        catch
        {
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fileDescriptor, int operation);

    private sealed class Lease(
        string candidatePath,
        string ownerPath,
        FileStream stream,
        bool unixLockHeld,
        Action<SafeFileHandle> releaseUnixLock) : ICacheCandidateLease
    {
        private int _disposed;

        public string CandidatePath { get; } = candidatePath;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            bool candidateDeleted = TryDelete(CandidatePath);
            if (unixLockHeld)
            {
                ReleaseSafely(stream.SafeFileHandle, releaseUnixLock);
            }

            stream.Dispose();
            if (candidateDeleted)
            {
                TryDelete(ownerPath);
            }
        }
    }
}
