using System.Text.Json;
using Scry.Contracts;
using Scry.Runtime;

namespace Scry.Endpoint;

public sealed class EndpointOptions
{
    public string Alias { get; init; } = DefaultAlias();

    public TimeSpan HandleLease { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan SessionLease { get; init; } = TimeSpan.FromMinutes(30);

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public int MaximumPreviewLength { get; init; } = 256;

    public int MaximumHandlesPerSession { get; init; } = 4096;

    public int MaximumSessions { get; init; } = 256;

    public int MaximumSourceLength { get; init; } = 256 * 1024;

    public int MaximumExecutionMilliseconds { get; init; } = 120_000;

    public int DefaultExecutionMilliseconds { get; init; } = 30_000;

    public int MaximumExecutionReferences { get; init; } = 256;

    /// <summary>
    /// How many compiled submissions to keep, bounding the script cache.
    /// </summary>
    public int MaximumCachedScripts { get; init; } = 64;

    public int MaximumExecutionImports { get; init; } = 64;

    public int MaximumLogEntries { get; init; } = 256;

    public int MaximumLogMessageLength { get; init; } = 4096;

    public int MaximumTypeResults { get; init; } = 1000;

    public int MaximumTypeMembers { get; init; } = 2000;

    public long MaximumAssemblyBytes { get; init; } = 256L * 1024 * 1024;

    public TimeSpan JobRetention { get; init; } = TimeSpan.FromMinutes(15);

    public int MaximumJobs { get; init; } = 1024;

    public int MaximumJobLogEntries { get; init; } = 1000;

    public int MaximumJobLogMessageLength { get; init; } = 4096;

    /// <summary>
    /// Whether to write the rolling audit file under <c>%LOCALAPPDATA%\Scry\audit</c>. On by
    /// default, deliberately: attach mode cannot be configured by the target application (it does
    /// not cooperate), so a zero-configuration default is the only way an injected endpoint is
    /// auditable at all.
    /// </summary>
    public bool AuditEnabled { get; init; } = true;

    /// <summary>Overrides where the audit file is written. Null uses the default directory.</summary>
    public string? AuditDirectory { get; init; }

    /// <summary>
    /// Overrides the rendezvous directory the connection descriptor is published into. Null uses
    /// the default directory. See <see cref="Scry.Runtime.RuntimeHostOptions.TargetsDirectory"/>
    /// for when this is needed.
    /// </summary>
    public string? TargetsDirectory { get; init; }

    /// <summary>
    /// Starts a loopback TCP listener alongside the named pipe when set: null (the default) starts
    /// no listener, 0 binds an OS-assigned free port, and 1-65535 binds that fixed port. See
    /// <see cref="Scry.Runtime.RuntimeHostOptions.TcpPort"/> for the trust-boundary implications.
    /// </summary>
    public int? TcpPort { get; init; }

    /// <summary>
    /// Receives every audit record in addition to (or instead of, with <see cref="AuditEnabled"/>
    /// false) the file. Called on a background thread; a throwing or slow handler affects nothing
    /// but its own records.
    /// </summary>
    public AuditRecordHandler? AuditCallback { get; init; }

    private static string DefaultAlias()
    {
#if NETFRAMEWORK
        return Path.GetFileNameWithoutExtension(AppDomain.CurrentDomain.FriendlyName);
#else
        return Environment.ProcessPath is { } path
            ? Path.GetFileNameWithoutExtension(path)
            : "managed-process";
#endif
    }
}

public enum RegistrationMode
{
    RejectDuplicate,
    ReplaceExisting
}

public enum OperationExecutionPolicy
{
    WorkerThread,
    UiOwner
}

public sealed record OperationPolicy
{
    public OperationExecutionPolicy ExecutionPolicy { get; init; } =
        OperationExecutionPolicy.WorkerThread;

    public bool IsReadOnly { get; init; }

    public bool RequiresConfirmation { get; init; }
}

public sealed class RegistrationRegistry
{
    internal RegistrationRegistry(EndpointConfiguration configuration)
    {
        Configuration = configuration;
    }

    internal EndpointConfiguration Configuration { get; }

    public RegistrationRegistry RegisterRoot(
        string name,
        Func<object?> valueFactory,
        string? description = null,
        RegistrationMode mode = RegistrationMode.RejectDuplicate)
    {
        Configuration.AddRoot(
            name,
            valueFactory,
            description,
            mode == RegistrationMode.ReplaceExisting);
        return this;
    }

    public RegistrationRegistry RegisterValue(
        string name,
        object? value,
        string? description = null,
        RegistrationMode mode = RegistrationMode.RejectDuplicate) =>
        RegisterRoot(name, () => value, description, mode);

    public RegistrationRegistry RegisterOperation(
        string name,
        Func<JsonElement, CancellationToken, ValueTask<object?>> handler,
        string? description = null,
        OperationPolicy? policy = null,
        RegistrationMode mode = RegistrationMode.RejectDuplicate)
    {
        var selectedPolicy = policy ?? new OperationPolicy();
        Configuration.AddOperation(
            name,
            handler,
            description,
            ToWireName(selectedPolicy.ExecutionPolicy),
            selectedPolicy.IsReadOnly,
            selectedPolicy.RequiresConfirmation,
            mode == RegistrationMode.ReplaceExisting);
        return this;
    }

    public RegistrationRegistry RegisterOperation(
        string name,
        Func<JsonElement, object?> handler,
        string? description = null,
        OperationPolicy? policy = null,
        RegistrationMode mode = RegistrationMode.RejectDuplicate)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        return RegisterOperation(
            name,
            (arguments, _) => new ValueTask<object?>(handler(arguments)),
            description,
            policy,
            mode);
    }

    public RegistrationRegistry RegisterJobOperation(
        string name,
        Func<JsonElement, OperationExecutionContext, ValueTask<object?>> handler,
        string? description = null,
        OperationPolicy? policy = null,
        RegistrationMode mode = RegistrationMode.RejectDuplicate)
    {
        var selectedPolicy = policy ?? new OperationPolicy();
        Configuration.AddContextualOperation(
            name,
            handler,
            description,
            ToWireName(selectedPolicy.ExecutionPolicy),
            selectedPolicy.IsReadOnly,
            selectedPolicy.RequiresConfirmation,
            mode == RegistrationMode.ReplaceExisting);
        return this;
    }

    public bool UnregisterRoot(string name) => Configuration.RemoveRoot(name);

    public bool UnregisterOperation(string name) => Configuration.RemoveOperation(name);

    private static string ToWireName(OperationExecutionPolicy policy) =>
        policy switch
        {
            OperationExecutionPolicy.WorkerThread => "worker-thread",
            OperationExecutionPolicy.UiOwner => "ui-owner",
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
        };
}

public sealed class EndpointBuilder
{
    public EndpointBuilder()
    {
        Registrations = new(new EndpointConfiguration());
    }

    internal EndpointConfiguration Configuration => Registrations.Configuration;

    internal RegistrationRegistry Registrations { get; }

    public EndpointBuilder RegisterRoot(
        string name,
        Func<object?> valueFactory,
        string? description = null) =>
        RegisterRoot(name, valueFactory, description, RegistrationMode.RejectDuplicate);

    public EndpointBuilder RegisterRoot(
        string name,
        Func<object?> valueFactory,
        string? description,
        RegistrationMode mode)
    {
        Registrations.RegisterRoot(name, valueFactory, description, mode);
        return this;
    }

    public EndpointBuilder RegisterValue(
        string name,
        object? value,
        string? description = null) =>
        RegisterValue(name, value, description, RegistrationMode.RejectDuplicate);

    public EndpointBuilder RegisterValue(
        string name,
        object? value,
        string? description,
        RegistrationMode mode)
    {
        Registrations.RegisterValue(name, value, description, mode);
        return this;
    }

    public EndpointBuilder RegisterOperation(
        string name,
        Func<JsonElement, CancellationToken, ValueTask<object?>> handler,
        string? description = null) =>
        RegisterOperation(
            name,
            handler,
            description,
            policy: null,
            RegistrationMode.RejectDuplicate);

    public EndpointBuilder RegisterOperation(
        string name,
        Func<JsonElement, CancellationToken, ValueTask<object?>> handler,
        string? description,
        OperationPolicy? policy,
        RegistrationMode mode = RegistrationMode.RejectDuplicate)
    {
        Registrations.RegisterOperation(name, handler, description, policy, mode);
        return this;
    }

    public EndpointBuilder RegisterOperation(
        string name,
        Func<JsonElement, object?> handler,
        string? description = null) =>
        RegisterOperation(
            name,
            handler,
            description,
            policy: null,
            RegistrationMode.RejectDuplicate);

    public EndpointBuilder RegisterOperation(
        string name,
        Func<JsonElement, object?> handler,
        string? description,
        OperationPolicy? policy,
        RegistrationMode mode = RegistrationMode.RejectDuplicate)
    {
        Registrations.RegisterOperation(name, handler, description, policy, mode);
        return this;
    }

    public EndpointBuilder RegisterJobOperation(
        string name,
        Func<JsonElement, OperationExecutionContext, ValueTask<object?>> handler,
        string? description = null) =>
        RegisterJobOperation(
            name,
            handler,
            description,
            policy: null,
            RegistrationMode.RejectDuplicate);

    public EndpointBuilder RegisterJobOperation(
        string name,
        Func<JsonElement, OperationExecutionContext, ValueTask<object?>> handler,
        string? description,
        OperationPolicy? policy,
        RegistrationMode mode = RegistrationMode.RejectDuplicate)
    {
        Registrations.RegisterJobOperation(name, handler, description, policy, mode);
        return this;
    }

    /// <summary>
    /// Lets evaluate/execute accept <c>"marshal": "ui"</c> by supplying the thread to run on. The
    /// WPF and Windows Forms adapters call this for you; register it directly only for a host with
    /// its own single-threaded context.
    /// </summary>
    public EndpointBuilder UseExecutionMarshaller(ExecutionMarshaller marshaller)
    {
        Configuration.SetExecutionMarshaller(marshaller);
        return this;
    }
}

public sealed class EndpointHost : IAsyncDisposable, IDisposable
{
    private readonly RuntimeHost _runtime;

    private EndpointHost(RuntimeHost runtime, RegistrationRegistry registrations)
    {
        _runtime = runtime;
        Registrations = registrations;
    }

    public TargetMetadata Metadata => _runtime.Metadata;

    public string TargetId => _runtime.Metadata.TargetId;

    public string DescriptorPath => _runtime.DescriptorPath;

    /// <summary>The bound TCP port, or null when no TCP listener was started.</summary>
    public int? TcpPort => _runtime.Descriptor.TcpPort;

    public RegistrationRegistry Registrations { get; }

    public static EndpointHost Start(
        Action<EndpointBuilder>? configure = null,
        EndpointOptions? options = null)
    {
        var builder = new EndpointBuilder();
        configure?.Invoke(builder);
        var selected = options ?? new EndpointOptions();
        var runtime = RuntimeHost.Start(
            builder.Configuration,
            new RuntimeHostOptions
            {
                Alias = selected.Alias,
                Aliases = selected.Aliases,
                HandleLease = selected.HandleLease,
                SessionLease = selected.SessionLease,
                MaximumPreviewLength = selected.MaximumPreviewLength,
                MaximumHandlesPerSession = selected.MaximumHandlesPerSession,
                MaximumSessions = selected.MaximumSessions,
                MaximumSourceLength = selected.MaximumSourceLength,
                MaximumExecutionMilliseconds = selected.MaximumExecutionMilliseconds,
                DefaultExecutionMilliseconds = selected.DefaultExecutionMilliseconds,
                MaximumExecutionReferences = selected.MaximumExecutionReferences,
                MaximumCachedScripts = selected.MaximumCachedScripts,
                MaximumExecutionImports = selected.MaximumExecutionImports,
                MaximumLogEntries = selected.MaximumLogEntries,
                MaximumLogMessageLength = selected.MaximumLogMessageLength,
                MaximumTypeResults = selected.MaximumTypeResults,
                MaximumTypeMembers = selected.MaximumTypeMembers,
                MaximumAssemblyBytes = selected.MaximumAssemblyBytes,
                JobRetention = selected.JobRetention,
                MaximumJobs = selected.MaximumJobs,
                MaximumJobLogEntries = selected.MaximumJobLogEntries,
                MaximumJobLogMessageLength = selected.MaximumJobLogMessageLength,
                AuditEnabled = selected.AuditEnabled,
                AuditDirectory = selected.AuditDirectory,
                AuditCallback = selected.AuditCallback,
                TcpPort = selected.TcpPort,
                TargetsDirectory = selected.TargetsDirectory
            });
        return new(runtime, builder.Registrations);
    }

    public void Dispose() => _runtime.Dispose();

    public ValueTask DisposeAsync() => _runtime.DisposeAsync();
}
