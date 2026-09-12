using System.Text.Json;
using System.Collections.ObjectModel;

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

    /// <summary>
    /// How many compiled submissions to keep. Bounds the script cache so a client sending many
    /// distinct submissions cannot grow it without limit.
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

/// <summary>
/// Runs a submission on a thread the host nominates - in practice a UI thread - and returns its
/// result. Deliberately framework-agnostic: the runtime never references a UI framework, so the
/// dispatcher is supplied by whoever owns one. The optional WPF and Windows Forms adapters register
/// an implementation over the dispatcher they already hold.
/// </summary>
public delegate Task<object?> ExecutionMarshaller(
    Func<Task<object?>> callback,
    CancellationToken cancellationToken);

public sealed class EndpointConfiguration
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RegisteredRoot> _roots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegisteredOperation> _operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContextualOperation> _contextualOperations = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, RegisteredRoot> Roots
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyDictionary<string, RegisteredRoot>(
                    new Dictionary<string, RegisteredRoot>(_roots, StringComparer.Ordinal));
            }
        }
    }

    public IReadOnlyDictionary<string, RegisteredOperation> Operations
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyDictionary<string, RegisteredOperation>(
                    new Dictionary<string, RegisteredOperation>(_operations, StringComparer.Ordinal));
            }
        }
    }

    /// <summary>
    /// Null unless the host registered one, in which case evaluate/execute accept
    /// <see cref="ExecutionMarshalTargets.UiThread"/>.
    /// </summary>
    public ExecutionMarshaller? ExecutionMarshaller { get; private set; }

    public void SetExecutionMarshaller(ExecutionMarshaller marshaller)
    {
        if (marshaller is null)
        {
            throw new ArgumentNullException(nameof(marshaller));
        }

        if (ExecutionMarshaller is not null)
        {
            throw new InvalidOperationException("An execution marshaller is already registered.");
        }

        ExecutionMarshaller = marshaller;
    }

    public void AddRoot(string name, Func<object?> valueFactory, string? description = null) =>
        AddRoot(name, valueFactory, description, replaceExisting: false);

    public void AddRoot(
        string name,
        Func<object?> valueFactory,
        string? description,
        bool replaceExisting)
    {
        ValidateName(name);
        if (valueFactory is null)
        {
            throw new ArgumentNullException(nameof(valueFactory));
        }

        lock (_gate)
        {
            if (!replaceExisting && _roots.ContainsKey(name))
            {
                throw new ArgumentException($"A root named '{name}' is already registered.", nameof(name));
            }

            _roots[name] = new(name, valueFactory, description);
        }
    }

    public void AddOperation(
        string name,
        Func<JsonElement, CancellationToken, ValueTask<object?>> handler,
        string? description = null) =>
        AddOperation(
            name,
            handler,
            description,
            "worker-thread",
            isReadOnly: false,
            requiresConfirmation: false,
            replaceExisting: false);

    public void AddOperation(
        string name,
        Func<JsonElement, CancellationToken, ValueTask<object?>> handler,
        string? description,
        string executionPolicy,
        bool isReadOnly,
        bool requiresConfirmation,
        bool replaceExisting)
    {
        ValidateName(name);
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        lock (_gate)
        {
            if (!replaceExisting &&
                (_operations.ContainsKey(name) || _contextualOperations.ContainsKey(name)))
            {
                throw new ArgumentException($"An operation named '{name}' is already registered.", nameof(name));
            }

            _contextualOperations.Remove(name);
            _operations[name] = new(
                name,
                handler,
                description,
                executionPolicy,
                isReadOnly,
                requiresConfirmation);
        }
    }

    public void AddContextualOperation(
        string name,
        Func<JsonElement, OperationExecutionContext, ValueTask<object?>> handler,
        string? description = null) =>
        AddContextualOperation(
            name,
            handler,
            description,
            "worker-thread",
            isReadOnly: false,
            requiresConfirmation: false,
            replaceExisting: false);

    public void AddContextualOperation(
        string name,
        Func<JsonElement, OperationExecutionContext, ValueTask<object?>> handler,
        string? description,
        string executionPolicy,
        bool isReadOnly,
        bool requiresConfirmation,
        bool replaceExisting)
    {
        ValidateName(name);
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        lock (_gate)
        {
            if (!replaceExisting &&
                (_operations.ContainsKey(name) || _contextualOperations.ContainsKey(name)))
            {
                throw new ArgumentException($"An operation named '{name}' is already registered.", nameof(name));
            }

            _operations.Remove(name);
            _contextualOperations[name] = new(
                name,
                handler,
                description,
                executionPolicy,
                isReadOnly,
                requiresConfirmation);
        }
    }

    public bool RemoveRoot(string name)
    {
        ValidateName(name);
        lock (_gate)
        {
            return _roots.Remove(name);
        }
    }

    public bool RemoveOperation(string name)
    {
        ValidateName(name);
        lock (_gate)
        {
            return _operations.Remove(name) | _contextualOperations.Remove(name);
        }
    }

    internal IReadOnlyList<RegisteredRoot> GetRoots()
    {
        lock (_gate)
        {
            return _roots.Values.ToArray();
        }
    }

    internal IReadOnlyList<RegisteredOperationDescription> DescribeOperations()
    {
        lock (_gate)
        {
            return _operations.Values
                .Select(ToDescription)
                .Concat(_contextualOperations.Values.Select(ToDescription))
                .ToArray();
        }
    }

    internal bool TryGetRoot(string name, out RegisteredRoot root)
    {
        lock (_gate)
        {
            return _roots.TryGetValue(name, out root!);
        }
    }

    internal bool TryResolveOperation(
        string name,
        out RegisteredOperation? operation,
        out ContextualOperation? contextualOperation)
    {
        lock (_gate)
        {
            if (_contextualOperations.TryGetValue(name, out contextualOperation))
            {
                operation = null;
                return true;
            }

            if (_operations.TryGetValue(name, out operation))
            {
                contextualOperation = null;
                return true;
            }

            operation = null;
            contextualOperation = null;
            return false;
        }
    }

    private static RegisteredOperationDescription ToDescription(RegisteredOperation operation) =>
        new(
            operation.Name,
            operation.Description,
            operation.ExecutionPolicy,
            operation.IsReadOnly,
            operation.RequiresConfirmation);

    private static RegisteredOperationDescription ToDescription(ContextualOperation operation) =>
        new(
            operation.Name,
            operation.Description,
            operation.ExecutionPolicy,
            operation.IsReadOnly,
            operation.RequiresConfirmation);

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
    string? Description,
    string ExecutionPolicy,
    bool IsReadOnly,
    bool RequiresConfirmation)
{
    public RegisteredOperation(
        string name,
        Func<JsonElement, CancellationToken, ValueTask<object?>> handler,
        string? description)
        : this(name, handler, description, "worker-thread", false, false)
    {
    }
}

internal sealed record ContextualOperation(
    string Name,
    Func<JsonElement, OperationExecutionContext, ValueTask<object?>> Handler,
    string? Description,
    string ExecutionPolicy,
    bool IsReadOnly,
    bool RequiresConfirmation);

internal sealed record RegisteredOperationDescription(
    string Name,
    string? Description,
    string ExecutionPolicy,
    bool IsReadOnly,
    bool RequiresConfirmation);

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
