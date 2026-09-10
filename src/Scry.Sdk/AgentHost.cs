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

    public int MaximumPreviewLength { get; init; } = 256;

    public int MaximumHandlesPerSession { get; init; } = 4096;

    public int MaximumSessions { get; init; } = 256;
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
                HandleLease = selected.HandleLease,
                SessionLease = selected.SessionLease,
                MaximumPreviewLength = selected.MaximumPreviewLength,
                MaximumHandlesPerSession = selected.MaximumHandlesPerSession,
                MaximumSessions = selected.MaximumSessions
            }));
    }

    public void Dispose() => _runtime.Dispose();

    public ValueTask DisposeAsync() => _runtime.DisposeAsync();
}
