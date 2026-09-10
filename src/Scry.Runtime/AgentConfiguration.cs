using System.Text.Json;

namespace Scry.Runtime;

public sealed class RuntimeHostOptions
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
#if NET48
        return Path.GetFileNameWithoutExtension(AppDomain.CurrentDomain.FriendlyName);
#else
        return Environment.ProcessPath is { } path
            ? Path.GetFileNameWithoutExtension(path)
            : "managed-process";
#endif
    }
}

public sealed class AgentConfiguration
{
    private readonly Dictionary<string, RegisteredRoot> _roots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegisteredOperation> _operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContextualOperation> _contextualOperations = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, RegisteredRoot> Roots => _roots;

    public IReadOnlyDictionary<string, RegisteredOperation> Operations => _operations;

    public void AddRoot(string name, Func<object?> valueFactory, string? description = null)
    {
        ValidateName(name);
        if (valueFactory is null)
        {
            throw new ArgumentNullException(nameof(valueFactory));
        }

        if (_roots.ContainsKey(name))
        {
            throw new ArgumentException($"A root named '{name}' is already registered.", nameof(name));
        }

        _roots.Add(name, new(name, valueFactory, description));
    }

    public void AddOperation(
        string name,
        Func<JsonElement, CancellationToken, ValueTask<object?>> handler,
        string? description = null)
    {
        ValidateName(name);
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        if (_contextualOperations.ContainsKey(name) || _operations.ContainsKey(name))
        {
            throw new ArgumentException($"An operation named '{name}' is already registered.", nameof(name));
        }

        _operations.Add(name, new(name, handler, description));
    }

    public void AddContextualOperation(
        string name,
        Func<JsonElement, OperationExecutionContext, ValueTask<object?>> handler,
        string? description = null)
    {
        ValidateName(name);
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        if (_operations.ContainsKey(name) || _contextualOperations.ContainsKey(name))
        {
            throw new ArgumentException($"An operation named '{name}' is already registered.", nameof(name));
        }

        _contextualOperations.Add(name, new(name, handler, description));
    }

    internal IEnumerable<(string Name, string? Description)> DescribeOperations() =>
        _operations.Values
            .Select(operation => (operation.Name, operation.Description))
            .Concat(_contextualOperations.Values.Select(operation => (operation.Name, operation.Description)));

    internal bool TryGetContextualOperation(string name, out ContextualOperation operation) =>
        _contextualOperations.TryGetValue(name, out operation!);

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

internal sealed record ContextualOperation(
    string Name,
    Func<JsonElement, OperationExecutionContext, ValueTask<object?>> Handler,
    string? Description);

public sealed class OperationExecutionContext
{
    private readonly Action<string, string>? _log;

    internal OperationExecutionContext(
        CancellationToken cancellationToken,
        string operationId,
        string correlationId,
        Action<string, string>? log = null)
    {
        CancellationToken = cancellationToken;
        OperationId = operationId;
        CorrelationId = correlationId;
        _log = log;
    }

    public CancellationToken CancellationToken { get; }

    public string OperationId { get; }

    public string CorrelationId { get; }

    public void Log(string message, string level = "information")
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(message));
        }

        if (string.IsNullOrWhiteSpace(level))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(level));
        }

        _log?.Invoke(level, message);
    }
}

internal sealed class ScryOperationException(
    string code,
    string message,
    IReadOnlyDictionary<string, string>? errorData = null) : Exception(message)
{
    public string Code { get; } = code;

    public IReadOnlyDictionary<string, string>? ErrorData { get; } = errorData;
}
