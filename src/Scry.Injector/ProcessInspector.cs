using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Scry.Injector;

public static class ProcessInspector
{
    internal const ushort ImageFileMachineUnknown = 0x0000;
    internal const ushort ImageFileMachineI386 = 0x014c;
    internal const ushort ImageFileMachineAmd64 = 0x8664;
    internal const ushort ImageFileMachineArm64 = 0xaa64;

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Th32csSnapModule = 0x00000008;
    private const uint Th32csSnapModule32 = 0x00000010;
    private static readonly nint InvalidHandleValue = (nint)(-1);

    public static ProcessInspectionResult Inspect(int processId)
    {
#if NETFRAMEWORK
        // OperatingSystem.IsWindows is .NET 5+, and RuntimeInformation (which says the same thing)
        // is inbox only from 4.7.1 - one minor version above this project's net462 floor.
        // Environment.OSVersion has been inbox since .NET Framework 1.1, and .NET Framework itself
        // never runs anywhere but Windows NT, so this is exactly as reliable here.
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
#else
        if (!OperatingSystem.IsWindows())
#endif
        {
            throw new InjectionException(
                InjectionErrorCode.UnsupportedClr,
                "Attach mode is supported only on Windows.");
        }

        using var process = GetProcess(processId);
        var startedAt = ReadStartTime(process);
        var architecture = InspectArchitecture(processId);

        // Checked here rather than only by the caller, because the module enumeration below
        // cannot run across a bitness boundary: a 32-bit injector reading a 64-bit process gets
        // ERROR_PARTIAL_COPY, since the module list it is asked to copy holds pointers that do
        // not fit. Left to fail there, a plain architecture mismatch would be reported as
        // detection_inconclusive with a native error code, which says nothing about the actual
        // problem or its fix.
        EnsureCompatibleArchitecture(CurrentArchitecture, architecture);

        var runtimeFamily = ClassifyRuntime(EnumerateModuleNames(processId));
        return new(process.Id, process.ProcessName, startedAt, architecture, runtimeFamily);
    }

    public static TargetArchitecture ClassifyMachine(ushort processMachine, ushort nativeMachine)
    {
        var machine = processMachine == ImageFileMachineUnknown ? nativeMachine : processMachine;
        return machine switch
        {
            ImageFileMachineI386 => TargetArchitecture.X86,
            ImageFileMachineAmd64 => TargetArchitecture.X64,
            _ => throw new InjectionException(
                InjectionErrorCode.UnsupportedArchitecture,
                $"Machine type 0x{machine:x4} is not supported. Scry attach supports only x86 and x64.")
        };
    }

    public static TargetRuntimeFamily ClassifyRuntime(IEnumerable<string> moduleNames)
    {
        // Built via the constructor rather than Enumerable.ToHashSet, which is not inbox on this
        // project's net462 floor (added to .NET Framework only in 4.7.1).
        var modules = new HashSet<string?>(
            moduleNames.Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);
        var hasDesktopClr = modules.Contains("clr.dll");
        var hasCoreClr = modules.Contains("coreclr.dll");

        if (hasDesktopClr == hasCoreClr)
        {
            var code = hasDesktopClr
                ? InjectionErrorCode.DetectionInconclusive
                : InjectionErrorCode.UnsupportedClr;
            var message = hasDesktopClr
                ? "Both CLR v4 and CoreCLR are loaded; selecting a runtime would be unsafe."
                : "No loaded CLR v4 or CoreCLR module was found in the target.";
            throw new InjectionException(code, message);
        }

        return hasDesktopClr
            ? TargetRuntimeFamily.NetFramework
            : TargetRuntimeFamily.ModernDotNet;
    }

    public static TargetArchitecture CurrentArchitecture =>
#if NETFRAMEWORK
        // RuntimeInformation.ProcessArchitecture is not inbox on this project's net462 floor
        // (added to .NET Framework only in 4.7.1). .NET Framework never runs as ARM64 - "no ARM64"
        // is already a stated product-wide limitation - so bitness alone disambiguates x86/x64.
        Environment.Is64BitProcess ? TargetArchitecture.X64 : TargetArchitecture.X86;
#else
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => TargetArchitecture.X86,
            Architecture.X64 => TargetArchitecture.X64,
            _ => throw new InjectionException(
                InjectionErrorCode.UnsupportedArchitecture,
                $"Injector architecture '{RuntimeInformation.ProcessArchitecture}' is not supported.")
        };
#endif

    public static void EnsureCompatibleArchitecture(
        TargetArchitecture injector,
        TargetArchitecture target)
    {
        if (injector != target)
        {
            throw new InjectionException(
                InjectionErrorCode.ArchitectureMismatch,
                $"The {injector.ToString().ToLowerInvariant()} injector cannot attach to an " +
                $"{target.ToString().ToLowerInvariant()} target. Injection writes into the target with " +
                "the loader addresses of its own bitness, so the injector process must match. " +
                $"Publish one with: dotnet publish src/Scry.Injector -c Release -f net8.0 " +
                $"-r win-{target.ToString().ToLowerInvariant()} --self-contained false -o <dir>");
        }
    }

    private static Process GetProcess(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (ArgumentException exception)
        {
            throw new InjectionException(
                InjectionErrorCode.TargetNotFound,
                $"Process {processId} does not exist.",
                innerException: exception);
        }
    }

    private static DateTimeOffset ReadStartTime(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 5)
        {
            throw PermissionDenied(process.Id, exception);
        }
    }

    private static TargetArchitecture InspectArchitecture(int processId)
    {
        using var handle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw error == 5
                ? PermissionDenied(processId, new Win32Exception(error))
                : new InjectionException(
                    InjectionErrorCode.DetectionInconclusive,
                    $"Could not open process {processId} for architecture inspection.",
                    error);
        }

        if (!IsWow64Process2(handle, out var processMachine, out var nativeMachine))
        {
            var error = Marshal.GetLastWin32Error();
            throw new InjectionException(
                InjectionErrorCode.DetectionInconclusive,
                $"Could not determine the architecture of process {processId}.",
                error);
        }

        return ClassifyMachine(processMachine, nativeMachine);
    }

    private static IEnumerable<string> EnumerateModuleNames(int processId)
    {
        using var snapshot = CreateToolhelp32Snapshot(
            Th32csSnapModule | Th32csSnapModule32,
            unchecked((uint)processId));
        if (snapshot.DangerousGetHandle() == InvalidHandleValue)
        {
            var error = Marshal.GetLastWin32Error();
            throw error == 5
                ? PermissionDenied(processId, new Win32Exception(error))
                : new InjectionException(
                    InjectionErrorCode.DetectionInconclusive,
                    $"Could not enumerate modules in process {processId}.",
                    error);
        }

        var entry = new ModuleEntry32 { Size = (uint)Marshal.SizeOf<ModuleEntry32>() };
        if (!Module32First(snapshot, ref entry))
        {
            var error = Marshal.GetLastWin32Error();
            throw new InjectionException(
                InjectionErrorCode.DetectionInconclusive,
                $"Could not inspect loaded CLR modules in process {processId}.",
                error);
        }

        var names = new List<string>();
        do
        {
            names.Add(entry.ModuleName);
            entry.Size = (uint)Marshal.SizeOf<ModuleEntry32>();
        }
        while (Module32Next(snapshot, ref entry));

        return names;
    }

    private static InjectionException PermissionDenied(int processId, Exception innerException) =>
        new(
            InjectionErrorCode.PermissionDenied,
            $"Access to process {processId} was denied. Run the injector at the same or a higher integrity level.",
            5,
            innerException);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ModuleEntry32
    {
        public uint Size;
        public uint ModuleId;
        public uint ProcessId;
        public uint GlobalUsage;
        public uint ProcessUsage;
        public nint ModuleBaseAddress;
        public uint ModuleBaseSize;
        public nint ModuleHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ModuleName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExePath;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(
        SafeProcessHandle process,
        out ushort processMachine,
        out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeSnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Module32FirstW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Module32First(
        SafeSnapshotHandle snapshot,
        ref ModuleEntry32 moduleEntry);

    [DllImport("kernel32.dll", EntryPoint = "Module32NextW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Module32Next(
        SafeSnapshotHandle snapshot,
        ref ModuleEntry32 moduleEntry);
}

public sealed class InjectionException : Exception
{
    public InjectionException(
        InjectionErrorCode code,
        string message,
        int? nativeError = null,
        Exception? innerException = null,
        string? detail = null)
        : base(message, innerException)
    {
        Code = code;
        NativeError = nativeError;
        Detail = detail;
    }

    public InjectionErrorCode Code { get; }

    public int? NativeError { get; }

    public string? Detail { get; }

    public InjectionError ToError(string? detail = null) =>
        new(Code, Message, NativeError, detail ?? Detail);
}
