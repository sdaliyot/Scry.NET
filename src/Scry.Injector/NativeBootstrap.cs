using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace Scry.Injector;

internal static class NativeBootstrap
{
    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint DontResolveDllReferences = 0x00000001;
    private const uint Th32csSnapModule = 0x00000008;
    private const uint Th32csSnapModule32 = 0x00000010;
    private const uint BootstrapMagic = 0x59524353;
    private const ushort BootstrapVersion = 1;
    private const int BootstrapHeaderSize = 116;
    private const uint BootstrapSuccess = 0;
    private const uint BootstrapAlreadyStarted = 0x2004;
    private const uint BootstrapRuntimeNotLoaded = 0x2101;
    private const uint BootstrapRuntimeNotCompatible = 0x2102;
    private const int ErrorAccessDenied = 5;
    private static readonly nint InvalidHandleValue = new(-1);

    public static void Inject(
        ProcessInspectionResult target,
        BootstrapComponents components,
        string? alias,
        string? adapters)
    {
        var access = ProcessCreateThread |
            ProcessQueryInformation |
            ProcessVmOperation |
            ProcessVmRead |
            ProcessVmWrite;
        using var process = OpenProcess(access, inheritHandle: false, target.ProcessId);
        if (process.IsInvalid)
        {
            ThrowWin32(
                "open the target for injection",
                Marshal.GetLastWin32Error(),
                accessDeniedIsSecurityInterference: false);
        }

        var statusPath = Path.Combine(
            Path.GetTempPath(),
            $"scry-bootstrap-{target.ProcessId}-{Guid.NewGuid():N}.bin");
        try
        {
            var helperBase = LoadHelper(process, target.ProcessId, components.HelperPath);
            var startAddress = ResolveRemoteExport(
                components.HelperPath,
                helperBase,
                "ScryBootstrapStart");
            var hostFxrPath = target.RuntimeFamily == TargetRuntimeFamily.ModernDotNet
                ? FindHostFxrPath(target.ProcessId)
                : null;
            var configuration = BuildConfiguration(
                target,
                components,
                alias,
                adapters,
                hostFxrPath,
                statusPath,
                statusPath + ".managed");

            using var remoteConfiguration = AllocateAndWrite(process, configuration);
            using var bootstrapThread = CreateRemoteThread(
                process,
                0,
                0,
                startAddress,
                remoteConfiguration.DangerousGetHandle(),
                0,
                out _);
            if (bootstrapThread.IsInvalid)
            {
                ThrowWin32(
                    "start the native bootstrap",
                    Marshal.GetLastWin32Error(),
                    accessDeniedIsSecurityInterference: true);
            }

            WaitForThread(bootstrapThread, TimeSpan.FromSeconds(30), "native bootstrap");
            if (!GetExitCodeThread(bootstrapThread, out var result))
            {
                ThrowWin32(
                    "read the native bootstrap result",
                    Marshal.GetLastWin32Error(),
                    accessDeniedIsSecurityInterference: false);
            }

            if (result != BootstrapSuccess)
            {
                ThrowBootstrapFailure(
                    result,
                    TryReadStatus(statusPath),
                    TryReadManagedFailure(statusPath + ".managed"));
            }
        }
        finally
        {
            TryDelete(statusPath);
            TryDelete(statusPath + ".managed");
        }
    }

    private static nint LoadHelper(
        SafeProcessHandle process,
        int processId,
        string helperPath)
    {
        var pathBytes = Encoding.Unicode.GetBytes(helperPath + '\0');
        using var remotePath = AllocateAndWrite(process, pathBytes);
        var localKernel32 = GetModuleHandle("kernel32.dll");
        if (localKernel32 == 0)
        {
            ThrowWin32(
                "resolve local kernel32.dll",
                Marshal.GetLastWin32Error(),
                accessDeniedIsSecurityInterference: false);
        }

        var localLoadLibrary = GetProcAddress(localKernel32, "LoadLibraryW");
        if (localLoadLibrary == 0)
        {
            ThrowWin32(
                "resolve LoadLibraryW",
                Marshal.GetLastWin32Error(),
                accessDeniedIsSecurityInterference: false);
        }

        var remoteKernel32 = FindModule(processId, "kernel32.dll")
            ?? throw new InjectionException(
                InjectionErrorCode.LoaderFailed,
                "kernel32.dll was not found in the target module list.");
        var remoteLoadLibrary = remoteKernel32.BaseAddress +
            (localLoadLibrary - localKernel32);
        using var loaderThread = CreateRemoteThread(
            process,
            0,
            0,
            remoteLoadLibrary,
            remotePath.DangerousGetHandle(),
            0,
            out _);
        if (loaderThread.IsInvalid)
        {
            ThrowWin32(
                "start LoadLibraryW in the target",
                Marshal.GetLastWin32Error(),
                accessDeniedIsSecurityInterference: true);
        }

        WaitForThread(loaderThread, TimeSpan.FromSeconds(15), "remote LoadLibraryW");
        if (!GetExitCodeThread(loaderThread, out var loaderResult))
        {
            ThrowWin32(
                "read the remote loader result",
                Marshal.GetLastWin32Error(),
                accessDeniedIsSecurityInterference: false);
        }

        if (loaderResult == 0)
        {
            throw new InjectionException(
                InjectionErrorCode.LoaderFailed,
                "LoadLibraryW did not load the native helper. The target may enforce a DLL policy or endpoint security may have blocked the load.");
        }

        return FindModule(processId, Path.GetFileName(helperPath))?.BaseAddress
            ?? throw new InjectionException(
                InjectionErrorCode.LoaderFailed,
                "The native helper loader thread completed, but the DLL is absent from the target module list.");
    }

    private static nint ResolveRemoteExport(
        string helperPath,
        nint remoteModule,
        string exportName)
    {
        var localModule = LoadLibraryEx(helperPath, 0, DontResolveDllReferences);
        if (localModule == 0)
        {
            ThrowWin32(
                "map the native helper for export resolution",
                Marshal.GetLastWin32Error(),
                accessDeniedIsSecurityInterference: false);
        }

        try
        {
            var localExport = GetProcAddress(localModule, exportName);
            if (localExport == 0)
            {
                ThrowWin32(
                    $"resolve native export '{exportName}'",
                    Marshal.GetLastWin32Error(),
                    accessDeniedIsSecurityInterference: false);
            }

            return remoteModule + (localExport - localModule);
        }
        finally
        {
            FreeLibrary(localModule);
        }
    }

    private static byte[] BuildConfiguration(
        ProcessInspectionResult target,
        BootstrapComponents components,
        string? alias,
        string? adapters,
        string? hostFxrPath,
        string statusPath,
        string managedErrorPath)
    {
        var bytes = new List<byte>(2048);
        bytes.AddRange(new byte[BootstrapHeaderSize]);
        var references = new BufferReference[8];
        references[0] = AddWideString(bytes, components.AssemblyPath);
        references[1] = AddWideString(bytes, components.RuntimeConfigPath);
        references[2] = AddWideString(
            bytes,
            target.RuntimeFamily == TargetRuntimeFamily.ModernDotNet
                ? "Scry.Injector.Payload.InjectedAgentEntryPoint, Scry.Injector.Payload"
                : "Scry.Injector.Payload.InjectedAgentEntryPoint");
        references[3] = AddWideString(
            bytes,
            target.RuntimeFamily == TargetRuntimeFamily.ModernDotNet
                ? "StartForCoreClr"
                : "StartForNetFramework");
        references[5] = AddWideString(bytes, hostFxrPath);
        references[6] = AddWideString(bytes, statusPath);
        references[7] = default;

        var argumentLines = new List<string>
        {
            "error=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(managedErrorPath))
        };
        if (!string.IsNullOrWhiteSpace(alias))
        {
            argumentLines.Add(
                "alias=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(alias)));
        }

        if (!string.IsNullOrWhiteSpace(adapters))
        {
            argumentLines.Add(
                "adapters=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(adapters)));
        }

        var argument = Encoding.UTF8.GetBytes(string.Join("\n", argumentLines));
        references[4] = AddBytes(bytes, argument);

        WriteUInt32(bytes, 0, BootstrapMagic);
        WriteUInt16(bytes, 4, BootstrapVersion);
        WriteUInt16(bytes, 6, BootstrapHeaderSize);
        WriteUInt32(bytes, 8, checked((uint)bytes.Count));
        WriteUInt32(
            bytes,
            12,
            target.RuntimeFamily == TargetRuntimeFamily.NetFramework ? 1u : 2u);
        for (var index = 0; index < references.Length; index++)
        {
            WriteUInt32(bytes, 20 + index * 8, references[index].Offset);
            WriteUInt32(bytes, 24 + index * 8, references[index].Length);
        }

        return bytes.ToArray();
    }

    private static BufferReference AddWideString(List<byte> destination, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return default;
        }

        if ((destination.Count & 1) != 0)
        {
            destination.Add(0);
        }

        var offset = checked((uint)destination.Count);
        destination.AddRange(Encoding.Unicode.GetBytes(value));
        destination.Add(0);
        destination.Add(0);
        return new(offset, checked((uint)value.Length));
    }

    private static BufferReference AddBytes(List<byte> destination, byte[] value)
    {
        if (value.Length == 0)
        {
            return default;
        }

        var offset = checked((uint)destination.Count);
        destination.AddRange(value);
        return new(offset, checked((uint)value.Length));
    }

    private static void WriteUInt16(List<byte> bytes, int offset, int value)
    {
        var encoded = BitConverter.GetBytes(checked((ushort)value));
        bytes[offset] = encoded[0];
        bytes[offset + 1] = encoded[1];
    }

    private static void WriteUInt32(List<byte> bytes, int offset, uint value)
    {
        var encoded = BitConverter.GetBytes(value);
        for (var index = 0; index < encoded.Length; index++)
        {
            bytes[offset + index] = encoded[index];
        }
    }

    private static RemoteAllocation AllocateAndWrite(
        SafeProcessHandle process,
        byte[] content)
    {
        var address = VirtualAllocEx(
            process,
            0,
            checked((nuint)content.Length),
            MemCommit | MemReserve,
            PageReadWrite);
        if (address == 0)
        {
            ThrowWin32(
                "allocate memory in the target",
                Marshal.GetLastWin32Error(),
                accessDeniedIsSecurityInterference: true);
        }

        var allocation = new RemoteAllocation(process, address);
        if (!WriteProcessMemory(
                process,
                address,
                content,
                checked((nuint)content.Length),
                out var written) ||
            written != checked((nuint)content.Length))
        {
            var error = Marshal.GetLastWin32Error();
            allocation.Dispose();
            ThrowWin32(
                "write bootstrap data into the target",
                error,
                accessDeniedIsSecurityInterference: true);
        }

        return allocation;
    }

    private static void WaitForThread(
        SafeThreadHandle thread,
        TimeSpan timeout,
        string operation)
    {
        var wait = WaitForSingleObject(thread, checked((uint)timeout.TotalMilliseconds));
        if (wait == WaitObject0)
        {
            return;
        }

        if (wait == WaitTimeout)
        {
            throw new InjectionException(
                InjectionErrorCode.SecuritySoftwareInterference,
                $"Timed out waiting for {operation}; process mitigation or endpoint security may have suspended the remote thread.");
        }

        ThrowWin32(
            $"wait for {operation}",
            Marshal.GetLastWin32Error(),
            accessDeniedIsSecurityInterference: false);
    }

    private static void ThrowBootstrapFailure(
        uint result,
        BootstrapStatus? status,
        string? managedFailure)
    {
        var code = result switch
        {
            BootstrapAlreadyStarted => InjectionErrorCode.AlreadyInjected,
            BootstrapRuntimeNotLoaded or BootstrapRuntimeNotCompatible =>
                InjectionErrorCode.UnsupportedClr,
            _ => InjectionErrorCode.BootstrapFailed
        };
        var message = status?.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            message = $"The native bootstrap failed with status 0x{result:x8}.";
        }

        throw new InjectionException(
            code,
            message,
            status?.Win32Error is > 0 ? checked((int)status.Win32Error) : null,
            detail: status is null && managedFailure is null
                ? null
                : string.Join(
                    Environment.NewLine,
                    new[]
                    {
                        status is null
                            ? null
                            : $"stage={status.Stage}; runtimeResult=0x{status.RuntimeResult:x8}; managedResult={status.ManagedResult}",
                        managedFailure
                    }.Where(value => !string.IsNullOrWhiteSpace(value))));
    }

    private static BootstrapStatus? TryReadStatus(string statusPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(statusPath);
            if (bytes.Length != 560 ||
                BitConverter.ToUInt32(bytes, 0) != 560 ||
                BitConverter.ToUInt32(bytes, 4) != BootstrapVersion)
            {
                return null;
            }

            return new(
                BitConverter.ToUInt32(bytes, 8),
                BitConverter.ToUInt32(bytes, 20),
                BitConverter.ToInt32(bytes, 16),
                BitConverter.ToInt32(bytes, 24),
                Encoding.Unicode.GetString(bytes, 48, 512).TrimEnd('\0'));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryReadManagedFailure(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ModuleRecord? FindModule(int processId, string moduleName) =>
        EnumerateModules(processId).FirstOrDefault(module =>
            string.Equals(module.Name, moduleName, StringComparison.OrdinalIgnoreCase));

    private static string? FindHostFxrPath(int processId)
    {
        var modules = EnumerateModules(processId);
        var loaded = modules.FirstOrDefault(module =>
            string.Equals(module.Name, "hostfxr.dll", StringComparison.OrdinalIgnoreCase));
        if (loaded is not null)
        {
            return loaded.Path;
        }

        var executable = modules.FirstOrDefault();
        if (executable is not null)
        {
            var adjacent = Path.Combine(
                Path.GetDirectoryName(executable.Path) ?? string.Empty,
                "hostfxr.dll");
            if (File.Exists(adjacent))
            {
                return adjacent;
            }
        }

        var coreClr = modules.FirstOrDefault(module =>
            string.Equals(module.Name, "coreclr.dll", StringComparison.OrdinalIgnoreCase));
        var runtimeDirectory = coreClr is null ? null : Path.GetDirectoryName(coreClr.Path);
        var version = runtimeDirectory is null ? null : Path.GetFileName(runtimeDirectory);
        var sharedDirectory = runtimeDirectory is null
            ? null
            : Directory.GetParent(runtimeDirectory)?.Parent?.Parent;
        if (sharedDirectory is null || string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var candidate = Path.Combine(
            sharedDirectory.FullName,
            "host",
            "fxr",
            version,
            "hostfxr.dll");
        return File.Exists(candidate) ? candidate : null;
    }

    private static IReadOnlyList<ModuleRecord> EnumerateModules(int processId)
    {
        using var snapshot = CreateToolhelp32Snapshot(
            Th32csSnapModule | Th32csSnapModule32,
            unchecked((uint)processId));
        if (snapshot.DangerousGetHandle() == InvalidHandleValue)
        {
            ThrowWin32(
                "enumerate target modules",
                Marshal.GetLastWin32Error(),
                accessDeniedIsSecurityInterference: false);
        }

        var entry = new ModuleEntry32 { Size = (uint)Marshal.SizeOf<ModuleEntry32>() };
        if (!Module32First(snapshot, ref entry))
        {
            ThrowWin32(
                "read the target module list",
                Marshal.GetLastWin32Error(),
                accessDeniedIsSecurityInterference: false);
        }

        var modules = new List<ModuleRecord>();
        do
        {
            modules.Add(new(entry.ModuleName, entry.ExePath, entry.ModuleBaseAddress));
            entry.Size = (uint)Marshal.SizeOf<ModuleEntry32>();
        }
        while (Module32Next(snapshot, ref entry));
        return modules;
    }

    private static void ThrowWin32(
        string operation,
        int error,
        bool accessDeniedIsSecurityInterference)
    {
        var code = error == ErrorAccessDenied
            ? accessDeniedIsSecurityInterference
                ? InjectionErrorCode.SecuritySoftwareInterference
                : InjectionErrorCode.PermissionDenied
            : InjectionErrorCode.LoaderFailed;
        throw new InjectionException(
            code,
            $"Could not {operation}: {new Win32Exception(error).Message}",
            error);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class RemoteAllocation : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly SafeProcessHandle _process;

        public RemoteAllocation(SafeProcessHandle process, nint address)
            : base(ownsHandle: true)
        {
            _process = process;
            SetHandle(address);
        }

        public override bool IsInvalid => handle == 0;

        protected override bool ReleaseHandle() =>
            VirtualFreeEx(_process, handle, 0, MemRelease);
    }

    private sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeThreadHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private readonly record struct BufferReference(uint Offset, uint Length);

    private sealed record ModuleRecord(string Name, string Path, nint BaseAddress);

    private sealed record BootstrapStatus(
        uint Stage,
        uint Win32Error,
        int RuntimeResult,
        int ManagedResult,
        string Message);

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
    private static extern nint VirtualAllocEx(
        SafeProcessHandle process,
        nint address,
        nuint size,
        uint allocationType,
        uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(
        SafeProcessHandle process,
        nint address,
        nuint size,
        uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        SafeProcessHandle process,
        nint address,
        byte[] buffer,
        nuint size,
        out nuint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeThreadHandle CreateRemoteThread(
        SafeProcessHandle process,
        nint threadAttributes,
        nuint stackSize,
        nint startAddress,
        nint parameter,
        uint creationFlags,
        out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        SafeThreadHandle handle,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeThread(
        SafeThreadHandle thread,
        out uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetProcAddress(nint module, string procedureName);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(
        string fileName,
        nint file,
        uint flags);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(nint module);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

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
