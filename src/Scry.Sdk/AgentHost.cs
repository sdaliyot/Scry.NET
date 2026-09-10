using System.Text.Json;
using Scry.Contracts;
using Scry.Runtime;

namespace Scry.Sdk;

public sealed class AgentHostOptions
{
    public string Alias { get; init; } = Environment.ProcessPath is { } path
        ? Path.GetFileNameWithoutExtension(path)
        : "managed-process";

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
}

public sealed class AgentBuilder
{
    internal AgentConfiguration Configuration { get; } = new();

    public AgentBuilder RegisterRoot(string name, Func<object?> valueFactory, string? description = null)
    {
        Configuration.AddRoot(name, valueFactory, description);
        return this;
    }

    public AgentBuilder RegisterValue(string name, object? value, string? description = null) =>
        RegisterRoot(name, () => value, description);

    public AgentBuilder RegisterOperation(
        string name,
        Func<JsonElement, CancellationToken, ValueTask<object?>> handler,
        string? description = null)
    {
        Configuration.AddOperation(name, handler, description);
        return this;
    }

    public AgentBuilder RegisterOperation(
        string name,
        Func<JsonElement, object?> handler,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterOperation(
            name,
            (arguments, _) => ValueTask.FromResult(handler(arguments)),
            description);
    }

    public AgentBuilder RegisterJobOperation(
        string name,
        Func<JsonElement, OperationExecutionContext, ValueTask<object?>> handler,
        string? description = null)
    {
        Configuration.AddContextualOperation(name, handler, description);
        return this;
    }
}

public sealed class AgentHost : IAsyncDisposable, IDisposable
{
    private readonly RuntimeHost _runtime;

    private AgentHost(RuntimeHost runtime)
    {
        _runtime = runtime;
    }

    public TargetMetadata Metadata => _runtime.Metadata;

    public string TargetId => _runtime.Metadata.TargetId;

    public string DescriptorPath => _runtime.DescriptorPath;

    public static AgentHost Start(
        Action<AgentBuilder>? configure = null,
        AgentHostOptions? options = null)
    {
        var builder = new AgentBuilder();
        configure?.Invoke(builder);
        var selected = options ?? new AgentHostOptions();
        return new(RuntimeHost.Start(
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
            }));
    }

    public void Dispose() => _runtime.Dispose();

    public ValueTask DisposeAsync() => _runtime.DisposeAsync();
}
