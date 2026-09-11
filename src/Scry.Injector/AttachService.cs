using System.Diagnostics;
using Scry.Contracts;

namespace Scry.Injector;

public sealed class AttachOptions
{
    public string? Alias { get; init; }

    /// <summary>
    /// Optional desktop adapter to wire inside the target: "wpf", "winforms", or null/"none".
    /// Without one an attached endpoint has no wpf.*/winforms.* operations and no execution
    /// marshaller, so evaluate/execute cannot use "marshal": "ui".
    /// </summary>
    public string? Adapters { get; init; }

    public string ComponentRoot { get; init; } = AppContext.BaseDirectory;

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

public static class AttachService
{
    public static Task<AttachResult> AttachAsync(
        string target,
        string? alias = null,
        string? adapters = null,
        CancellationToken cancellationToken = default) =>
        AttachAsync(
            ProcessTargetResolver.Resolve(target),
            new AttachOptions { Alias = alias, Adapters = adapters },
            cancellationToken);

    public static async Task<AttachResult> AttachAsync(
        int processId,
        AttachOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var selected = options ?? new AttachOptions();
        ProcessInspectionResult? inspection = null;
        try
        {
            inspection = ProcessInspector.Inspect(processId);
            ProcessInspector.EnsureCompatibleArchitecture(
                ProcessInspector.CurrentArchitecture,
                inspection.Architecture);

            var existing = await FindDescriptorAsync(inspection, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                throw new InjectionException(
                    InjectionErrorCode.AlreadyInjected,
                    $"Process {processId} already hosts a Scry endpoint.",
                    detail: existing.Value.Path);
            }

            var components = ResolveComponents(inspection, selected.ComponentRoot);

            // Refuse before writing anything into the target. A binding downgrade cannot be
            // detected or recovered from once the payload is running, and the failure would
            // surface inside somebody else's application rather than here.
            var conflict = BindingPolicyInspector.FindConflict(
                inspection,
                Path.GetDirectoryName(components.AssemblyPath)!);
            if (conflict is not null)
            {
                throw new InjectionException(InjectionErrorCode.BindingConflict, conflict);
            }

            NativeBootstrap.Inject(inspection, components, selected.Alias, selected.Adapters);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(selected.StartupTimeout);
            while (true)
            {
                var descriptor = await FindDescriptorAsync(inspection, timeoutSource.Token)
                    .ConfigureAwait(false);
                if (descriptor is not null)
                {
                    return AttachResult.Attached(
                        inspection,
                        descriptor.Value.Descriptor,
                        descriptor.Value.Path);
                }

                try
                {
                    using var process = Process.GetProcessById(processId);
                    if (process.HasExited)
                    {
                        throw new InjectionException(
                            InjectionErrorCode.BootstrapFailed,
                            $"Process {processId} exited before publishing a Scry endpoint.");
                    }
                }
                catch (ArgumentException exception)
                {
                    throw new InjectionException(
                        InjectionErrorCode.BootstrapFailed,
                        $"Process {processId} exited before publishing a Scry endpoint.",
                        innerException: exception);
                }

                await Task.Delay(100, timeoutSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AttachResult.Failed(
                new InjectionError(
                    InjectionErrorCode.BootstrapFailed,
                    $"The injected bootstrap did not publish an endpoint within {selected.StartupTimeout}."),
                inspection);
        }
        catch (InjectionException exception)
        {
            return AttachResult.Failed(exception.ToError(exception.Detail), inspection);
        }
    }

    private static async Task<(ConnectionDescriptor Descriptor, string Path)?> FindDescriptorAsync(
        ProcessInspectionResult inspection,
        CancellationToken cancellationToken)
    {
        var descriptor = (await TargetDiscovery.FindAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate =>
                candidate.Descriptor.Target.ProcessId == inspection.ProcessId &&
                candidate.Descriptor.Target.StartedAt == inspection.StartedAt);
        return descriptor.Descriptor is null ? null : descriptor;
    }

    private static BootstrapComponents ResolveComponents(
        ProcessInspectionResult target,
        string componentRoot)
    {
        var root = Path.GetFullPath(componentRoot);
        var architecture = target.Architecture == TargetArchitecture.X64
            ? "win-x64"
            : "win-x86";
        var framework = target.RuntimeFamily == TargetRuntimeFamily.ModernDotNet
            ? ScryPayloadLayout.ModernDirectory
            : ScryPayloadLayout.NetFrameworkDirectory;
        var helperPath = Path.Combine(
            root,
            "native",
            architecture,
            "Scry.Injector.Native.dll");
        if (!File.Exists(helperPath))
        {
            throw new InjectionException(
                InjectionErrorCode.MissingNativeHelper,
                $"The required {architecture} native bootstrap helper was not found at '{helperPath}'.");
        }

        var payloadDirectory = Path.Combine(root, "payload", framework);
        var assemblyPath = Path.Combine(payloadDirectory, "Scry.Injector.Payload.dll");
        if (!File.Exists(assemblyPath))
        {
            throw new InjectionException(
                InjectionErrorCode.MissingManagedPayload,
                $"The {framework} managed injection payload was not found at '{assemblyPath}'.");
        }

        var runtimeConfigPath = target.RuntimeFamily == TargetRuntimeFamily.ModernDotNet
            ? Path.Combine(payloadDirectory, "Scry.Injector.Payload.runtimeconfig.json")
            : null;
        if (runtimeConfigPath is not null && !File.Exists(runtimeConfigPath))
        {
            throw new InjectionException(
                InjectionErrorCode.MissingManagedPayload,
                $"The modern .NET runtime configuration was not found at '{runtimeConfigPath}'.");
        }

        return new(helperPath, assemblyPath, runtimeConfigPath);
    }
}

internal sealed record BootstrapComponents(
    string HelperPath,
    string AssemblyPath,
    string? RuntimeConfigPath);

/// <summary>
/// Names of the staged payload directories beside the injector. Deliberately not target framework
/// monikers: the directory is chosen from the target's detected CLR family, not from a TFM, so
/// retargeting the .NET Framework leg does not require touching this code. Keep in sync with
/// ScryModernPayloadDirectory / ScryNetFrameworkPayloadDirectory in Scry.Injector.csproj, which
/// produce these directories.
/// </summary>
internal static class ScryPayloadLayout
{
    public const string ModernDirectory = "net";

    public const string NetFrameworkDirectory = "netfx";
}
