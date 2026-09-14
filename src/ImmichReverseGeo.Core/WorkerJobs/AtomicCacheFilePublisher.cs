using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace ImmichReverseGeo.Core.WorkerJobs;

public interface ICacheFilePublisher
{
    void Publish(string candidatePath, string finalPath);
}

public sealed class CachePublicationException : IOException
{
    public CachePublicationException(Exception innerException)
        : base("The cache replacement could not be published atomically.", innerException)
    {
    }
}

public sealed class AtomicCacheFilePublisher : ICacheFilePublisher
{
    private const uint MoveFileReplaceExisting = 0x1;

    public void Publish(string candidatePath, string finalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);

        string candidate = Path.GetFullPath(candidatePath);
        string final = Path.GetFullPath(finalPath);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(candidate, final, comparison)
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(candidate) ?? string.Empty),
                Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(final) ?? string.Empty),
                comparison))
        {
            throw new ArgumentException(
                "Atomic cache publication requires distinct sibling files.",
                nameof(candidatePath));
        }

        int result;
        if (OperatingSystem.IsWindows())
        {
            result = MoveFileEx(candidate, final, MoveFileReplaceExisting) ? 0 : -1;
        }
        else if (OperatingSystem.IsLinux()
            || OperatingSystem.IsMacOS()
            || OperatingSystem.IsFreeBSD())
        {
            result = Rename(candidate, final);
        }
        else
        {
            throw new PlatformNotSupportedException(
                "Atomic cache publication is not supported on this platform.");
        }

        if (result != 0)
        {
            throw new CachePublicationException(
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    [DllImport(
        "libc",
        EntryPoint = "rename",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        ExactSpelling = true)]
    private static extern int Rename(string oldPath, string newPath);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "MoveFileExW",
        SetLastError = true,
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);
}
