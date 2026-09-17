#if NETFRAMEWORK
using System.Runtime.InteropServices;

namespace Scry.Runtime;

/// <summary>
/// Enumerates the AppDomains actually loaded in this .NET Framework process, using
/// <c>ICorRuntimeHost</c> - the general, host-agnostic gate to a non-default AppDomain. (An
/// ASP.NET-specific alternative, <c>System.Web.Hosting.ApplicationManager</c>, was considered and
/// rejected: it only ever sees IIS application domains, not a service's, a test runner's, or a
/// plugin host's.)
/// <para>
/// The interface declaration below is transcribed from the official Windows SDK header
/// (<c>NETFXSDK\4.7.2\Include\um\mscoree.h</c>), <c>ICorRuntimeHost</c>, lines ~1608-1670. COM
/// interop dispatches by vtable slot order, not by name or signature, so every one of its 19
/// methods must be declared here in the exact order the header declares them - the ones this class
/// never calls are declared anyway, as plain stand-ins, purely to hold their slot. Only
/// <see cref="ICorRuntimeHost.EnumDomains"/>, <see cref="ICorRuntimeHost.NextDomain"/> and
/// <see cref="ICorRuntimeHost.CloseEnum"/> are ever invoked, and only their signatures need to be
/// exactly right. <c>GetDefaultDomain</c> is likewise declared to hold its slot but is not called -
/// <see cref="AppDomain.IsDefaultAppDomain"/> on each enumerated domain is simpler and needs no
/// extra COM round trip.
/// </para>
/// <para>
/// <see cref="ICorRuntimeHost.NextDomain"/> hands back what looks like a COM <c>IUnknown</c>, but
/// because the call is serviced in-process by this same CLR, the interop layer recognises the
/// object as one of its own and returns the real, live <see cref="AppDomain"/> reference rather
/// than a proxy - there is nothing further to unwrap.
/// </para>
/// </summary>
internal static class ClrAppDomains
{
    private static readonly Guid CorRuntimeHostClsid = new("CB2F6723-AB3A-11d2-9C40-00C04FA30A3E");

    /// <summary>
    /// Every AppDomain currently loaded in this process, in enumeration order (default domain
    /// first, per <c>ICorRuntimeHost</c>'s own contract).
    /// </summary>
    public static IReadOnlyList<AppDomain> EnumerateDomains()
    {
        var host = CreateHost();
        try
        {
            var domains = new List<AppDomain>();
            ThrowIfFailed(host.EnumDomains(out var handle), "EnumDomains");
            try
            {
                while (true)
                {
                    // NextDomain returns S_FALSE (a positive, non-zero HRESULT) with a null
                    // out-parameter once the enumeration is exhausted - not an error, just done.
                    var hr = host.NextDomain(handle, out var domainObject);
                    if (hr != 0)
                    {
                        ThrowIfFailed(hr < 0 ? hr : 0, "NextDomain");
                        break;
                    }

                    if (domainObject is AppDomain domain)
                    {
                        domains.Add(domain);
                    }
                }
            }
            finally
            {
                host.CloseEnum(handle);
            }

            return domains;
        }
        finally
        {
            if (Marshal.IsComObject(host))
            {
                Marshal.ReleaseComObject(host);
            }
        }
    }

    private static ICorRuntimeHost CreateHost()
    {
        var type = Type.GetTypeFromCLSID(CorRuntimeHostClsid)
            ?? throw new InvalidOperationException(
                "Could not resolve the CorRuntimeHost COM class in this process.");
        return (ICorRuntimeHost)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Could not activate CorRuntimeHost."));
    }

    private static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw new InvalidOperationException(
                $"ICorRuntimeHost.{operation} failed with HRESULT 0x{hresult:x8}.",
                Marshal.GetExceptionForHR(hresult));
        }
    }

    [ComImport]
    [Guid("CB2F6722-AB3A-11d2-9C40-00C04FA30A3E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICorRuntimeHost
    {
        // Slots 0-8: never called. Declared only to hold their position in the vtable so the
        // slots this class does use (10-13) land where mscoree.h says they are.
        [PreserveSig] int CreateLogicalThreadState();
        [PreserveSig] int DeleteLogicalThreadState();
        [PreserveSig] int SwitchInLogicalThreadState(IntPtr fiberCookie);
        [PreserveSig] int SwitchOutLogicalThreadState(out IntPtr fiberCookie);
        [PreserveSig] int LocksHeldByLogicalThread(out uint count);
        [PreserveSig] int MapFile(IntPtr fileHandle, out IntPtr mapAddress);
        [PreserveSig] int GetConfiguration(out IntPtr configuration);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int CreateDomain(IntPtr friendlyName, IntPtr identityArray, out IntPtr appDomain);

        // Slot 10.
        [PreserveSig] int GetDefaultDomain(
            [MarshalAs(UnmanagedType.IUnknown)] out object appDomain);

        // Slot 11.
        [PreserveSig] int EnumDomains(out IntPtr enumerationHandle);

        // Slot 12.
        [PreserveSig] int NextDomain(
            IntPtr enumerationHandle,
            [MarshalAs(UnmanagedType.IUnknown)] out object? appDomain);

        // Slot 13.
        [PreserveSig] int CloseEnum(IntPtr enumerationHandle);

        // Slots 14-18: never called.
        [PreserveSig] int CreateDomainEx(
            IntPtr friendlyName, IntPtr setup, IntPtr evidence, out IntPtr appDomain);
        [PreserveSig] int CreateDomainSetup(out IntPtr appDomainSetup);
        [PreserveSig] int CreateEvidence(out IntPtr evidence);
        [PreserveSig] int UnloadDomain(IntPtr appDomain);
        [PreserveSig] int CurrentDomain([MarshalAs(UnmanagedType.IUnknown)] out object appDomain);
    }
}
#endif
