using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Scry.Injector;

/// <summary>
/// Best-effort lookup of the Windows identity a process runs as, used only to explain an attach
/// failure - never to gate or widen access. <see cref="TryGet"/> never throws: a denied query
/// returns null, and callers must treat that as "unknown", not as "the identities match".
/// Windows-only, like the rest of attach mode; this only makes that explicit to the platform
/// compatibility analyzer, which the .NET Framework leg of this project does not need told.
/// </summary>
#if !NETFRAMEWORK
[SupportedOSPlatform("windows")]
#endif
internal static class ProcessIdentity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    private const int ErrorInsufficientBuffer = 122;

    public static ProcessUser? TryGet(int processId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);
        if (process.IsInvalid)
        {
            return null;
        }

        var opened = OpenProcessToken(process, TokenQuery, out var token);
        using var _ = token;
        if (!opened || token.IsInvalid)
        {
            return null;
        }

        return TryReadUser(token);
    }

    private static ProcessUser? TryReadUser(SafeAccessTokenHandle token)
    {
        // The two-call sizing pattern: the first call reports the buffer size it needed, the
        // second actually fills it. TOKEN_USER is a fixed-size SID_AND_ATTRIBUTES header followed
        // by the variable-length SID itself, so the size cannot be known up front.
        GetTokenInformation(token, TokenUser, (nint)0, 0, out var required);
        if (required == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!GetTokenInformation(token, TokenUser, buffer, required, out _))
            {
                return null;
            }

            var sidPointer = Marshal.ReadIntPtr(buffer);
            var sid = new SecurityIdentifier(sidPointer);
            string? account;
            try
            {
                account = sid.Translate(typeof(NTAccount)).Value;
            }
            catch (IdentityNotMappedException)
            {
                account = null;
            }

            return new ProcessUser(sid.Value, account);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        nint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);
}

/// <summary>The Windows identity a target process runs as, as best determined by <see cref="ProcessIdentity"/>.</summary>
/// <param name="Sid">The security identifier, always present when this record exists at all.</param>
/// <param name="Account">
/// The <c>DOMAIN\name</c> form, or null when the SID could not be translated to an account name -
/// which happens for some service SIDs, or when the injector's identity cannot resolve names in the
/// target's domain.
/// </param>
internal sealed record ProcessUser(string Sid, string? Account)
{
    public override string ToString() => Account is null ? Sid : $"{Account} ({Sid})";
}
