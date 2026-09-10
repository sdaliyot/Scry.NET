using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Scry.Contracts;

namespace Scry.Runtime;

public sealed class ScryExecutionContext
{
    private readonly Func<ExternalReference, object> _resolver;
    private readonly ExecutionLogBuffer _logs;

    internal ScryExecutionContext(
        string sessionId,
        IReadOnlyDictionary<string, object?> roots,
        Func<ExternalReference, object> resolver,
        CancellationToken cancellationToken,
        ExecutionLogBuffer logs)
    {
        SessionId = sessionId;
        Roots = roots;
        _resolver = resolver;
        CancellationToken = cancellationToken;
        _logs = logs;
    }

    public string SessionId { get; }

    public IReadOnlyDictionary<string, object?> Roots { get; }

    public CancellationToken CancellationToken { get; }

    public object? GetRoot(string name) =>
        Roots.TryGetValue(name, out var value)
            ? value
            : throw new KeyNotFoundException($"Registered root '{name}' does not exist.");

    public object Resolve(ExternalReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return _resolver(reference);
    }

    public void Log(object? value, string level = "information") =>
        _logs.Add(level, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null");
}

public sealed class ExecutionGlobals
{
    internal ExecutionGlobals(ScryExecutionContext context)
    {
        Context = context;
    }

    public ScryExecutionContext Context { get; }
}

internal sealed class ExecutionEngine
{
    private static readonly string[] DefaultImports =
    [
        "System",
        "System.Collections.Generic",
        "System.Linq",
        "System.Threading",
        "System.Threading.Tasks",
        "Scry.Contracts",
        "Scry.Runtime"
    ];

    private readonly AgentConfiguration _configuration;
    private readonly RuntimeHostOptions _options;
    private readonly AssemblyCatalog _assemblies;

    public ExecutionEngine(
        AgentConfiguration configuration,
        RuntimeHostOptions options,
        AssemblyCatalog assemblies)
    {
        _configuration = configuration;
        _options = options;
        _assemblies = assemblies;
    }

    public async ValueTask<ExecutionOutput> EvaluateAsync(
        ExecutionRequest request,
        SessionState session,
        CancellationToken cancellationToken) =>
        await RunAsync(request, session, isStatementBody: false, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ExecutionOutput> ExecuteAsync(
        ExecutionRequest request,
        SessionState session,
        CancellationToken cancellationToken) =>
        await RunAsync(request, session, isStatementBody: true, cancellationToken).ConfigureAwait(false);

    private async ValueTask<ExecutionOutput> RunAsync(
        ExecutionRequest request,
        SessionState session,
        bool isStatementBody,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var timeout = request.TimeoutMilliseconds ?? _options.DefaultExecutionMilliseconds;
        using var timeoutSource = new CancellationTokenSource();
        timeoutSource.CancelAfter(TimeSpan.FromMilliseconds(timeout));
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var executionToken = linkedSource.Token;
        var logs = new ExecutionLogBuffer(
            _options.MaximumLogEntries,
            _options.MaximumLogMessageLength);
        var roots = _configuration.Roots.Values.ToDictionary(
            item => item.Name,
            item => item.ValueFactory(),
            StringComparer.Ordinal);
        var context = new ScryExecutionContext(
            session.Id,
            roots,
            session.Resolve,
            executionToken,
            logs);
        var globals = new ExecutionGlobals(context);
        var options = ScriptOptions.Default
            .WithEmitDebugInformation(false)
            .WithReferences(_assemblies.GetMetadataReferences(
                request.References,
                _options.MaximumExecutionReferences))
            .WithImports(DefaultImports.Concat(request.Imports ?? []).Distinct(StringComparer.Ordinal));
        var source = isStatementBody ? WrapStatementBody(request.Source) : request.Source;
        var script = CSharpScript.Create<object?>(
            source,
            options,
            typeof(ExecutionGlobals));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var diagnostics = script.Compile(executionToken).Select(ToDiagnostic).ToArray();
            var errors = diagnostics
                .Where(diagnostic => diagnostic.Severity == nameof(DiagnosticSeverity.Error))
                .ToArray();
            if (errors.Length != 0)
            {
                throw new ScryCompilationException(errors);
            }

            var state = await script.RunAsync(globals, cancellationToken: executionToken)
                .ConfigureAwait(false);
            var value = await UnwrapAsync(state.ReturnValue).ConfigureAwait(false);
            stopwatch.Stop();
            return new(value, logs.Entries, logs.DroppedCount, diagnostics, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ScryOperationException(
                "execution_timed_out",
                $"Execution observed cancellation after the {timeout}-millisecond deadline.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["cancellation"] = "cooperative",
                    ["timeoutMilliseconds"] = timeout.ToString(CultureInfo.InvariantCulture)
                });
        }
    }

    private void Validate(ExecutionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Source))
        {
            throw new ScryOperationException("invalid_request", "source is required.");
        }

        if (request.Source.Length > _options.MaximumSourceLength)
        {
            throw new ScryOperationException(
                "request_limit_exceeded",
                $"source exceeds the {_options.MaximumSourceLength}-character limit.");
        }

        if (request.Imports is { Count: > 0 } imports)
        {
            if (imports.Count > _options.MaximumExecutionImports ||
                imports.Any(string.IsNullOrWhiteSpace))
            {
                throw new ScryOperationException(
                    "request_limit_exceeded",
                    $"imports must contain at most {_options.MaximumExecutionImports} non-empty entries.");
            }
        }

        var timeout = request.TimeoutMilliseconds ?? _options.DefaultExecutionMilliseconds;
        if (timeout < 1 || timeout > _options.MaximumExecutionMilliseconds)
        {
            throw new ScryOperationException(
                "invalid_request",
                $"timeoutMilliseconds must be between 1 and {_options.MaximumExecutionMilliseconds}.");
        }
    }

    private static string WrapStatementBody(string source) =>
        $$"""
        async Task<object?> __ScryExecuteAsync()
        {
        #line 1 "request.cs"
        {{source}}
        #line default
            return null;
        }
        return await __ScryExecuteAsync();
        """;

    private static CompilationDiagnostic ToDiagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.IsInSource
            ? diagnostic.Location.GetLineSpan()
            : default;
        return new(
            diagnostic.Id,
            diagnostic.Severity.ToString(),
            diagnostic.GetMessage(CultureInfo.InvariantCulture),
            diagnostic.Location.IsInSource ? span.Path : null,
            diagnostic.Location.IsInSource ? span.StartLinePosition.Line + 1 : null,
            diagnostic.Location.IsInSource ? span.StartLinePosition.Character + 1 : null,
            diagnostic.Location.IsInSource ? span.EndLinePosition.Line + 1 : null,
            diagnostic.Location.IsInSource ? span.EndLinePosition.Character + 1 : null);
    }

    private static async ValueTask<object?> UnwrapAsync(object? value)
    {
        if (value is Task task)
        {
            await task.ConfigureAwait(false);
            return task.GetType().IsGenericType
                ? task.GetType().GetProperty("Result")!.GetValue(task)
                : null;
        }

        if (value is ValueTask valueTask)
        {
            await valueTask.ConfigureAwait(false);
            return null;
        }

        if (value is not null &&
            value.GetType().IsGenericType &&
            value.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var convertedTask = (Task)value.GetType().GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance)!
                .Invoke(value, null)!;
            await convertedTask.ConfigureAwait(false);
            return convertedTask.GetType().GetProperty("Result")!.GetValue(convertedTask);
        }

        return value;
    }
}

internal sealed record ExecutionOutput(
    object? Value,
    IReadOnlyList<ExecutionLogEntry> Logs,
    int DroppedLogEntries,
    IReadOnlyList<CompilationDiagnostic> Diagnostics,
    long ElapsedMilliseconds);

internal sealed class ScryCompilationException(IReadOnlyList<CompilationDiagnostic> diagnostics)
    : Exception("C# compilation failed.")
{
    public IReadOnlyList<CompilationDiagnostic> Diagnostics { get; } = diagnostics;
}

internal sealed class ExecutionLogBuffer
{
    private readonly object _gate = new();
    private readonly int _maximumEntries;
    private readonly int _maximumMessageLength;
    private readonly List<ExecutionLogEntry> _entries = [];
    private int _droppedCount;

    public ExecutionLogBuffer(int maximumEntries, int maximumMessageLength)
    {
        _maximumEntries = maximumEntries;
        _maximumMessageLength = maximumMessageLength;
    }

    public IReadOnlyList<ExecutionLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public int DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return _droppedCount;
            }
        }
    }

    public void Add(string level, string message)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            throw new ArgumentException("Log level cannot be empty.", nameof(level));
        }

        lock (_gate)
        {
            if (_entries.Count >= _maximumEntries)
            {
                _droppedCount++;
                return;
            }

            var boundedLevel = level.Length <= 64 ? level : level[..64];
            var bounded = message.Length <= _maximumMessageLength
                ? message
                : message[.._maximumMessageLength];
            _entries.Add(new(DateTimeOffset.UtcNow, boundedLevel, bounded));
        }
    }
}
