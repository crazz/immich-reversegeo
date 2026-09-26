using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ImmichReverseGeo.Web.Services;

internal static class SettingsFilePermissions
{
    public static void EnsureCandidateIsPrivate(string candidate)
    {
        if (OperatingSystem.IsMacOS())
        {
            RequireNoMacAcl(candidate);
        }
    }

    public static void EnsureCanReplace(string original, string candidate)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            RequireNoMacAcl(original);
            RequireNoMacAcl(candidate);
        }
        else if (!OperatingSystem.IsLinux())
        {
            throw CannotPreserve();
        }

        if (ReadOwnership(original) != ReadOwnership(candidate))
        {
            throw CannotPreserve();
        }

        var originalAttributes = ReadAttributes(original);
        var candidateAttributes = ReadAttributes(candidate);
        if (originalAttributes.Count != candidateAttributes.Count)
        {
            throw CannotPreserve();
        }

        foreach (var attribute in originalAttributes)
        {
            if (!candidateAttributes.TryGetValue(attribute.Key, out var value)
                || !attribute.Value.AsSpan().SequenceEqual(value))
            {
                throw CannotPreserve();
            }
        }
    }

    private static (uint Owner, uint Group) ReadOwnership(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (GetMacStatus(path, out var status) != 0)
            {
                throw CannotPreserve();
            }

            return (status.Owner, status.Group);
        }

        const uint ownerAndGroup = 0x18;
        const int currentDirectory = -100;
        if (GetLinuxStatus(currentDirectory, path, 0, ownerAndGroup, out var linuxStatus) != 0
            || (linuxStatus.Mask & ownerAndGroup) != ownerAndGroup)
        {
            throw CannotPreserve();
        }

        return (linuxStatus.Owner, linuxStatus.Group);
    }

    private static void RequireNoMacAcl(string path)
    {
        const int extendedAcl = 0x100;
        IntPtr acl = GetMacAcl(path, extendedAcl);
        if (acl == IntPtr.Zero)
        {
            if (Marshal.GetLastPInvokeError() == 2)
            {
                return;
            }

            throw CannotPreserve();
        }

        FreeMacAcl(acl);
        throw CannotPreserve();
    }

    private static Dictionary<string, byte[]> ReadAttributes(string path)
    {
        nint length = ListAttributes(path, null, 0);
        if (length < 0 || length > 1024 * 1024)
        {
            throw CannotPreserve();
        }

        var names = new byte[(int)length];
        if (ListAttributes(path, names, (nuint)names.Length) != length)
        {
            throw CannotPreserve();
        }

        var attributes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string name in Encoding.UTF8.GetString(names).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // macOS attaches provenance to newly created files; it is not an access rule.
            if (OperatingSystem.IsMacOS() && name == "com.apple.provenance")
            {
                continue;
            }

            nint size = GetAttribute(path, name, null, 0);
            if (size < 0 || size > 1024 * 1024)
            {
                throw CannotPreserve();
            }

            var value = new byte[(int)size];
            if (GetAttribute(path, name, value, (nuint)value.Length) != size)
            {
                throw CannotPreserve();
            }

            attributes.Add(name, value);
        }

        return attributes;
    }

    private static nint ListAttributes(string path, byte[]? buffer, nuint size)
    {
        return OperatingSystem.IsMacOS()
            ? ListMacAttributes(path, buffer, size, 0)
            : ListLinuxAttributes(path, buffer, size);
    }

    private static nint GetAttribute(string path, string name, byte[]? buffer, nuint size)
    {
        return OperatingSystem.IsMacOS()
            ? GetMacAttribute(path, name, buffer, size, 0, 0)
            : GetLinuxAttribute(path, name, buffer, size);
    }

    private static IOException CannotPreserve() => new("Settings file access restrictions cannot be preserved.");

    // Native stat64/statx buffers include fields beyond the ownership values used here.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct MacStatus
    {
        [FieldOffset(16)] public uint Owner;
        [FieldOffset(20)] public uint Group;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint Owner;
        [FieldOffset(24)] public uint Group;
    }

    [DllImport("libc", EntryPoint = "stat64", SetLastError = true)]
    private static extern int GetMacStatus(string path, out MacStatus status);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int GetLinuxStatus(int directory, string path, int flags, uint mask, out LinuxStatus status);

    [DllImport("libc", EntryPoint = "acl_get_file", SetLastError = true)]
    private static extern IntPtr GetMacAcl(string path, int type);

    [DllImport("libc", EntryPoint = "acl_free")]
    private static extern int FreeMacAcl(IntPtr acl);

    [DllImport("libc", EntryPoint = "listxattr", SetLastError = true)]
    private static extern nint ListMacAttributes(string path, [Out] byte[]? buffer, nuint size, int options);

    [DllImport("libc", EntryPoint = "listxattr", SetLastError = true)]
    private static extern nint ListLinuxAttributes(string path, [Out] byte[]? buffer, nuint size);

    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint GetMacAttribute(string path, string name, [Out] byte[]? buffer, nuint size, uint position, int options);

    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint GetLinuxAttribute(string path, string name, [Out] byte[]? buffer, nuint size);
}
