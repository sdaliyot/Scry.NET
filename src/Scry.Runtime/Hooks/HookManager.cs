using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Scry.Contracts;

namespace Scry.Runtime;

/// <summary>
/// Owns the method hooks of one endpoint. Modelled on <see cref="JobManager"/>: handles are scoped to
/// the session that created them, reads are cursor-based over a bounded buffer, and waits report a
/// timeout rather than throwing.
/// <para>
/// Unlike a job, a hook does not pin its session. A session that goes away - an ephemeral session on
/// disconnect, any session on lease expiry - takes its hooks with it (<see cref="RemoveSession"/>), so
/// a crashed client does not leave a patch behind indefinitely.
/// </para>
/// </summary>
internal sealed class HookManager : IDisposable
{
    private const int MaximumReadLimit = 1000;
    private const int MaximumWaitMilliseconds = 300_000;
    private const int PredicateBatchSize = 100;

    private readonly ConcurrentDictionary<string, HookEntry> _hooks = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly AssemblyCatalog _assemblies;
    private readonly ExecutionEngine _execution;
    private readonly string _targetId;
    private readonly int _maximumHooks;
    private readonly int _maximumCapacity;
    private HookHarmonyInfo? _harmony;
    private bool _disposed;

    public HookManager(
        AssemblyCatalog assemblies,
        ExecutionEngine execution,
        string targetId,
        int maximumHooks,
        int maximumCapacity)
    {
        _assemblies = assemblies;
        _execution = execution;
        _targetId = targetId;
        _maximumHooks = maximumHooks;
        _maximumCapacity = maximumCapacity;
    }

    public async ValueTask<object> DispatchAsync(
        string operation,
        JsonElement payload,
        SessionState session,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HookManager));
        }

        return operation switch
        {
            "hook.add" => Add(Parse<HookAddRequest>(payload), session),
            "hook.read" => Read(Parse<HookReadRequest>(payload), session, drain: false),
            "hook.drain" => Read(Parse<HookReadRequest>(payload), session, drain: true),
            "hook.wait" => await WaitAsync(Parse<HookWaitRequest>(payload), session, cancellationToken)
                .ConfigureAwait(false),
            "hook.remove" => Remove(Parse<HookQueryRequest>(payload), session),
            "hook.list" => List(session),
            _ => throw new ScryOperationException(
                "operation_not_supported",
                $"Operation '{operation}' is not supported.")
        };
    }

    private HookAddResult Add(HookAddRequest request, SessionState session)
    {
        if (request.Capacity < 1 || request.Capacity > _maximumCapacity)
        {
            throw new ScryOperationException(
                "invalid_request",
                $"capacity must be between 1 and {_maximumCapacity}.");
        }

        var type = _assemblies.ResolveType(request.Type, request.Assembly, request.LoadContext, includeNonPublic: true);
        var method = HookMethodResolver.Resolve(type, request);
        var inlining = HookInlining.Assess(method);

        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(HookManager));
            }

            var existing = _hooks.Values.FirstOrDefault(hook =>
                string.Equals(hook.Handle.SessionId, session.Id, StringComparison.Ordinal) &&
                hook.Method.MethodHandle.Value == method.MethodHandle.Value);
            if (existing is not null)
            {
                var repeated = new List<string>();
                if (!existing.Matches(request))
                {
                    repeated.Add(
                        "This session already hooks this method; the existing hook is returned unchanged " +
                        "and the capture options and capacity in this request were ignored.");
                }

                return existing.Describe(created: false, HarmonyEnvironment.Describe(), repeated);
            }

            if (_hooks.Count >= _maximumHooks)
            {
                throw new ScryOperationException(
                    "hook_limit_reached",
                    $"The target's limit of {_maximumHooks} hooks has been reached.");
            }

            var harmony = DescribeHarmony();
            var warnings = new List<string>();
            var foreignOwners = HarmonyEnvironment.ForeignOwners(method);
            if (foreignOwners.Count > 0)
            {
                throw new ScryOperationException(
                    "method_patched_by_foreign_harmony",
                    $"{HookMethodResolver.Signature(method)} is already patched through a different Harmony " +
                    $"copy loaded in this process ({string.Join(", ", foreignOwners)}). Two independent copies " +
                    "keep separate patch state, so a second detour on the same method could not be removed safely.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["owners"] = string.Join(", ", foreignOwners)
                    });
            }

            AddAdvisories(method, request, harmony, inlining, warnings);

            var buffer = new HookBuffer(request.Capacity);
            var subscription = new HookSubscription(
                buffer,
                request.CaptureArguments,
                request.CaptureReturnValue,
                request.CaptureException,
                request.CaptureInstance);
            try
            {
                HookPatcher.Subscribe(method, subscription);
            }
            catch (Exception exception) when (exception is not ScryOperationException)
            {
                throw new ScryOperationException(
                    "hook_patch_failed",
                    $"Harmony could not patch {HookMethodResolver.Signature(method)}: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }

            var shape = HookShape.Of(method);
            var entry = new HookEntry(
                new HookHandle(_targetId, session.Id, RuntimeCompatibility.CreateRandomHex(16)),
                method,
                request,
                buffer,
                subscription,
                inlining,
                shape.HasReturnValue,
                shape.IsStatic);
            _hooks[Key(entry.Handle)] = entry;
            _harmony = harmony;
            return entry.Describe(created: true, harmony, warnings);
        }
    }

    private static HookHarmonyInfo DescribeHarmony()
    {
        try
        {
            var info = HarmonyEnvironment.Describe();
            HarmonyEnvironment.ThrowIfUnusable(info);
            return info;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or FileLoadException or BadImageFormatException or
                TypeLoadException or MissingMethodException or TypeInitializationException)
        {
            throw new ScryOperationException(
                "hooks_unavailable",
                $"Harmony could not be loaded in this process: {exception.GetType().Name}: {exception.Message}. " +
                "0Harmony.dll must be staged next to the Scry runtime.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exception"] = exception.GetType().FullName ?? exception.GetType().Name
                });
        }
    }

    private static void AddAdvisories(
        MethodBase method,
        HookAddRequest request,
        HookHarmonyInfo harmony,
        HookInliningRisk inlining,
        List<string> warnings)
    {
        if (inlining.Risk is HookInliningRisks.High or HookInliningRisks.Medium)
        {
            warnings.Add(
                $"inlining_risk_{inlining.Risk}: a caller that inlined this method bypasses the hook and the " +
                $"call is not recorded. {string.Join(" ", inlining.Reasons)}");
        }

        if (harmony.ForeignCopies.Count > 0)
        {
            warnings.Add(
                "foreign_harmony_loaded: " + string.Join(
                    "; ",
                    harmony.ForeignCopies.Select(copy => $"{copy.Assembly} {copy.Version} at {copy.Location ?? "unknown location"}")) +
                ". Hooks still work, but patch state is not shared with those copies.");
        }

        var others = HookPatcher.OtherOwners(method);
        if (others.Count > 0)
        {
            warnings.Add(
                $"method_patched_by_others: {HookMethodResolver.Signature(method)} is also patched by " +
                $"{string.Join(", ", others)}. Patches stack; the recorded values reflect what those patches pass on.");
        }

        if (request.CaptureInstance && method.DeclaringType?.IsValueType == true && !method.IsStatic)
        {
            warnings.Add("instance_not_captured: the declaring type is a struct, so its instance cannot be captured.");
        }

        if (method is MethodInfo info && typeof(System.Threading.Tasks.Task).IsAssignableFrom(info.ReturnType))
        {
            warnings.Add(
                "async_method: the call is recorded when the method returns its Task, not when the " +
                "asynchronous work finishes; ReturnValue is that Task.");
        }
    }

    private HookReadResult Read(HookReadRequest request, SessionState session, bool drain)
    {
        if (request.Cursor < 0 || request.Limit is < 1 or > MaximumReadLimit)
        {
            throw new ScryOperationException(
                "invalid_request",
                $"cursor must be non-negative and limit must be between 1 and {MaximumReadLimit}.");
        }

        var entry = Find(request.Hook, session);
        var page = entry.Buffer.Read(request.Cursor, request.Limit);
        var calls = new List<HookCall>(page.Records.Count);
        var byteBudget = ProtocolConstants.MaximumFrameBytes - (64 * 1024);
        var usedBytes = 0;
        foreach (var record in page.Records)
        {
            var call = EncodeCall(record, entry, session, request.IncludeReferences);
            var callBytes = JsonSerializer.SerializeToUtf8Bytes(call, ScryJson.Options).Length;
            if (calls.Count > 0 && usedBytes + callBytes > byteBudget)
            {
                break;
            }

            calls.Add(call);
            usedBytes += callBytes;
        }

        var nextCursor = calls.Count == 0 ? page.NextSequence : calls[calls.Count - 1].Sequence + 1;
        if (drain && calls.Count > 0)
        {
            entry.Buffer.DiscardThrough(nextCursor - 1);
        }

        return new(
            entry.Handle,
            request.Cursor,
            page.OldestSequence,
            nextCursor,
            request.Cursor < page.OldestSequence,
            nextCursor < entry.Buffer.TotalCalls,
            entry.Buffer.TotalCalls,
            entry.Buffer.DroppedCalls,
            entry.Buffer.CaptureFailures,
            calls);
    }

    private async ValueTask<HookWaitResult> WaitAsync(
        HookWaitRequest request,
        SessionState session,
        CancellationToken cancellationToken)
    {
        if (request.Cursor < 0 || request.TimeoutMilliseconds is < 0 or > MaximumWaitMilliseconds)
        {
            throw new ScryOperationException(
                "invalid_request",
                $"cursor must be non-negative and timeoutMilliseconds must be between 0 and {MaximumWaitMilliseconds}.");
        }

        var entry = Find(request.Hook, session);

        // Compiled before anything is waited on, so a bad predicate fails immediately rather than
        // after the timeout, and so the first buffered call is not charged the compile.
        var predicate = request.Predicate is null
            ? null
            : _execution.CompilePredicate(request.Predicate, request.Imports, request.References, cancellationToken);

        var diagnostics = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        var timeout = TimeSpan.FromMilliseconds(request.TimeoutMilliseconds);
        var cursor = request.Cursor;
        var evaluated = 0;
        var oldest = entry.Buffer.OldestSequence;
        if (cursor < oldest)
        {
            diagnostics.Add(
                $"Calls {cursor} through {oldest - 1} were dropped from the buffer before they could be " +
                "evaluated; raise capacity or wait with a more recent cursor.");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = entry.Buffer.Read(cursor, PredicateBatchSize);
            foreach (var record in page.Records)
            {
                evaluated++;
                if (await MatchesAsync(predicate, record, request.Marshal, cancellationToken).ConfigureAwait(false))
                {
                    return new(
                        entry.Handle,
                        Satisfied: true,
                        TimedOut: false,
                        EncodeCall(record, entry, session, request.IncludeReferences),
                        record.Sequence + 1,
                        evaluated,
                        entry.Buffer.TotalCalls,
                        entry.Buffer.DroppedCalls,
                        entry.Buffer.CaptureFailures,
                        diagnostics);
                }
            }

            cursor = page.NextSequence;
            if (page.HasMore)
            {
                continue;
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                await RuntimeCompatibility.AwaitWithTimeoutAsync(
                    entry.Buffer.WaitForChange(cursor),
                    remaining,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                break;
            }
        }

        if (entry.Buffer.TotalCalls == 0)
        {
            diagnostics.Add(
                "No call has been recorded since the hook was added. " +
                (entry.Inlining.Risk is HookInliningRisks.None
                    ? "The method cannot be inlined, so it has simply not been called yet."
                    : $"Inlining risk is {entry.Inlining.Risk}: if the method was expected to run, a caller " +
                      $"may have inlined it and bypassed the hook. {string.Join(" ", entry.Inlining.Reasons)}"));
        }

        return new(
            entry.Handle,
            Satisfied: false,
            TimedOut: true,
            Call: null,
            cursor,
            evaluated,
            entry.Buffer.TotalCalls,
            entry.Buffer.DroppedCalls,
            entry.Buffer.CaptureFailures,
            diagnostics);
    }

    private async ValueTask<bool> MatchesAsync(
        HookPredicate? predicate,
        HookRecord record,
        string? marshal,
        CancellationToken cancellationToken)
    {
        if (predicate is null)
        {
            return true;
        }

        var globals = new HookCallGlobals(
            record.Arguments ?? Array.Empty<object?>(),
            record.ReturnValue,
            record.Exception,
            record.Instance,
            new HookCallInfo(record.Sequence, record.ThreadId, record.StartedUtc, record.CompletedUtc, record.Threw));
        object? result;
        try
        {
            result = await _execution.EvaluatePredicateAsync(predicate, globals, marshal, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or ScryOperationException))
        {
            throw new ScryOperationException(
                "predicate_failed",
                $"The predicate threw for call {record.Sequence}: {exception.GetType().Name}: {exception.Message}",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sequence"] = record.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
        }

        return result is bool matches
            ? matches
            : throw new ScryOperationException(
                "invalid_predicate",
                $"The predicate must produce a bool but produced {result?.GetType().FullName ?? "null"}.");
    }

    private HookRemoveResult Remove(HookQueryRequest request, SessionState session)
    {
        var entry = Find(request.Hook, session);
        var removed = _hooks.TryRemove(Key(entry.Handle), out var taken);
        taken?.Dispose();
        return new(entry.Handle, removed);
    }

    private HookListResult List(SessionState session) =>
        new(
            _hooks.Values
                .Where(hook => string.Equals(hook.Handle.SessionId, session.Id, StringComparison.Ordinal))
                .OrderBy(hook => hook.CreatedAt)
                .Select(hook => hook.Summarize())
                .ToArray(),
            _harmony is null ? null : HarmonyEnvironment.Describe());

    /// <summary>Unpatches every hook the session owned. Called as the session is disposed.</summary>
    public void RemoveSession(string sessionId)
    {
        foreach (var pair in _hooks)
        {
            if (string.Equals(pair.Value.Handle.SessionId, sessionId, StringComparison.Ordinal) &&
                _hooks.TryRemove(pair.Key, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    private HookEntry Find(HookHandle? handle, SessionState session)
    {
        if (handle is null)
        {
            throw new ScryOperationException("invalid_request", "A hook handle is required.");
        }

        if (!string.Equals(handle.TargetId, _targetId, StringComparison.Ordinal) ||
            !string.Equals(handle.SessionId, session.Id, StringComparison.Ordinal))
        {
            throw new ScryOperationException(
                "hook_scope_mismatch",
                "The hook belongs to a different target or session.");
        }

        return _hooks.TryGetValue(Key(handle), out var entry)
            ? entry
            : throw new ScryOperationException(
                "hook_not_found",
                "The hook does not exist; it was removed or its session ended.");
    }

    private static HookCall EncodeCall(
        HookRecord record,
        HookEntry entry,
        SessionState session,
        bool includeReferences) =>
        new(
            record.Sequence,
            new DateTimeOffset(DateTime.SpecifyKind(record.StartedUtc, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(record.CompletedUtc, DateTimeKind.Utc)),
            record.ThreadId,
            record.Threw ? HookOutcomes.Threw : HookOutcomes.Returned,
            record.Arguments?.Select(argument => EncodeValue(argument, session, includeReferences)).ToArray(),
            !record.Threw && entry.CaptureReturnValue && entry.HasReturnValue
                ? EncodeValue(record.ReturnValue, session, includeReferences)
                : null,
            record.Exception is null ? null : ExceptionDetail.FromException(record.Exception),
            includeReferences && record.Exception is not null ? session.Lease(record.Exception, out _) : null,
            entry.CaptureInstance && !entry.IsStatic && record.Instance is not null
                ? EncodeValue(record.Instance, session, includeReferences)
                : null);

    /// <summary>
    /// Scalars and value types are encoded in full - they were boxed at call time, so this is the
    /// value the call saw. A reference type is reported as its type only (kind <c>preview</c>) unless
    /// the caller asked for handles: the object may have been mutated since the call, and leasing it
    /// keeps it alive for the session.
    /// </summary>
    private static RemoteValue EncodeValue(object? value, SessionState session, bool includeReferences)
    {
        if (value is null || value.GetType().IsValueType || ValueProjection.IsScalar(value.GetType()) || includeReferences)
        {
            return OperationDispatcher.Encode(value, session);
        }

        var name = value.GetType().FullName ?? value.GetType().Name;
        return new("preview", name, name);
    }

    private static T Parse<T>(JsonElement payload)
    {
        try
        {
            return payload.Deserialize<T>(ScryJson.Options)
                ?? throw new ScryOperationException("invalid_request", "The hook request is missing.");
        }
        catch (JsonException exception)
        {
            throw new ScryOperationException("invalid_request", exception.Message);
        }
    }

    private static string Key(HookHandle handle) => $"{handle.SessionId}:{handle.HookId}";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _hooks.Values)
            {
                entry.Dispose();
            }

            _hooks.Clear();
        }
    }
}

internal sealed class HookEntry(
    HookHandle handle,
    MethodBase method,
    HookAddRequest request,
    HookBuffer buffer,
    HookSubscription subscription,
    HookInliningRisk inlining,
    bool hasReturnValue,
    bool isStatic) : IDisposable
{
    private int _disposed;

    public HookHandle Handle { get; } = handle;

    public MethodBase Method { get; } = method;

    public HookBuffer Buffer { get; } = buffer;

    public HookInliningRisk Inlining { get; } = inlining;

    public bool HasReturnValue { get; } = hasReturnValue;

    public bool IsStatic { get; } = isStatic;

    public bool CaptureReturnValue => request.CaptureReturnValue;

    public bool CaptureInstance => request.CaptureInstance;

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    public bool Matches(HookAddRequest other) =>
        other.Capacity == request.Capacity &&
        other.CaptureArguments == request.CaptureArguments &&
        other.CaptureReturnValue == request.CaptureReturnValue &&
        other.CaptureException == request.CaptureException &&
        other.CaptureInstance == request.CaptureInstance;

    public HookAddResult Describe(bool created, HookHarmonyInfo harmony, IReadOnlyList<string> warnings) =>
        new(
            Handle,
            created,
            Method.DeclaringType?.FullName ?? string.Empty,
            HookMethodResolver.Signature(Method),
            Buffer.Capacity,
            Inlining,
            harmony,
            warnings);

    public HookSummary Summarize() =>
        new(
            Handle,
            Method.DeclaringType?.FullName ?? string.Empty,
            HookMethodResolver.Signature(Method),
            Buffer.Capacity,
            Buffer.TotalCalls,
            Buffer.BufferedCalls,
            Buffer.DroppedCalls,
            Buffer.CaptureFailures,
            CreatedAt,
            Inlining);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            HookPatcher.Unsubscribe(Method, subscription);
        }
    }
}
