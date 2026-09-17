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

/// <param name="InnerExceptions">
/// Populated only when the projected exception is an <see cref="AggregateException"/>, in which
/// case it carries every fault (bounded by <see cref="ExceptionDetail.MaximumDepth"/>) and
/// <see cref="InnerException"/> is set to the first fault as well, so a reader that only looks
/// at <c>InnerException</c> still sees a fault rather than nothing.
/// </param>
/// <param name="DroppedInnerExceptions">
/// How many of an <see cref="AggregateException"/>'s faults were not projected because
/// <see cref="InnerExceptions"/> is already at <see cref="ExceptionDetail.MaximumDepth"/>. Null
/// when nothing was dropped.
/// </param>
/// <param name="Truncated">
/// True when this node's own inner-exception chain kept going past
/// <see cref="ExceptionDetail.MaximumDepth"/> and was cut off rather than followed further.
/// </param>
public sealed record ExceptionDetail(
    string Type,
    string Message,
    string? StackTrace,
    int HResult,
    string? Source,
    ExceptionDetail? InnerException,
    IReadOnlyList<ExceptionDetail>? InnerExceptions = null,
    int? DroppedInnerExceptions = null,
    bool Truncated = false)
{
    /// <summary>
    /// How many inner-exception levels are followed - through <see cref="Exception.InnerException"/>,
    /// or through each fault of an <see cref="AggregateException"/> - before the chain is cut off.
    /// There is no natural bound on how deeply an application can wrap exceptions, and following it
    /// unbounded risks overrunning the JSON depth limit or the frame's byte cap, which would fail to
    /// serialize the error being reported rather than merely truncate it. The same constant bounds
    /// how many of an <see cref="AggregateException"/>'s faults are projected at all.
    /// </summary>
    public const int MaximumDepth = 8;

    /// <summary>Matches the truncation length used elsewhere for a projected string value.</summary>
    public const int MaximumMessageLength = 4096;

    /// <summary>
    /// Generous relative to <see cref="MaximumMessageLength"/>: a stack trace is where most of an
    /// exception's forensic value lives, so it is capped far looser.
    /// </summary>
    public const int MaximumStackTraceLength = 16384;

    public static ExceptionDetail FromException(Exception exception)
    {
        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        return FromException(exception, MaximumDepth);
    }

    private static ExceptionDetail FromException(Exception exception, int remainingDepth)
    {
        if (remainingDepth <= 0)
        {
            return new(
                exception.GetType().FullName ?? exception.GetType().Name,
                Truncate(exception.Message, MaximumMessageLength)!,
                null,
                exception.HResult,
                exception.Source,
                InnerException: null,
                Truncated: true);
        }

        ExceptionDetail? innerException = null;
        IReadOnlyList<ExceptionDetail>? innerExceptions = null;
        int? dropped = null;

        if (exception is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
        {
            var faults = aggregate.InnerExceptions;
            var projectedCount = Math.Min(faults.Count, MaximumDepth);
            var projected = new ExceptionDetail[projectedCount];
            for (var index = 0; index < projectedCount; index++)
            {
                projected[index] = FromException(faults[index], remainingDepth - 1);
            }

            innerExceptions = projected;
            innerException = projected[0];
            if (faults.Count > projectedCount)
            {
                dropped = faults.Count - projectedCount;
            }
        }
        else if (exception.InnerException is not null)
        {
            innerException = FromException(exception.InnerException, remainingDepth - 1);
        }

        return new(
            exception.GetType().FullName ?? exception.GetType().Name,
            Truncate(exception.Message, MaximumMessageLength)!,
            Truncate(exception.StackTrace, MaximumStackTraceLength),
            exception.HResult,
            exception.Source,
            innerException,
            innerExceptions,
            dropped);
    }

    private static string? Truncate(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength
            ? value
            : value.Substring(0, maximumLength);
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

    /// <summary>
    /// The <see cref="AppDomain.Id"/> this endpoint actually runs in, or null for the process's
    /// default AppDomain - which is every endpoint before AppDomain targeting existed, so a
    /// descriptor predating this field stays byte-identical (<see cref="ScryJson.Options"/> omits
    /// null members on write). Set only on .NET Framework, where more than one AppDomain can exist
    /// in a process; always null on modern .NET.
    /// </summary>
    public int? AppDomainId { get; init; }

    /// <summary>
    /// The <see cref="AppDomain.FriendlyName"/> this endpoint actually runs in. Null exactly when
    /// <see cref="AppDomainId"/> is null, for the same reason.
    /// </summary>
    public string? AppDomainFriendlyName { get; init; }

    /// <summary>
    /// Set only when an attach-time <c>--appdomain</c> selector could not be honoured - it matched
    /// zero or several AppDomains, or the chosen domain's own binding policy conflicted with the
    /// payload - and the endpoint was started in the default AppDomain instead of failing the
    /// attach outright (the native injection that got this far cannot be retried without recycling
    /// the target). Null on every ordinary attach and on every embedded host.
    /// </summary>
    public string? AppDomainSelectionWarning { get; init; }
}

/// <param name="TcpAddress">
/// The loopback address the TCP listener is bound to (always <c>127.0.0.1</c> when present).
/// Null when the endpoint has no TCP listener - the common case, and byte-identical to a
/// descriptor written before TCP support existed, since <see cref="ScryJson.Options"/> omits null
/// members on write and a missing member binds to its declared default on read.
/// </param>
/// <param name="TcpPort">The bound TCP port, or null when there is no TCP listener. Must be
/// 1-65535 when present: a descriptor always carries the port actually bound, and 0 (meaning
/// "bind a free port") is only ever an input option, never a published value.</param>
/// <param name="MachineName">
/// The host machine's name (<see cref="Environment.MachineName"/>), so a descriptor carried onto
/// another machine can be told apart from a local one by <see cref="TargetDiscovery.FindAsync"/>.
/// Null on a descriptor written before this field existed; <see cref="TargetDiscovery.FindAsync"/>
/// treats null the same as a local machine name, preserving its existing behaviour exactly.
/// </param>
public sealed record ConnectionDescriptor(
    int ProtocolVersion,
    TargetMetadata Target,
    string PipeName,
    string CapabilityToken,
    DateTimeOffset PublishedAt,
    string? TcpAddress = null,
    int? TcpPort = null,
    string? MachineName = null);

public sealed record DiscoveredTarget(
    int ProtocolVersion,
    TargetMetadata Target,
    string DescriptorPath,
    DateTimeOffset PublishedAt,
    string? TcpAddress = null,
    int? TcpPort = null);

/// <summary>Values for <see cref="ConnectionDescriptor"/>'s implicit transport kind, and for
/// <see cref="AuditRecord.Transport"/>/<c>ScryClient.Transport</c>.</summary>
public static class ScryTransports
{
    public const string Pipe = "pipe";
    public const string Tcp = "tcp";
}

/// <summary>
/// A loopback host and port to connect to over TCP, distinct from the address a descriptor's own
/// <see cref="ConnectionDescriptor.TcpAddress"/>/<see cref="ConnectionDescriptor.TcpPort"/>
/// publish - this is what a caller supplies to reach that listener through a port forward, which
/// may not be the same port the endpoint itself bound.
/// <para>
/// Restricted to loopback hosts by construction and by <see cref="Parse"/> - on the client side,
/// matching the listener, which binds loopback only. That restriction keeps Scry.NET itself off
/// the network and forces a caller to explicitly choose and own the mechanism that bridges the
/// last hop (a port forward such as <c>ssh -L</c> or <c>netsh interface portproxy</c>); it does
/// not by itself make the token confidential in transit - an unencrypted forward such as
/// <c>netsh portproxy</c> still carries it in cleartext across whatever network that forward
/// spans. Prefer an encrypted forward (<c>ssh -L</c>) whenever the two loopback endpoints are not
/// the same machine.
/// </para>
/// </summary>
public sealed record ScryEndpointAddress(string Host, int Port)
{
    public string Host { get; } = ValidateHost(Host);

    public int Port { get; } = ValidatePort(Port);

    /// <summary>
    /// Parses <c>host:port</c>, <c>[::1]:port</c>, a bare <c>port</c> (host defaults to
    /// <c>127.0.0.1</c>), or <c>auto</c>, meaning "use the descriptor's own TCP address/port"
    /// (represented as a null return rather than an instance).
    /// </summary>
    public static ScryEndpointAddress? Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("An address cannot be empty.");
        }

        if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (int.TryParse(value, out var barePort))
        {
            return new ScryEndpointAddress("127.0.0.1", barePort);
        }

        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var closing = value.IndexOf(']');
            if (closing < 0 || closing + 1 >= value.Length || value[closing + 1] != ':')
            {
                throw new FormatException($"'{value}' is not a valid host:port address.");
            }

            var bracketedHost = value.Substring(1, closing - 1);
            var bracketedPortText = value.Substring(closing + 2);
            if (!int.TryParse(bracketedPortText, out var bracketedPort))
            {
                throw new FormatException($"'{value}' is not a valid host:port address.");
            }

            return new ScryEndpointAddress(bracketedHost, bracketedPort);
        }

        var separator = value.LastIndexOf(':');
        if (separator < 0)
        {
            throw new FormatException($"'{value}' is not a valid host:port address.");
        }

        var host = value.Substring(0, separator);
        var portText = value.Substring(separator + 1);
        if (!int.TryParse(portText, out var port))
        {
            throw new FormatException($"'{value}' is not a valid host:port address.");
        }

        return new ScryEndpointAddress(host, port);
    }

    private static string ValidateHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host cannot be empty.", nameof(host));
        }

        var isLoopback =
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "::1", StringComparison.Ordinal) ||
            (host.StartsWith("127.", StringComparison.Ordinal) &&
                System.Net.IPAddress.TryParse(host, out var parsed) &&
                System.Net.IPAddress.IsLoopback(parsed));

        if (!isLoopback)
        {
            throw new ArgumentException(
                $"'{host}' is not a loopback host. Only 127.0.0.1, other 127.x.x.x addresses, " +
                "::1, and localhost are accepted, because the capability token crosses this " +
                "connection in cleartext and must never leave the local machine.",
                nameof(host));
        }

        return host;
    }

    private static int ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be 1-65535.");
        }

        return port;
    }
}

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
