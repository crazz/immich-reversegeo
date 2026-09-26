using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public sealed class SettingsFilePermissionTests
{
    [TestMethod]
    public async Task InheritedDirectoryReadAccess_DoesNotExposeCandidateContents()
    {
        string directory = Path.Combine(Path.GetTempPath(), "settings-inherited-acl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        string candidate = Path.Combine(directory, ".settings-test.tmp");
        string first = Path.Combine(directory, "first.json");
        try
        {
            var service = new ConfigService(NullLogger<ConfigService>.Instance, directory);
            await service.SaveAppearanceModeAsync(AppearanceModes.Light);
            byte[] prior = await File.ReadAllBytesAsync(path);
            await AddInheritedReadAccessAsync(directory);

            if (OperatingSystem.IsMacOS())
            {
                Assert.ThrowsExactly<IOException>(() =>
                {
                    using var stream = SettingsDocumentStore.CreateCandidate(candidate);
                });
                Assert.AreEqual(0, new FileInfo(candidate).Length);
                using (File.Open(candidate, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }

                File.Delete(candidate);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.SaveAppearanceModeAsync(AppearanceModes.Dark));
                CollectionAssert.AreEqual(prior, await File.ReadAllBytesAsync(path));
                await Assert.ThrowsExactlyAsync<IOException>(() => new SettingsDocumentStore(first).PublishAsync(prior, CancellationToken.None));
                Assert.IsFalse(File.Exists(first));
            }
            else
            {
                await using (var stream = SettingsDocumentStore.CreateCandidate(candidate))
                {
                    AssertPrivateCandidate(candidate);
                    await stream.WriteAsync(prior);
                }

                AssertPrivateCandidate(candidate);
                File.Delete(candidate);
                await new SettingsDocumentStore(first).PublishAsync(prior, CancellationToken.None);
                AssertPrivateCandidate(first);
                CollectionAssert.AreEqual(prior, await File.ReadAllBytesAsync(first));
            }

            Assert.AreEqual(0, Directory.GetFiles(directory, ".settings-*.tmp").Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task CandidateCreation_KeepsContentPrivateUntilPublication()
    {
        string directory = Path.Combine(Path.GetTempPath(), "settings-candidate-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        string candidate = Path.Combine(directory, ".settings-test.tmp");
        byte[] prior = "prior document"u8.ToArray();
        byte[] next = "replacement document"u8.ToArray();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                await AddInheritedReadAccessAsync(directory);
            }

            await File.WriteAllBytesAsync(path, prior);
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var permissions = new FileSecurity();
                permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                permissions.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(permissions);
                await AddReadRestrictionAsync(path);
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            string restrictions = await ReadRestrictionsAsync(path);
            await using (var stream = SettingsDocumentStore.CreateCandidate(candidate))
            {
                Assert.AreEqual(0, stream.Length);
                AssertPrivateCandidate(candidate);
                await stream.WriteAsync(next);
                await stream.FlushAsync();
                AssertPrivateCandidate(candidate);
            }

            AssertPrivateCandidate(candidate);
            CollectionAssert.AreEqual(next, await File.ReadAllBytesAsync(candidate));
            CollectionAssert.AreEqual(prior, await File.ReadAllBytesAsync(path));
            SettingsDocumentStore.PrepareReplacement(path, candidate);
            AssertPrivateCandidate(candidate);
            File.Replace(candidate, path, null);

            CollectionAssert.AreEqual(next, await File.ReadAllBytesAsync(path));
            Assert.AreEqual(restrictions, await ReadRestrictionsAsync(path));
            Assert.IsFalse(File.Exists(candidate));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PreparingRestrictedReplacement_KeepsRejectedCandidatePrivate()
    {
        string directory = Path.Combine(Path.GetTempPath(), "settings-candidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        string candidate = Path.Combine(directory, ".settings-test.tmp");
        try
        {
            var service = new ConfigService(NullLogger<ConfigService>.Instance, directory);
            await service.SaveAppearanceModeAsync(AppearanceModes.Light);
            await AddReadRestrictionAsync(path);
            byte[] prior = await File.ReadAllBytesAsync(path);
            string restrictions = await ReadRestrictionsAsync(path);
            await File.WriteAllBytesAsync(candidate, "private candidate"u8.ToArray());
            if (OperatingSystem.IsWindows())
            {
                string candidateRestrictions = await ReadRestrictionsAsync(candidate);
                SettingsDocumentStore.PrepareReplacement(path, candidate);
                Assert.AreEqual(candidateRestrictions, await ReadRestrictionsAsync(candidate));
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                File.SetUnixFileMode(candidate, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Assert.ThrowsExactly<IOException>(() => SettingsDocumentStore.PrepareReplacement(path, candidate));
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(candidate));
            }

            CollectionAssert.AreEqual(prior, await File.ReadAllBytesAsync(path));
            Assert.AreEqual(restrictions, await ReadRestrictionsAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SavingSettingsWithExistingOwnership_PreservesOwnerAndGroupOrFailsWithoutPublishing()
    {
        string directory = Path.Combine(Path.GetTempPath(), "settings-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var service = new ConfigService(NullLogger<ConfigService>.Instance, directory);
            await service.SaveAppearanceModeAsync(AppearanceModes.Light);
            if (!OperatingSystem.IsWindows())
            {
                string[] ownership = (await ReadOwnershipAsync(path)).Split(':');
                string[] groups = (await RunAsync("/usr/bin/id", "-G")).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string? group = groups.FirstOrDefault(value => value != ownership[1]);
                if (group is null && ownership[0] == "0")
                {
                    group = ownership[1] == "65534" ? "0" : "65534";
                }

                if (group is not null)
                {
                    await RunAsync("/usr/bin/chgrp", group, path);
                }

                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            }

            byte[] prior = await File.ReadAllBytesAsync(path);
            string permissions = await ReadOwnershipAsync(path);
            Exception? error = null;
            try
            {
                await service.SaveAppearanceModeAsync(AppearanceModes.Dark);
            }
            catch (InvalidOperationException failure)
            {
                error = failure;
            }

            Assert.AreEqual(permissions, await ReadOwnershipAsync(path));
            if (error is not null)
            {
                CollectionAssert.AreEqual(prior, await File.ReadAllBytesAsync(path));
                Assert.AreEqual(AppearanceModes.Light, (await service.GetConfigAsync()).Appearance.Mode);
            }
            else
            {
                Assert.AreEqual(AppearanceModes.Dark, (await service.GetConfigAsync()).Appearance.Mode);
            }

            Assert.AreEqual(0, Directory.GetFiles(directory, ".settings-*.tmp").Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SavingAclProtectedSettings_PreservesTheAclOrFailsWithoutPublishing()
    {
        string directory = Path.Combine(Path.GetTempPath(), "settings-acl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var service = new ConfigService(NullLogger<ConfigService>.Instance, directory);
            await service.SaveAppearanceModeAsync(AppearanceModes.Light);
            await AddReadRestrictionAsync(path);
            byte[] prior = await File.ReadAllBytesAsync(path);
            string permissions = await ReadRestrictionsAsync(path);
            Exception? error = null;

            try
            {
                await service.SaveAppearanceModeAsync(AppearanceModes.Dark);
            }
            catch (InvalidOperationException failure)
            {
                error = failure;
            }

            Assert.AreEqual(permissions, await ReadRestrictionsAsync(path));
            if (error is not null)
            {
                CollectionAssert.AreEqual(prior, await File.ReadAllBytesAsync(path));
                Assert.AreEqual(AppearanceModes.Light, (await service.GetConfigAsync()).Appearance.Mode);
            }
            else
            {
                Assert.AreEqual(AppearanceModes.Dark, (await service.GetConfigAsync()).Appearance.Mode);
            }

            Assert.AreEqual(0, Directory.GetFiles(directory, ".settings-*.tmp").Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AddInheritedReadAccessAsync(string directory)
    {
        if (OperatingSystem.IsMacOS())
        {
            await RunAsync("/bin/chmod", "+a", "everyone allow read,file_inherit", directory);
        }
        else if (OperatingSystem.IsWindows())
        {
            var parent = new DirectoryInfo(directory);
            var permissions = parent.GetAccessControl();
            permissions.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            parent.SetAccessControl(permissions);
        }
        else if (OperatingSystem.IsLinux())
        {
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer);
            writer.Write(2);
            foreach (var entry in new[] { (1, 7, uint.MaxValue), (2, 4, 65534u), (4, 5, uint.MaxValue), (16, 5, uint.MaxValue), (32, 5, uint.MaxValue) })
            {
                writer.Write((ushort)entry.Item1);
                writer.Write((ushort)entry.Item2);
                writer.Write(entry.Item3);
            }

            byte[] acl = buffer.ToArray();
            Assert.AreEqual(0, SetXattr(directory, "system.posix_acl_default", acl, (nuint)acl.Length, 0));
        }
        else
        {
            Assert.Fail("This platform does not provide the inherited settings permission fixture.");
        }
    }

    private static void AssertPrivateCandidate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var permissions = new FileInfo(path).GetAccessControl();
            Assert.IsTrue(permissions.AreAccessRulesProtected);
            var rules = permissions.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().ToArray();
            Assert.AreEqual(1, rules.Length);
            Assert.AreEqual(identity.User, rules[0].IdentityReference);
            Assert.AreEqual(AccessControlType.Allow, rules[0].AccessControlType);
            Assert.AreEqual(FileSystemRights.FullControl, rules[0].FileSystemRights);
            Assert.IsFalse(rules[0].IsInherited);
        }
        else
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    private static async Task AddReadRestrictionAsync(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            await RunAsync("/bin/chmod", "+a", "user:nobody deny read", path);
        }
        else if (OperatingSystem.IsLinux())
        {
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer);
            writer.Write(2);
            foreach (var entry in new[] { (1, 6, uint.MaxValue), (2, 0, 65534u), (4, 4, uint.MaxValue), (16, 4, uint.MaxValue), (32, 4, uint.MaxValue) })
            {
                writer.Write((ushort)entry.Item1);
                writer.Write((ushort)entry.Item2);
                writer.Write(entry.Item3);
            }

            byte[] acl = buffer.ToArray();
            Assert.AreEqual(0, SetXattr(path, "system.posix_acl_access", acl, (nuint)acl.Length, 0));
        }
        else if (OperatingSystem.IsWindows())
        {
            var file = new FileInfo(path);
            var permissions = file.GetAccessControl();
            permissions.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.AnonymousSid, null),
                FileSystemRights.ReadData,
                AccessControlType.Deny));
            file.SetAccessControl(permissions);
        }
        else
        {
            Assert.Fail("This platform does not provide the settings permission fixture.");
        }
    }

    private static async Task<string> ReadRestrictionsAsync(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            string listing = await RunAsync("/bin/ls", "-le", path);
            return string.Join('\n', listing.Split('\n').Where(line => line.TrimStart().StartsWith("0:", StringComparison.Ordinal)));
        }

        if (OperatingSystem.IsLinux())
        {
            var buffer = new byte[1024];
            nint count = GetXattr(path, "system.posix_acl_access", buffer, (nuint)buffer.Length);
            return count < 0 ? "absent" : Convert.ToHexString(buffer.AsSpan(0, (int)count));
        }

        if (OperatingSystem.IsWindows())
        {
            return new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        }

        throw new PlatformNotSupportedException();
    }

    private static async Task<string> ReadOwnershipAsync(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Group);
        }

        return OperatingSystem.IsMacOS()
            ? (await RunAsync("/usr/bin/stat", "-f", "%u:%g", path)).Trim()
            : (await RunAsync("/usr/bin/stat", "-c", "%u:%g", path)).Trim();
    }

    private static async Task<string> RunAsync(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, process.ExitCode, await errors);
        return await output;
    }

    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int SetXattr(string path, string name, byte[] value, nuint size, int flags);

    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint GetXattr(string path, string name, byte[] value, nuint size);
}
