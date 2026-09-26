using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Web.Services;

internal sealed class SettingsDocumentStore(string path) : ISettingsDocumentStore
{
    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, true);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async Task PublishAsync(byte[] document, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string candidate = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = CreateCandidate(candidate))
            {
                await stream.WriteAsync(document, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            bool replacing = File.Exists(path);
            if (replacing)
            {
                PrepareReplacement(path, candidate);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (replacing)
            {
                File.Replace(candidate, path, null);
            }
            else
            {
                File.Move(candidate, path);
            }
        }
        finally
        {
            try
            {
                File.Delete(candidate);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static FileStream CreateCandidate(string candidate)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new IOException("Unable to restrict settings candidate access.");
            var permissions = new FileSecurity();
            permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            permissions.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(candidate).Create(
                FileMode.CreateNew,
                FileSystemRights.FullControl,
                FileShare.None,
                4096,
                FileOptions.Asynchronous,
                permissions);
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        };
        var stream = new FileStream(candidate, options);
        try
        {
            SettingsFilePermissions.EnsureCandidateIsPrivate(candidate);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static void PrepareReplacement(string original, string candidate)
    {
        if (!OperatingSystem.IsWindows())
        {
            SettingsFilePermissions.EnsureCanReplace(original, candidate);
            File.SetUnixFileMode(candidate, File.GetUnixFileMode(original));
        }
    }
}
