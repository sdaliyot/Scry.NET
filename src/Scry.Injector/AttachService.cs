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

    /// <summary>
    /// Starts a loopback TCP listener inside the attached endpoint alongside the named pipe: null
    /// (the default) starts no listener, 0 binds a free port, 1-65535 binds that fixed port. See
    /// <c>RuntimeHostOptions.TcpPort</c> for the trust-boundary implications.
    /// </summary>
    public int? TcpPort { get; init; }

    /// <summary>
    /// Overrides the rendezvous directory both this attach and the endpoint it injects use. Null
    /// uses the default directory. See <c>RuntimeHostOptions.TargetsDirectory</c> for when this is
    /// needed - most commonly, attaching to a process running under a different Windows identity
    /// than this one, such as an IIS application pool or a service account.
    /// </summary>
    public string? TargetsDirectory { get; init; }

    /// <summary>
    /// Places the endpoint in a specific AppDomain of the target process instead of the default
    /// one: an id (<see cref="System.AppDomain.Id"/>), a friendly name or a stable prefix of one (an
    /// ASP.NET application's own id, for example, survives its domain's volatile trailing recycle
    /// sequence), or <c>"auto"</c> - the single non-default domain when there is exactly one, else
    /// the default. Null (the default) never attempts a hop. Only meaningful on .NET Framework, and
    /// on modern .NET is silently ignored.
    /// <para>
    /// A selector that cannot be honoured - it matches zero or several domains, the domain's own
    /// binding policy conflicts with the payload, or the hop itself fails - does not fail the
    /// attach: the native injection that got this far cannot be retried without recycling the
    /// target, so the endpoint starts in the default domain instead, and
    /// <c>TargetMetadata.AppDomainSelectionWarning</c> says why.
    /// </para>
    /// </summary>
    public string? AppDomain { get; init; }

    public string ComponentRoot { get; init; } = AppContext.BaseDirectory;

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

public static class AttachService
{
    public static Task<AttachResult> AttachAsync(
        string target,
        string? alias = null,
        string? adapters = null,
        int? tcpPort = null,
        string? targetsDirectory = null,
        string? appDomain = null,
        CancellationToken cancellationToken = default) =>
        AttachAsync(
            ProcessTargetResolver.Resolve(target),
            new AttachOptions
            {
                Alias = alias,
                Adapters = adapters,
                TcpPort = tcpPort,
                TargetsDirectory = targetsDirectory,
                AppDomain = appDomain
            },
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
            // Fail before anything else runs: an unrooted override should never surface 15 seconds
            // later as a confusing startup timeout.
            var targetsDirectory = TargetDiscovery.ResolveDirectory(selected.TargetsDirectory);

            inspection = ProcessInspector.Inspect(processId);
            ProcessInspector.EnsureCompatibleArchitecture(
                ProcessInspector.CurrentArchitecture,
                inspection.Architecture);

            var existing = await FindDescriptorAsync(inspection, targetsDirectory, cancellationToken)
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

            NativeBootstrap.Inject(
                inspection,
                components,
                selected.Alias,
                selected.Adapters,
                selected.TcpPort,
                targetsDirectory,
                selected.AppDomain);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(selected.StartupTimeout);
            while (true)
            {
                var descriptor = await FindDescriptorAsync(inspection, targetsDirectory, timeoutSource.Token)
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
            var error = new InjectionError(
                InjectionErrorCode.BootstrapFailed,
                $"The injected bootstrap did not publish an endpoint within {selected.StartupTimeout}.");
            return AttachResult.Failed(
                AppendIdentityDiagnostic(error, processId, selected), inspection);
        }
        catch (InjectionException exception)
        {
            var error = exception.ToError(exception.Detail);
            if (exception.Code == InjectionErrorCode.BootstrapFailed)
            {
                error = AppendIdentityDiagnostic(error, processId, selected);
            }

            return AttachResult.Failed(error, inspection);
        }
    }

    /// <summary>
    /// Explains a bootstrap failure that may be caused by attaching across Windows identities - the
    /// injector cannot see a descriptor the target published under its own identity's rendezvous
    /// directory (when <see cref="AttachOptions.TargetsDirectory"/> was not used to make both sides
    /// agree), and even when it can, the named pipe's owner-only DACL refuses any other identity, so
    /// such an attach requires <see cref="AttachOptions.TcpPort"/>. Best-effort and additive: a
    /// failed identity lookup on either side leaves the original message untouched rather than
    /// implying the identities match.
    /// </summary>
    private static InjectionError AppendIdentityDiagnostic(
        InjectionError error,
        int processId,
        AttachOptions selected)
    {
        // Attach mode is Windows-only end to end (ProcessInspector.Inspect refuses elsewhere), but
        // only this call chain touches APIs the platform-compatibility analyzer marks Windows-only,
        // so the guard is local rather than annotating the whole method. Same two-branch form as
        // ProcessInspector.Inspect, for the same reason: OperatingSystem.IsWindows is .NET 5+, and
        // RuntimeInformation is not inbox on this project's net462 floor (only from 4.7.1).
#if NETFRAMEWORK
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
#else
        if (!OperatingSystem.IsWindows())
#endif
        {
            return error;
        }

        var targetUser = ProcessIdentity.TryGet(processId);
        var thisUser = ProcessIdentity.TryGet(Process.GetCurrentProcess().Id);
        if (targetUser is null || thisUser is null || string.Equals(
                targetUser.Sid, thisUser.Sid, StringComparison.OrdinalIgnoreCase))
        {
            return error;
        }

        var lines = new List<string>
        {
            $"The target process runs as {targetUser}; this injector runs as {thisUser}.",
        };
        lines.Add(
            selected.TargetsDirectory is null
                ? "The target publishes its descriptor under its own identity's rendezvous " +
                  "directory, which this identity cannot see by default. Pass --targets-dir " +
                  "<path> to an absolute directory both identities can write to."
                : "Both identities must be able to write to the --targets-dir directory already " +
                  "given; grant the target's identity access, e.g. " +
                  $"icacls \"{selected.TargetsDirectory}\" /grant \"<target identity>\":(OI)(CI)M");
        if (selected.TcpPort is null)
        {
            lines.Add(
                "The named pipe is protected to the identity that created it, so an attach across " +
                "identities also requires --tcp-port 0 (or a fixed port) and connecting over TCP.");
        }

        var detail = string.Join(" ", lines);
        return new InjectionError(
            error.Kind,
            error.Message,
            error.NativeError,
            error.Detail is null ? detail : error.Detail + " " + detail);
    }

    private static async Task<(ConnectionDescriptor Descriptor, string Path)?> FindDescriptorAsync(
        ProcessInspectionResult inspection,
        string targetsDirectory,
        CancellationToken cancellationToken)
    {
        var descriptor = (await TargetDiscovery.FindAsync(targetsDirectory, cancellationToken)
            .ConfigureAwait(false))
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
