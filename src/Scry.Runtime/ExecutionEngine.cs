using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Scry.Contracts;

namespace Scry.Runtime;

public sealed class ScryExecutionContext
{
    /// <summary>Used when a request omits <c>arguments</c>, so <see cref="Arguments"/> always has a
    /// well-defined <see cref="JsonValueKind.Object"/> rather than <see cref="JsonValueKind.Undefined"/>.
    /// Never disposed - a single parsed constant kept for the process lifetime.</summary>
    private static readonly JsonElement EmptyArguments = JsonDocument.Parse("{}").RootElement;

    private readonly Func<ExternalReference, object> _resolver;
    private readonly ExecutionLogBuffer _logs;

    internal ScryExecutionContext(
        string sessionId,
        IReadOnlyDictionary<string, object?> roots,
        JsonElement? arguments,
        Func<ExternalReference, object> resolver,
        CancellationToken cancellationToken,
        ExecutionLogBuffer logs)
    {
        SessionId = sessionId;
        Roots = roots;
        Arguments = arguments ?? EmptyArguments;
        _resolver = resolver;
        CancellationToken = cancellationToken;
        _logs = logs;
    }

    public string SessionId { get; }

    public IReadOnlyDictionary<string, object?> Roots { get; }

    /// <summary>The request's <c>arguments</c> object, or an empty object when omitted.</summary>
    public JsonElement Arguments { get; }

    public CancellationToken CancellationToken { get; }

    public object? GetRoot(string name) =>
        Roots.TryGetValue(name, out var value)
            ? value
            : throw new KeyNotFoundException($"Registered root '{name}' does not exist.");

    /// <summary>
    /// Reads a named value from <see cref="Arguments"/>, deserialized as <typeparamref name="T"/>.
    /// This is how a script should receive a runtime-varying value instead of having it
    /// string-substituted into the script's own source - see <see cref="Arguments"/>.
    /// </summary>
    public T GetArgument<T>(string name)
    {
        if (Arguments.ValueKind != JsonValueKind.Object || !Arguments.TryGetProperty(name, out var value))
        {
            throw new KeyNotFoundException($"Argument '{name}' does not exist.");
        }

        return JsonSerializer.Deserialize<T>(value, ScryJson.Options)!;
    }

    public object Resolve(ExternalReference reference)
    {
        if (reference is null)
        {
            throw new ArgumentNullException(nameof(reference));
        }

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
    {
        "System",
        "System.Collections.Generic",
        "System.Linq",
        "System.Threading",
        "System.Threading.Tasks",
        "Scry.Contracts",
        "Scry.Runtime"
    };

    private readonly EndpointConfiguration _configuration;
    private readonly RuntimeHostOptions _options;
    private readonly AssemblyCatalog _assemblies;
    private readonly ScriptCache _scripts;

    public ExecutionEngine(
        EndpointConfiguration configuration,
        RuntimeHostOptions options,
        AssemblyCatalog assemblies)
    {
        _configuration = configuration;
        _options = options;
        _assemblies = assemblies;
        _scripts = new(options.MaximumCachedScripts);
    }

    /// <summary>
    /// Exposed so a caller building its own per-call <see cref="ExecutionRequest.TimeoutMilliseconds"/>
    /// (<c>OperationDispatcher</c>'s <c>wait</c> polling loop, bounding each iteration to its
    /// remaining budget) can clamp into the same range <see cref="Validate"/> enforces, rather than
    /// discovering the limit via a thrown <c>invalid_request</c>.
    /// </summary>
    internal int MaximumExecutionMilliseconds => _options.MaximumExecutionMilliseconds;

    /// <summary>
    /// Exposed so <c>wait</c>'s polling loop only *raises* a poll iteration's own timeout above this
    /// default (when its own remaining budget is bigger), rather than ever lowering it below this -
    /// a wait with a very small or zero overall timeout is a deliberate "check once, whatever it
    /// costs" idiom used throughout this codebase's own tests, and clamping an iteration's timeout
    /// down to match a tiny wait budget would cripple even an ordinary compile+run that this default
    /// already comfortably covers.
    /// </summary>
    internal int DefaultExecutionMilliseconds => _options.DefaultExecutionMilliseconds;

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
        var marshalToUiThread = MarshalTarget.Resolve(
            request.Marshal,
            _configuration.ExecutionMarshaller);
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
        var roots = _configuration.GetRoots().ToDictionary(
            item => item.Name,
            item => item.ValueFactory(),
            StringComparer.Ordinal);
        var context = new ScryExecutionContext(
            session.Id,
            roots,
            request.Arguments,
            session.Resolve,
            executionToken,
            logs);
        var globals = new ExecutionGlobals(context);
        var imports = DefaultImports.Concat(request.Imports ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var source = isStatementBody ? WrapStatementBody(request.Source) : request.Source;

        // Reuse an already-compiled script when the same submission comes back. wait polls one
        // expression repeatedly, so without this every attempt paid a full compile - and a compile
        // means building a metadata reference for every loaded assembly plus Roslyn codegen, which
        // in a real application ran into seconds per attempt.
        var cacheKey = new ScriptCacheKey(source, imports, request.References, request.LoadContext);
        var compilationCached = _scripts.TryGet(cacheKey, out var script, out var diagnostics);
        if (!compilationCached)
        {
            var options = ScriptOptions.Default
                .WithEmitDebugInformation(false)
                .WithReferences(_assemblies.GetMetadataReferences(
                    request.References,
                    _options.MaximumExecutionReferences,
                    request.LoadContext))
                .WithImports(imports);
            script = CSharpScript.Create<object?>(
                source,
                options,
                typeof(ExecutionGlobals),
                _assemblies.CreateExecutionAssemblyLoader(request.LoadContext));
        }

        var stopwatch = Stopwatch.StartNew();
        long? compileMilliseconds = null;
        try
        {
            if (!compilationCached)
            {
                // Bracketed separately from the overall stopwatch so a slow compile (reference
                // resolution, script complexity) can be told apart from a slow run (the
                // submission's own work) - the two point at very different problems.
                var compileStopwatch = Stopwatch.StartNew();
                diagnostics = script.Compile(executionToken).Select(ToDiagnostic).ToArray();
                var errors = diagnostics
                    .Where(diagnostic => diagnostic.Severity == nameof(DiagnosticSeverity.Error))
                    .ToArray();
                if (errors.Length != 0)
                {
                    // Deliberately not cached: the type this needed may simply not be loaded yet,
                    // and recompiling next time lets the same submission start working.
                    throw new ScryCompilationException(errors);
                }

                _scripts.Add(cacheKey, script, diagnostics);
                compileStopwatch.Stop();
                compileMilliseconds = compileStopwatch.ElapsedMilliseconds;
            }

            // No ConfigureAwait(false) inside: when this runs through a marshaller the dispatcher's
            // SynchronizationContext is current, and capturing it is the whole point - a submission
            // that awaits must resume on the UI thread too, not just start there. Unmarshalled there
            // is no context to capture, so the behaviour is unchanged.
            //
            // The completing thread is captured here, at the tail of this local function, rather
            // than after the outer `await marshaller(...)` below. A marshaller is free to bridge its
            // own completion back through machinery - a TaskCompletionSource, a dispatcher operation
            // - that does not itself resume on the marshalled thread even though the submission's own
            // code did; measuring the wrong side of that bridge would misreport a request that was
            // genuinely marshalled as if it had not been.
            var completionThreadId = 0;
            async Task<object?> RunSubmissionAsync()
            {
                var state = await script.RunAsync(globals, cancellationToken: executionToken);
                var result = await UnwrapAsync(state.ReturnValue);
                completionThreadId = Environment.CurrentManagedThreadId;
                return result;
            }

            var marshaller = _configuration.ExecutionMarshaller;
            var marshalled = marshalToUiThread && marshaller is not null;
            var value = marshalled
                ? await marshaller!(RunSubmissionAsync, executionToken).ConfigureAwait(false)
                : await RunSubmissionAsync().ConfigureAwait(false);
            stopwatch.Stop();
            return new(
                value,
                logs.Entries,
                logs.DroppedCount,
                diagnostics,
                stopwatch.ElapsedMilliseconds,
                compilationCached,
                marshalled,
                completionThreadId,
                compileMilliseconds,
                stopwatch.ElapsedMilliseconds - (compileMilliseconds ?? 0));
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

    /// <summary>
    /// Separates a hook predicate's cache entries from ordinary submissions: the compiled script is
    /// bound to <see cref="HookCallGlobals"/> rather than <see cref="ExecutionGlobals"/>, so the same
    /// source text must never share a cache slot between the two.
    /// </summary>
    private const string HookPredicateCachePrefix = "\u0001hook-call\u0001";

    /// <summary>
    /// Compiles a <c>hook.wait</c> predicate once, so it can then be evaluated against many captured
    /// calls through <see cref="EvaluatePredicateAsync"/> without paying for compilation, a roots
    /// snapshot, or a log buffer per call the way a full <c>evaluate</c> would.
    /// </summary>
    internal HookPredicate CompilePredicate(
        string source,
        IReadOnlyList<string>? requestImports,
        IReadOnlyList<string>? references,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ScryOperationException("invalid_request", "predicate must not be empty.");
        }

        if (source.Length > _options.MaximumSourceLength)
        {
            throw new ScryOperationException(
                "request_limit_exceeded",
                $"predicate exceeds the {_options.MaximumSourceLength}-character limit.");
        }

        if (requestImports is { Count: > 0 } &&
            (requestImports.Count > _options.MaximumExecutionImports || requestImports.Any(string.IsNullOrWhiteSpace)))
        {
            throw new ScryOperationException(
                "request_limit_exceeded",
                $"imports must contain at most {_options.MaximumExecutionImports} non-empty entries.");
        }

        var imports = DefaultImports.Concat(requestImports ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var cacheKey = new ScriptCacheKey(HookPredicateCachePrefix + source, imports, references);
        if (!_scripts.TryGet(cacheKey, out var script, out _))
        {
            var options = ScriptOptions.Default
                .WithEmitDebugInformation(false)
                .WithReferences(_assemblies.GetMetadataReferences(references, _options.MaximumExecutionReferences))
                .WithImports(imports);
            script = CSharpScript.Create<object?>(
                source,
                options,
                typeof(HookCallGlobals),
                _assemblies.CreateExecutionAssemblyLoader(null));
            var diagnostics = script.Compile(cancellationToken).Select(ToDiagnostic).ToArray();
            var errors = diagnostics
                .Where(diagnostic => diagnostic.Severity == nameof(DiagnosticSeverity.Error))
                .ToArray();
            if (errors.Length != 0)
            {
                throw new ScryCompilationException(errors);
            }

            _scripts.Add(cacheKey, script, diagnostics);
        }

        return new HookPredicate(script);
    }

    /// <summary>
    /// Runs a compiled predicate against one captured call and returns whatever it produced (the
    /// caller decides whether that is a usable boolean). Bounded by the default execution timeout.
    /// </summary>
    internal async ValueTask<object?> EvaluatePredicateAsync(
        HookPredicate predicate,
        HookCallGlobals globals,
        string? marshal,
        CancellationToken cancellationToken)
    {
        var marshalToUiThread = MarshalTarget.Resolve(marshal, _configuration.ExecutionMarshaller);
        using var timeoutSource = new CancellationTokenSource();
        timeoutSource.CancelAfter(TimeSpan.FromMilliseconds(_options.DefaultExecutionMilliseconds));
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var token = linkedSource.Token;

        // No ConfigureAwait(false) inside, for the same reason as RunAsync: through a marshaller the
        // dispatcher's SynchronizationContext is current and a predicate that awaits must resume on it.
        async Task<object?> RunPredicateAsync()
        {
            var state = await predicate.Script.RunAsync(globals, cancellationToken: token);
            return await UnwrapAsync(state.ReturnValue);
        }

        try
        {
            return marshalToUiThread
                ? await _configuration.ExecutionMarshaller!(RunPredicateAsync, token).ConfigureAwait(false)
                : await RunPredicateAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ScryOperationException(
                "predicate_timed_out",
                $"The predicate did not finish within {_options.DefaultExecutionMilliseconds} milliseconds.");
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

        if (request.LoadContext is not null)
        {
            if (string.IsNullOrWhiteSpace(request.LoadContext))
            {
                throw new ScryOperationException("invalid_request", "loadContext must be a non-empty string.");
            }

#if NETFRAMEWORK
            throw new ScryOperationException(
                "load_context_not_supported",
                "loadContext targeting is available only on modern .NET; on .NET Framework, target a specific AppDomain with --appdomain / appdomain.start instead.");
#endif
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
    long ElapsedMilliseconds,
    bool CompilationCached,
    bool Marshalled,
    int ThreadId,
    long? CompileMilliseconds,
    long RunMilliseconds);

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
    private readonly List<ExecutionLogEntry> _entries = new();
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

            var boundedLevel = level.Length <= 64 ? level : level.Substring(0, 64);
            var bounded = message.Length <= _maximumMessageLength
                ? message
                : message.Substring(0, _maximumMessageLength);
            _entries.Add(new(DateTimeOffset.UtcNow, boundedLevel, bounded));
        }
    }
}
