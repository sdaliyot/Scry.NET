namespace Scry.Contracts;

/// <summary>
/// Where the endpoint runs a submission. Omitting <see cref="ExecutionRequest.Marshal"/> keeps the
/// default: the submission runs on whichever endpoint thread serves the request.
/// </summary>
public static class ExecutionMarshalTargets
{
    /// <summary>
    /// Run the submission on the target's UI thread. Requires the host to have registered an
    /// execution marshaller, which the optional WPF and Windows Forms adapters do for you.
    /// </summary>
    public const string UiThread = "ui";
}

public sealed record ExecutionRequest(
    string Source,
    IReadOnlyList<string>? Imports = null,
    IReadOnlyList<string>? References = null,
    int? TimeoutMilliseconds = null,
    string? Marshal = null);

/// <param name="CompilationCached">
/// True when the submission reused an already-compiled script rather than compiling. Lets a caller
/// tell a fast repeat from a cold compile, and makes the script cache observable without relying
/// on timing.
/// </param>
/// <param name="Marshalled">
/// True when this submission actually ran through the host's execution marshaller - the only way
/// to confirm a <c>"marshal": "ui"</c> request was honoured. Before this existed, the caller had no
/// positive signal that marshalling happened; the only feedback was a failure, when a marshaller
/// was missing or unrecognised. False for a submission that ran without asking to be marshalled.
/// </param>
/// <param name="ThreadId">
/// The managed thread ID the submission completed on. Mainly useful together with
/// <see cref="Marshalled"/>, to confirm a marshalled submission actually finished on the host's
/// nominated thread rather than one it was resumed onto elsewhere.
/// </param>
/// <param name="CompileMilliseconds">
/// Time spent compiling, or null when <see cref="CompilationCached"/> is true and no compile
/// happened. Split out from <see cref="ElapsedMilliseconds"/> because the two have very different
/// causes: a slow compile points at reference resolution or script complexity, a slow run points at
/// the submission's own work.
/// </param>
/// <param name="RunMilliseconds">
/// Time spent actually running the script, excluding any compile captured in
/// <see cref="CompileMilliseconds"/>. Equals <see cref="ElapsedMilliseconds"/> when the submission
/// was served from the script cache.
/// </param>
public sealed record ExecutionResult(
    RemoteValue Value,
    IReadOnlyList<ExecutionLogEntry> Logs,
    int DroppedLogEntries,
    IReadOnlyList<CompilationDiagnostic> Diagnostics,
    long ElapsedMilliseconds,
    bool CompilationCached = false,
    bool Marshalled = false,
    int ThreadId = 0,
    long? CompileMilliseconds = null,
    long RunMilliseconds = 0);

public sealed record ExecutionLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Message);

public sealed record CompilationDiagnostic(
    string Id,
    string Severity,
    string Message,
    string? FilePath,
    int? StartLine,
    int? StartColumn,
    int? EndLine,
    int? EndColumn);
