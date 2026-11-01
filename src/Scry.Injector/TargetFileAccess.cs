using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Scry.Injector;

/// <summary>
/// Asks Windows whether a target process's own account can open a file, before anything is written
/// into the target. The native helper and the managed payload are both loaded from inside the
/// target - LoadLibraryW runs on a thread the injector starts there, and the payload is loaded by
/// the target's CLR - so the files are opened with the target's token, not the injector's. A
/// service running as a domain account typically cannot read a folder under an administrator's
/// profile, and the remote load then fails with nothing more than a null module handle, which says
/// nothing about why. Asking AccessCheck first turns that into an error naming the account and the
/// file.
/// <para>
/// Fails open. A step that cannot be performed - the target's token cannot be opened, a security
/// descriptor cannot be read - means "unknown", which never refuses an attach. Only a definite
/// "denied" from AccessCheck does, and that is the same evaluation the kernel would make when the
/// target opened the file.
/// </para>
/// </summary>
#if !NETFRAMEWORK
[SupportedOSPlatform("windows")]
#endif
internal static class TargetFileAccess
{
    /// <summary>FILE_GENERIC_READ.</summary>
    public const uint ReadAccess = 0x00120089;

    /// <summary>FILE_GENERIC_READ | FILE_GENERIC_EXECUTE: mapping a DLL as an image needs both.</summary>
    public const uint ReadAndExecuteAccess = 0x00120089 | 0x001200A0;

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const int SecurityIdentification = 1;

    // OWNER | GROUP | DACL | LABEL. AccessCheck needs the owner and group present, and the label is
    // what carries a mandatory integrity policy - without it a no-read-up label would be invisible.
    private const uint SecurityInformation = 0x1 | 0x2 | 0x4 | 0x10;

    /// <summary>
    /// Returns the first of <paramref name="files"/> the process's account is definitely denied, or
    /// null when every file is readable or the answer is unknown.
    /// </summary>
    public static string? FindDenied(int processId, IEnumerable<(string Path, uint Access)> files)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);
        if (process.IsInvalid)
        {
            return null;
        }

        var opened = OpenProcessToken(process, TokenQuery | TokenDuplicate, out var token);
        using var _ = token;
        if (!opened || token.IsInvalid)
        {
            return null;
        }

        // AccessCheck evaluates against an impersonation token; identification level is enough to
        // ask the question without being able to act as the account.
        var duplicated = DuplicateToken(token, SecurityIdentification, out var identification);
        using var __ = identification;
        if (!duplicated || identification.IsInvalid)
        {
            return null;
        }

        foreach (var (path, access) in files)
        {
            if (IsDenied(identification, path, access))
            {
                return path;
            }
        }

        return null;
    }

    private static bool IsDenied(SafeAccessTokenHandle token, string path, uint access)
    {
        var descriptor = TryReadSecurityDescriptor(path);
        if (descriptor is null)
        {
            return false;
        }

        var mapping = new GenericMapping
        {
            GenericRead = ReadAccess,
            GenericWrite = 0x00120116,
            GenericExecute = 0x001200A0,
            GenericAll = 0x001F01FF
        };
        var privileges = new byte[1024];
        var privilegesLength = (uint)privileges.Length;
        if (!AccessCheck(
                descriptor,
                token,
                access,
                ref mapping,
                privileges,
                ref privilegesLength,
                out _,
                out var granted))
        {
            return false;
        }

        return !granted;
    }

    private static byte[]? TryReadSecurityDescriptor(string path)
    {
        // The two-call sizing pattern: the first call reports the size it needed.
        GetFileSecurity(path, SecurityInformation, null, 0, out var required);
        if (required == 0)
        {
            return null;
        }

        var buffer = new byte[required];
        return GetFileSecurity(path, SecurityInformation, buffer, required, out _) ? buffer : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GenericMapping
    {
        public uint GenericRead;
        public uint GenericWrite;
        public uint GenericExecute;
        public uint GenericAll;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateToken(
        SafeAccessTokenHandle existingTokenHandle,
        int impersonationLevel,
        out SafeAccessTokenHandle duplicateTokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetFileSecurityW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSecurity(
        string fileName,
        uint requestedInformation,
        byte[]? securityDescriptor,
        uint length,
        out uint lengthNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AccessCheck(
        byte[] securityDescriptor,
        SafeAccessTokenHandle clientToken,
        uint desiredAccess,
        ref GenericMapping genericMapping,
        byte[] privilegeSet,
        ref uint privilegeSetLength,
        out uint grantedAccess,
        [MarshalAs(UnmanagedType.Bool)] out bool accessStatus);
}
