using Scry.Contracts;
using System.Text.Json.Serialization;

namespace Scry.Injector;

public enum TargetArchitecture
{
    X86,
    X64
}

public enum TargetRuntimeFamily
{
    NetFramework,
    ModernDotNet
}

public enum InjectionErrorCode
{
    TargetNotFound,
    AmbiguousTarget,
    PermissionDenied,
    ArchitectureMismatch,
    UnsupportedArchitecture,
    UnsupportedClr,
    DetectionInconclusive,
    MissingNativeHelper,
    MissingManagedPayload,
    AlreadyInjected,
    LoaderFailed,
    SecuritySoftwareInterference,
    BootstrapFailed,
    HandshakeFailed,

    /// <summary>
    /// The .NET Framework target's own assembly binding policy would downgrade one of the payload's
    /// dependencies. Injecting anyway would fail inside the target process rather than here.
    /// </summary>
    BindingConflict
}

public sealed record InjectionError
{
    public InjectionError(
        InjectionErrorCode kind,
        string message,
        int? nativeError = null,
        string? detail = null)
    {
        Kind = kind;
        Message = message;
        NativeError = nativeError;
        Detail = detail;
    }

    [JsonIgnore]
    public InjectionErrorCode Kind { get; }

    public string Code => InjectionErrorNames.ToCode(Kind);

    public string Message { get; }

    public int? NativeError { get; }

    public string? Detail { get; }
}

public sealed record ProcessInspectionResult(
    int ProcessId,
    string ProcessName,
    DateTimeOffset StartedAt,
    TargetArchitecture Architecture,
    TargetRuntimeFamily RuntimeFamily);

public sealed record AttachResult(
    bool Success,
    ProcessInspectionResult? Target,
    ConnectionDescriptor? Descriptor,
    string? DescriptorPath,
    InjectionError? Error)
{
    public static AttachResult Failed(
        InjectionError error,
        ProcessInspectionResult? target = null) =>
        new(false, target, null, null, error);

    public static AttachResult Attached(
        ProcessInspectionResult target,
        ConnectionDescriptor descriptor,
        string descriptorPath) =>
        new(true, target, descriptor, descriptorPath, null);
}

internal static class InjectionErrorNames
{
    public static string ToCode(InjectionErrorCode code) =>
        string.Concat(
            code.ToString().Select(
                (character, index) =>
                    index > 0 && char.IsUpper(character)
                        ? $"_{char.ToLowerInvariant(character)}"
                        : char.ToLowerInvariant(character).ToString()));
}
