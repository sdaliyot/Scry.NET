using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scry.Contracts;

public static class ProtocolConstants
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 8 * 1024 * 1024;

    public static IReadOnlyList<string> CoreCapabilities { get; } = Array.AsReadOnly(
        new[]
        {
            "capabilities", "roots", "inspect", "get", "set", "invoke", "enumerate", "release",
            "evaluate", "execute", "wait", "assert",
            "load-assembly", "list-assemblies", "find-types", "describe-type",
            "job.start", "job.status", "job.wait", "job.cancel", "job.logs"
        });

    public static IReadOnlyList<string> FeatureCapabilities { get; } = Array.AsReadOnly(
        new[] { "bounded-value-projection" });

    /// <summary>
    /// Advertised in a capabilities response only when the host registered an execution marshaller,
    /// so a client can tell whether "marshal": "ui" will be accepted before sending a submission.
    /// </summary>
    public const string UiThreadMarshallingFeature = "ui-thread-marshalling";

    public static IReadOnlyList<string> AllCapabilities { get; } =
        CoreCapabilities.Concat(FeatureCapabilities).ToArray();
}

public static class ScryJson
{
    /// <summary>
    /// Well above System.Text.Json's default of 64, because that default is an incidental
    /// serializer artifact rather than a protocol bound - and it bites at a surprisingly shallow
    /// tree. A UI projection nests two JSON levels per tree level (a children array plus a node
    /// object), so the WPF adapter's own 32-level cap alone reaches roughly 64, and snapshotting a
    /// real application's window failed with a misleading "possible object cycle" error. The real
    /// bounds are the adapters' node and depth budgets and the frame size cap, all still in force.
    /// </summary>
    public const int MaximumJsonDepth = 256;

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        MaxDepth = MaximumJsonDepth
    };
}

public sealed record ProtocolRequest(
    int ProtocolVersion,
    string RequestId,
    string Operation,
    JsonElement Payload)
{
    public string? CorrelationId { get; init; }
}

public sealed record ProtocolResponse(
    int ProtocolVersion,
    string RequestId,
    bool Success,
    string? SessionId,
    JsonElement? Result,
    ProtocolError? Error)
{
    public string? OperationId { get; init; }

    public string? CorrelationId { get; init; }

    public static ProtocolResponse Succeeded(string requestId, string? sessionId, object? result) =>
        Succeeded(requestId, sessionId, result, null, null);

    public static ProtocolResponse Succeeded(
        string requestId,
        string? sessionId,
        object? result,
        string? operationId,
        string? correlationId) =>
        new(ProtocolConstants.Version, requestId, true, sessionId,
            JsonSerializer.SerializeToElement(result, ScryJson.Options), null)
        {
            OperationId = operationId,
            CorrelationId = correlationId
        };

    public static ProtocolResponse Failed(string requestId, string? sessionId, ProtocolError error) =>
        Failed(requestId, sessionId, error, null, null);

    public static ProtocolResponse Failed(
        string requestId,
        string? sessionId,
        ProtocolError error,
        string? operationId,
        string? correlationId) =>
        new(ProtocolConstants.Version, requestId, false, sessionId, null, error)
        {
            OperationId = operationId,
            CorrelationId = correlationId
        };
}

public sealed record ProtocolError(
    string Code,
    string Message,
    ExceptionDetail? Exception = null,
    IReadOnlyDictionary<string, string>? Data = null,
    IReadOnlyList<CompilationDiagnostic>? Diagnostics = null);

public sealed record ExceptionDetail(
    string Type,
    string Message,
    string? StackTrace,
    int HResult,
    string? Source,
    ExceptionDetail? InnerException)
{
    public static ExceptionDetail FromException(Exception exception)
    {
        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        return new(
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            exception.StackTrace,
            exception.HResult,
            exception.Source,
            exception.InnerException is null ? null : FromException(exception.InnerException));
    }
}

public sealed record HandshakeRequest(
    string CapabilityToken,
    int MinimumVersion = ProtocolConstants.Version,
    int MaximumVersion = ProtocolConstants.Version,
    string? SessionId = null,
    string? ClientName = null,
    bool EphemeralSession = false);

public sealed record HandshakeResult(
    int ProtocolVersion,
    TargetMetadata Target,
    string SessionId,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset SessionExpiresAt);

public sealed record TargetMetadata(
    string TargetId,
    string Alias,
    int ProcessId,
    string ProcessName,
    string RuntimeVersion,
    string FrameworkDescription,
    string Architecture,
    DateTimeOffset StartedAt)
{
    public IReadOnlyList<string>? Aliases { get; init; }
}

public sealed record ConnectionDescriptor(
    int ProtocolVersion,
    TargetMetadata Target,
    string PipeName,
    string CapabilityToken,
    DateTimeOffset PublishedAt);

public sealed record DiscoveredTarget(
    int ProtocolVersion,
    TargetMetadata Target,
    string DescriptorPath,
    DateTimeOffset PublishedAt);

public sealed record ExternalReference(
    string TargetId,
    string SessionId,
    string HandleId,
    string Type,
    string Preview,
    DateTimeOffset LeaseExpiresAt);

public sealed record RemoteValue(
    string Kind,
    string Type,
    string Preview,
    JsonElement? Value = null,
    ExternalReference? Reference = null);

public sealed record MemberDescription(
    string Name,
    string Kind,
    string Type,
    bool CanRead,
    bool CanWrite,
    bool IsPublic,
    IReadOnlyList<string>? Parameters = null);

public sealed class ProtocolException(string message) : Exception(message);

public static class FrameCodec
{
    public static async ValueTask WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, ScryJson.Options);
        if (payload.Length > ProtocolConstants.MaximumFrameBytes)
        {
            throw new ProtocolException($"Frame exceeds {ProtocolConstants.MaximumFrameBytes} bytes.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
#if NETFRAMEWORK
        await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
#else
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
#endif
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T?> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[sizeof(int)];
#if NETFRAMEWORK
        var first = await stream.ReadAsync(header, 0, 1, cancellationToken).ConfigureAwait(false);
#else
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
#endif
        if (first == 0)
        {
            return default;
        }

#if NETFRAMEWORK
        await ReadExactlyAsync(stream, header, 1, header.Length - 1, cancellationToken).ConfigureAwait(false);
#else
        await ReadExactlyAsync(stream, header.AsMemory(1), cancellationToken).ConfigureAwait(false);
#endif
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > ProtocolConstants.MaximumFrameBytes)
        {
            throw new ProtocolException($"Invalid frame length {length}.");
        }

        var payload = new byte[length];
#if NETFRAMEWORK
        await ReadExactlyAsync(stream, payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
#else
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
#endif
        return JsonSerializer.Deserialize<T>(payload, ScryJson.Options)
            ?? throw new ProtocolException($"Frame did not contain a {typeof(T).Name}.");
    }

#if NETFRAMEWORK
    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < count)
        {
            var bytesRead = await stream.ReadAsync(
                buffer,
                offset + read,
                count - read,
                cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException("The stream ended inside a protocol frame.");
            }

            read += bytesRead;
        }
    }
#else
    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The stream ended inside a protocol frame.");
            }

            read += count;
        }
    }
#endif
}
