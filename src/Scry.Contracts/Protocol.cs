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
        new[] { "capabilities", "roots", "inspect", "get", "set", "invoke", "enumerate", "release" });

    public static IReadOnlyList<string> FeatureCapabilities { get; } = Array.AsReadOnly(
        new[] { "bounded-value-projection" });

    public static IReadOnlyList<string> AllCapabilities { get; } =
        CoreCapabilities.Concat(FeatureCapabilities).ToArray();
}

public static class ScryJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}

public sealed record ProtocolRequest(
    int ProtocolVersion,
    string RequestId,
    string Operation,
    JsonElement Payload);

public sealed record ProtocolResponse(
    int ProtocolVersion,
    string RequestId,
    bool Success,
    string? SessionId,
    JsonElement? Result,
    ProtocolError? Error)
{
    public static ProtocolResponse Succeeded(string requestId, string? sessionId, object? result) =>
        new(ProtocolConstants.Version, requestId, true, sessionId,
            JsonSerializer.SerializeToElement(result, ScryJson.Options), null);

    public static ProtocolResponse Failed(string requestId, string? sessionId, ProtocolError error) =>
        new(ProtocolConstants.Version, requestId, false, sessionId, null, error);
}

public sealed record ProtocolError(
    string Code,
    string Message,
    ExceptionDetail? Exception = null,
    IReadOnlyDictionary<string, string>? Data = null);

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
        ArgumentNullException.ThrowIfNull(exception);
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
    DateTimeOffset StartedAt);

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
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T?> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[sizeof(int)];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0)
        {
            return default;
        }

        await ReadExactlyAsync(stream, header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > ProtocolConstants.MaximumFrameBytes)
        {
            throw new ProtocolException($"Invalid frame length {length}.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, ScryJson.Options)
            ?? throw new ProtocolException($"Frame did not contain a {typeof(T).Name}.");
    }

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
}
