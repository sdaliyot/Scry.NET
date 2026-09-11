using System.Text.Json;
using Scry.Contracts;
using Scry.Runtime;

namespace Scry.Sdk;

public sealed class AgentHostOptions
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

public enum AgentRegistrationMode
{
    RejectDuplicate,
    ReplaceExisting
}

public enum AgentOperationExecutionPolicy
{
    WorkerThread,
    UiOwner
}

public sealed record AgentOperationPolicy
{
    public AgentOperationExecutionPolicy ExecutionPolicy { get; init; } =
        AgentOperationExecutionPolicy.WorkerThread;

    public bool IsReadOnly { get; init; }

    public bool RequiresConfirmation { get; init; }
}

public sealed class AgentRegistrationRegistry
{
    internal AgentRegistrationRegistry(AgentConfiguration configuration)
    {
        Configuration = configuration;
    }

    internal AgentConfiguration Configuration { get; }

    public AgentRegistrationRegistry RegisterRoot(
        string name,
        Func<object?> valueFactory,
        string? description = null,
        AgentRegistrationMode mode = AgentRegistrationMode.RejectDuplicate)
    {
        Configuration.AddRoot(
            name,
            valueFactory,
            description,
            mode == AgentRegistrationMode.ReplaceExisting);
        return this;
    }

    public AgentRegistrationRegistry RegisterValue(
        string name,
        object? value,
        string? description = null,
        AgentRegistrationMode mode = AgentRegistrationMode.RejectDuplicate) =>
        RegisterRoot(name, () => value, description, mode);

    public AgentRegistrationRegistry RegisterOperation(
        string name,
        Func<JsonElement, CancellationToken, ValueTask<object?>> handler,
        string? description = null,
        AgentOperationPolicy? policy = null,
        AgentRegistrationMode mode = AgentRegistrationMode.RejectDuplicate)
    {
        var selectedPolicy = policy ?? new AgentOperationPolicy();
        Configuration.AddOperation(
            name,
            handler,
            description,
            ToWireName(selectedPolicy.ExecutionPolicy),
            selectedPolicy.IsReadOnly,
            selectedPolicy.RequiresConfirmation,
            mode == AgentRegistrationMode.ReplaceExisting);
        return this;
    }

    public AgentRegistrationRegistry RegisterOperation(
        string name,
        Func<JsonElement, object?> handler,
        string? description = null,
        AgentOperationPolicy? policy = null,
        AgentRegistrationMode mode = AgentRegistrationMode.RejectDuplicate)
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

    public AgentRegistrationRegistry RegisterJobOperation(
        string name,
        Func<JsonElement, OperationExecutionContext, ValueTask<object?>> handler,
        string? description = null,
        AgentOperationPolicy? policy = null,
        AgentRegistrationMode mode = AgentRegistrationMode.RejectDuplicate)
    {
        var selectedPolicy = policy ?? new AgentOperationPolicy();
        Configuration.AddContextualOperation(
            name,
            handler,
            description,
            ToWireName(selectedPolicy.ExecutionPolicy),
            selectedPolicy.IsReadOnly,
            selectedPolicy.RequiresConfirmation,
            mode == AgentRegistrationMode.ReplaceExisting);
        return this;
    }

    public bool UnregisterRoot(string name) => Configuration.RemoveRoot(name);

    public bool UnregisterOperation(string name) => Configuration.RemoveOperation(name);

    private static string ToWireName(AgentOperationExecutionPolicy policy) =>
        policy switch
        {
            AgentOperationExecutionPolicy.WorkerThread => "worker-thread",
            AgentOperationExecutionPolicy.UiOwner => "ui-owner",
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
        };
}

public sealed class AgentBuilder
{
    public AgentBuilder()
    {
        Registrations = new(new AgentConfiguration());
    }

    internal AgentConfiguration Configuration => Registrations.Configuration;

    internal AgentRegistrationRegistry Registrations { get; }

    public AgentBuilder RegisterRoot(
        string name,
        Func<object?> valueFactory,
        string? description = null) =>
        RegisterRoot(name, valueFactory, description, AgentRegistrationMode.RejectDuplicate);

    public AgentBuilder RegisterRoot(
        string name,
        Func<object?> valueFactory,
        string? description,
        AgentRegistrationMode mode)
    {
        Registrations.RegisterRoot(name, valueFactory, description, mode);
        return this;
    }

    public AgentBuilder RegisterValue(
        string name,
        object? value,
        string? description = null) =>
        RegisterValue(name, value, description, AgentRegistrationMode.RejectDuplicate);

    public AgentBuilder RegisterValue(
        string name,
        object? value,
        string? description,
        AgentRegistrationMode mode)
    {
        Registrations.RegisterValue(name, value, description, mode);
        return this;
    }

    public AgentBuilder RegisterOperation(
        string name,
        Func<JsonElement, CancellationToken, ValueTask<object?>> handler,
        string? description = null) =>
        RegisterOperation(
            name,
            handler,
            description,
            policy: null,
            AgentRegistrationMode.RejectDuplicate);

    public AgentBuilder RegisterOperation(
        string name,
        Func<JsonElement, CancellationToken, ValueTask<object?>> handler,
        string? description,
        AgentOperationPolicy? policy,
        AgentRegistrationMode mode = AgentRegistrationMode.RejectDuplicate)
    {
        Registrations.RegisterOperation(name, handler, description, policy, mode);
        return this;
    }

    public AgentBuilder RegisterOperation(
        string name,
        Func<JsonElement, object?> handler,
        string? description = null) =>
        RegisterOperation(
            name,
            handler,
            description,
            policy: null,
            AgentRegistrationMode.RejectDuplicate);

    public AgentBuilder RegisterOperation(
        string name,
        Func<JsonElement, object?> handler,
        string? description,
        AgentOperationPolicy? policy,
        AgentRegistrationMode mode = AgentRegistrationMode.RejectDuplicate)
    {
        Registrations.RegisterOperation(name, handler, description, policy, mode);
        return this;
    }

    public AgentBuilder RegisterJobOperation(
        string name,
        Func<JsonElement, OperationExecutionContext, ValueTask<object?>> handler,
        string? description = null) =>
        RegisterJobOperation(
            name,
            handler,
            description,
            policy: null,
            AgentRegistrationMode.RejectDuplicate);

    public AgentBuilder RegisterJobOperation(
        string name,
        Func<JsonElement, OperationExecutionContext, ValueTask<object?>> handler,
        string? description,
        AgentOperationPolicy? policy,
        AgentRegistrationMode mode = AgentRegistrationMode.RejectDuplicate)
    {
        Registrations.RegisterJobOperation(name, handler, description, policy, mode);
        return this;
    }

    /// <summary>
    /// Lets evaluate/execute accept <c>"marshal": "ui"</c> by supplying the thread to run on. The
    /// WPF and Windows Forms adapters call this for you; register it directly only for a host with
    /// its own single-threaded context.
    /// </summary>
    public AgentBuilder UseExecutionMarshaller(ExecutionMarshaller marshaller)
    {
        Configuration.SetExecutionMarshaller(marshaller);
        return this;
    }
}

public sealed class AgentHost : IAsyncDisposable, IDisposable
{
    private readonly RuntimeHost _runtime;

    private AgentHost(RuntimeHost runtime, AgentRegistrationRegistry registrations)
    {
        _runtime = runtime;
        Registrations = registrations;
    }

    public TargetMetadata Metadata => _runtime.Metadata;

    public string TargetId => _runtime.Metadata.TargetId;

    public string DescriptorPath => _runtime.DescriptorPath;

    public AgentRegistrationRegistry Registrations { get; }

    public static AgentHost Start(
        Action<AgentBuilder>? configure = null,
        AgentHostOptions? options = null)
    {
        var builder = new AgentBuilder();
        configure?.Invoke(builder);
        var selected = options ?? new AgentHostOptions();
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
                MaximumJobLogMessageLength = selected.MaximumJobLogMessageLength
            });
        return new(runtime, builder.Registrations);
    }

    public void Dispose() => _runtime.Dispose();

    public ValueTask DisposeAsync() => _runtime.DisposeAsync();
}
