using System.Text.Json;

namespace Scry.Contracts;

public static class HookOutcomes
{
    public const string Returned = "returned";
    public const string Threw = "threw";
}

public static class HookInliningRisks
{
    public const string None = "none";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
}

/// <summary>Identifies a hook. Scoped to the session that created it, like a job handle.</summary>
public sealed record HookHandle(string TargetId, string SessionId, string HookId);

/// <param name="Type">Full name of the declaring type, as <c>find-types</c> reports it.</param>
/// <param name="Method">Method name, or <c>.ctor</c> for a constructor. A property accessor is
/// <c>get_Name</c> or <c>set_Name</c>.</param>
/// <param name="ParameterTypes">Selects an overload. Each entry matches a parameter's full name,
/// simple name or C# keyword (<c>string</c>, <c>int</c>); a trailing <c>&amp;</c> is ignored.</param>
/// <param name="BindingFlags">Comma-separated <c>System.Reflection.BindingFlags</c> names. Defaults
/// to <c>Public, NonPublic, Instance, Static, DeclaredOnly</c>.</param>
/// <param name="Capacity">Number of calls kept; the oldest are dropped when it is exceeded.</param>
public sealed record HookAddRequest(
    string Type,
    string Method,
    IReadOnlyList<string>? ParameterTypes = null,
    string? Assembly = null,
    string? LoadContext = null,
    string? BindingFlags = null,
    bool CaptureArguments = true,
    bool CaptureReturnValue = true,
    bool CaptureException = true,
    bool CaptureInstance = false,
    int Capacity = 1000);

/// <param name="Cursor">First sequence number wanted; calls older than the buffer holds are
/// reported as <see cref="HookReadResult.Truncated"/>.</param>
/// <param name="IncludeReferences">Leases a session handle for each captured reference-type value
/// (and the exception) instead of reporting only its type.</param>
public sealed record HookReadRequest(
    HookHandle Hook,
    long Cursor = 0,
    int Limit = 100,
    bool IncludeReferences = false);

/// <param name="Predicate">C# expression evaluated in the target against each buffered call, with
/// <c>Args</c>, <c>ReturnValue</c>, <c>Exception</c>, <c>Instance</c> and <c>Call</c> in scope. It
/// must produce a <see cref="bool"/>. Omitted, the first call at or after the cursor matches.</param>
public sealed record HookWaitRequest(
    HookHandle Hook,
    string? Predicate = null,
    long Cursor = 0,
    int TimeoutMilliseconds = 30000,
    IReadOnlyList<string>? Imports = null,
    IReadOnlyList<string>? References = null,
    string? Marshal = null,
    bool IncludeReferences = false);

public sealed record HookQueryRequest(HookHandle Hook);

/// <param name="Risk">One of <see cref="HookInliningRisks"/>. Derived from method metadata: the
/// runtime offers no way to ask whether a caller already inlined a method.</param>
public sealed record HookInliningRisk(string Risk, IReadOnlyList<string> Reasons);

public sealed record HookForeignHarmony(string Assembly, string Version, string? Location);

public sealed record HookHarmonyInfo(
    string Version,
    string? Location,
    IReadOnlyList<HookForeignHarmony> ForeignCopies);

public sealed record HookAddResult(
    HookHandle Hook,
    bool Created,
    string DeclaringType,
    string Signature,
    int Capacity,
    HookInliningRisk Inlining,
    HookHarmonyInfo Harmony,
    IReadOnlyList<string> Warnings);

/// <summary>
/// One recorded call. <see cref="Arguments"/>, <see cref="ReturnValue"/> and <see cref="Instance"/>
/// use <see cref="RemoteValue"/>; a reference-type value is reported with kind <c>preview</c>
/// (type name only) unless the request set <c>includeReferences</c>, which reports kind
/// <c>reference</c> with a session handle.
/// </summary>
public sealed record HookCall(
    long Sequence,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int ThreadId,
    string Outcome,
    IReadOnlyList<RemoteValue>? Arguments,
    RemoteValue? ReturnValue,
    ExceptionDetail? Exception,
    ExternalReference? ExceptionReference,
    RemoteValue? Instance)
{
    /// <summary>
    /// The return value as <typeparamref name="T"/>. Only a scalar or value-type return value is
    /// carried in full, so this throws for a call that threw, for a method whose return value was not
    /// captured, and for a reference-type value (read it with <c>includeReferences</c> instead).
    /// </summary>
    public T GetReturnValue<T>()
    {
        if (ReturnValue is not { Value: { } value })
        {
            throw new InvalidOperationException(
                $"Call {Sequence} has no scalar or value-type return value to read " +
                $"(outcome: {Outcome}, return kind: {ReturnValue?.Kind ?? "none"}).");
        }

        return value.Deserialize<T>(ScryJson.Options)!;
    }
}

public sealed record HookReadResult(
    HookHandle Hook,
    long RequestedCursor,
    long OldestCursor,
    long NextCursor,
    bool Truncated,
    bool HasMore,
    long TotalCalls,
    long DroppedCalls,
    long CaptureFailures,
    IReadOnlyList<HookCall> Calls);

public sealed record HookWaitResult(
    HookHandle Hook,
    bool Satisfied,
    bool TimedOut,
    HookCall? Call,
    long NextCursor,
    int Evaluated,
    long TotalCalls,
    long DroppedCalls,
    long CaptureFailures,
    IReadOnlyList<string> Diagnostics);

public sealed record HookSummary(
    HookHandle Hook,
    string DeclaringType,
    string Signature,
    int Capacity,
    long TotalCalls,
    long BufferedCalls,
    long DroppedCalls,
    long CaptureFailures,
    DateTimeOffset CreatedAt,
    HookInliningRisk Inlining);

public sealed record HookListResult(IReadOnlyList<HookSummary> Hooks, HookHarmonyInfo? Harmony);

public sealed record HookRemoveResult(HookHandle Hook, bool Removed);
