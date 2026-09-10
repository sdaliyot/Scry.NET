using System.Text.Json;

namespace Scry.Runtime;

public sealed class RuntimeHostOptions
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

public sealed class AgentConfiguration
{
    private readonly Dictionary<string, RegisteredRoot> _roots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegisteredOperation> _operations = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, RegisteredRoot> Roots => _roots;

    public IReadOnlyDictionary<string, RegisteredOperation> Operations => _operations;

    public void AddRoot(string name, Func<object?> valueFactory, string? description = null)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(valueFactory);
        if (!_roots.TryAdd(name, new(name, valueFactory, description)))
        {
            throw new ArgumentException($"A root named '{name}' is already registered.", nameof(name));
        }
    }

    public void AddOperation(
        string name,
        Func<JsonElement, CancellationToken, ValueTask<object?>> handler,
        string? description = null)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_operations.TryAdd(name, new(name, handler, description)))
        {
            throw new ArgumentException($"An operation named '{name}' is already registered.", nameof(name));
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            throw new ArgumentException("Registration names must contain 1-128 non-whitespace characters.", nameof(name));
        }
    }
}

public sealed record RegisteredRoot(string Name, Func<object?> ValueFactory, string? Description);

public sealed record RegisteredOperation(
    string Name,
    Func<JsonElement, CancellationToken, ValueTask<object?>> Handler,
    string? Description);

internal sealed class ScryOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
