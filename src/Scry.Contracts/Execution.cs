namespace Scry.Contracts;

public sealed record ExecutionRequest(
    string Source,
    IReadOnlyList<string>? Imports = null,
    IReadOnlyList<string>? References = null,
    int? TimeoutMilliseconds = null);

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
