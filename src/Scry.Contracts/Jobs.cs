using System.Text.Json;

namespace Scry.Contracts;

public static class JobStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
}

public sealed record JobHandle(string TargetId, string SessionId, string JobId);

public sealed record JobStartRequest(
    string Operation,
    JsonElement Payload,
    string? CorrelationId = null);

public sealed record JobQueryRequest(JobHandle Job);

public sealed record JobWaitRequest(JobHandle Job, int TimeoutMilliseconds = 30000);

public sealed record JobLogsRequest(JobHandle Job, long Cursor = 0, int Limit = 100);

public sealed record JobSnapshot(
    JobHandle Job,
    string Operation,
    string OperationId,
    string CorrelationId,
    string State,
    bool CancellationRequested,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    JsonElement? Result,
    ProtocolError? Error);

public sealed record JobWaitResult(JobSnapshot Job, bool TimedOut);

public sealed record JobLogEntry(
    long Cursor,
    DateTimeOffset Timestamp,
    string Level,
    string Message);

public sealed record JobLogResult(
    JobHandle Job,
    long RequestedCursor,
    long OldestCursor,
    long NextCursor,
    bool Truncated,
    bool HasMore,
    IReadOnlyList<JobLogEntry> Entries);

public sealed record ScenarioRequest(
    string Mode,
    IReadOnlyList<ScenarioCommand> Commands);

/// <param name="Address">
/// When present, connects over TCP to <paramref name="Address"/> (parsed with
/// <see cref="ScryEndpointAddress.Parse"/>) instead of the named pipe - the flagship reason this
/// exists: one <c>scry scenario</c> call can drive a local target and assert on a remote one.
/// Only meaningful together with <see cref="Descriptor"/>, since <see cref="Target"/> resolves
/// through local discovery and a remote target can never be discovered that way.
/// </param>
public sealed record ScenarioCommand(
    string Id,
    string Operation,
    JsonElement Payload,
    string? Target = null,
    string? Descriptor = null,
    string? SessionId = null,
    string? CorrelationId = null,
    string? Address = null);

public sealed record ScenarioCommandResult(
    string Id,
    int Index,
    string Operation,
    string? TargetSelector,
    TargetMetadata? Target,
    ProtocolResponse? Response,
    ProtocolError? Error)
{
    public bool Success => Response?.Success == true && Error is null;
}

public sealed record ScenarioResult(
    int ProtocolVersion,
    string Mode,
    bool Success,
    IReadOnlyList<ScenarioCommandResult> Results);
