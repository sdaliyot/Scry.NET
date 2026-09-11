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

public sealed record ExecutionResult(
    RemoteValue Value,
    IReadOnlyList<ExecutionLogEntry> Logs,
    int DroppedLogEntries,
    IReadOnlyList<CompilationDiagnostic> Diagnostics,
    long ElapsedMilliseconds);

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
