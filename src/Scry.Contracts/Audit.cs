using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scry.Contracts;

/// <summary>
/// One line of the audit log: a connection, a handshake attempt, an operation, or a job
/// lifecycle event. Every event the target ever handles goes through one of these, which is what
/// makes it possible to answer "what did an agent or a test actually do to this process" after
/// the fact - something nothing in Scry.NET recorded before this existed.
///
/// <para>
/// Three fields identify who did something, and they carry different weight. <see cref="HostUser"/>
/// is authenticated - the pipe only accepts connections from the current Windows user, so this is
/// guaranteed rather than asserted. <see cref="ClientName"/> is whatever the connecting client
/// claimed to be at handshake (<c>"scry"</c>, <c>"scry attach"</c>, a test's own name) - useful, but
/// self-reported, and a hostile client could lie. There is deliberately no process-identity field:
/// capturing one would need the first native interop in this assembly, which is a bigger cost than
/// this warrants; the connection can be told apart from others by <see cref="ConnectionId"/> within
/// one run, but not attributed to a specific process.
/// </para>
///
/// <para>
/// What must never appear here: the capability token, in any form - not even hashed, since a wrong
/// token during a rejected handshake might be a valid token for a different live target. And never
/// the value passed to <c>set</c> or the arguments passed to <c>invoke</c> - this project's own
/// README uses <c>set-password.json</c> as its worked example, so the credential risk here is the
/// mutated value, not the submitted C# source.
/// </para>
/// </summary>
public sealed record AuditRecord(
    string Kind,
    DateTimeOffset Timestamp,
    string TargetId,
    string Outcome)
{
    /// <summary>Host process identity - the accompanying <see cref="AuditRecord"/>'s
    /// <see cref="Alias"/>, <see cref="ProcessId"/> and <see cref="HostUser"/> are constant for
    /// the life of the target, so most lines omit them; only <c>endpoint.started</c> is
    /// guaranteed to carry them.</summary>
    public string? Alias { get; init; }

    public int? ProcessId { get; init; }

    public string? HostUser { get; init; }

    /// <summary>Distinguishes concurrent connections within one run. Not stable across restarts.</summary>
    public int? ConnectionId { get; init; }

    /// <summary>Self-asserted by the client at handshake. A label, not an authenticated identity.</summary>
    public string? ClientName { get; init; }

    public string? SessionId { get; init; }

    public string? Operation { get; init; }

    public string? OperationId { get; init; }

    public string? CorrelationId { get; init; }

    public long? ElapsedMilliseconds { get; init; }

    public string? ErrorCode { get; init; }

    /// <summary>
    /// Everything else worth knowing about this line that does not warrant its own column: the
    /// root or member touched, the kind and length of a <c>set</c> value (never the value itself),
    /// a submission's length and SHA-256 (never its text), a bounded error message, and so on. Kept
    /// as free text rather than a growing list of nullable fields, because the shape of "what is
    /// worth recording" differs per operation and would otherwise demand a new column for every one.
    /// </summary>
    public string? Detail { get; init; }
}

/// <summary>Values for <see cref="AuditRecord.Kind"/>.</summary>
public static class AuditEventKinds
{
    public const string EndpointStarted = "endpoint.started";
    public const string EndpointStopped = "endpoint.stopped";
    public const string Handshake = "handshake";
    public const string Operation = "operation";
    public const string ConnectionFaulted = "connection.faulted";
}

/// <summary>Values for <see cref="AuditRecord.Outcome"/>.</summary>
public static class AuditOutcomes
{
    public const string Started = "started";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    /// <summary>A handshake that was rejected before a session existed.</summary>
    public const string Denied = "denied";
}

/// <summary>
/// Where the audit log lives, and why it is not simply beside the connection descriptors.
/// <see cref="TargetDiscovery.FindAsync"/> globs every <c>*.json</c> file under
/// <see cref="TargetDiscovery.DirectoryPath"/> and deletes any that fails to parse as a
/// <see cref="ConnectionDescriptor"/> - an audit file placed there, or even named with a
/// <c>.json</c> extension there, would be silently destroyed by the next <c>scry discover</c>.
/// Hence a sibling directory and the <c>.jsonl</c> extension.
/// </summary>
public static class AuditPaths
{
    public static string DirectoryPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Scry",
            "audit");

    /// <summary>
    /// One file per host instance, named so concurrent instances - and rotated files from the same
    /// instance - can never collide: the process ID plus a fresh GUID (rather than the target ID,
    /// which callers should not need to know to find the file) plus a rotation sequence number.
    /// Takes <paramref name="directory"/> explicitly rather than assuming <see cref="DirectoryPath"/>,
    /// since a host may override where its log is written.
    /// </summary>
    public static string GetLogPath(string directory, int processId, string instanceId, int sequence) =>
        Path.Combine(directory, $"scry-audit-{processId}-{instanceId}-{sequence:D3}.jsonl");
}

/// <summary>
/// A dedicated, pinned serialization configuration for the audit log - deliberately not
/// <see cref="ScryJson.Options"/>, which is the wire protocol's format. The audit log is a durable
/// on-disk artifact; coupling its shape to the wire format would mean a future protocol-motivated
/// change (indentation, naming policy) silently changes every existing audit file's format too.
/// </summary>
public static class AuditJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}
