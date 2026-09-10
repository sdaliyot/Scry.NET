using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Scry.Contracts;

namespace Scry.Runtime;

public sealed class RuntimeHost : IAsyncDisposable, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<int, NamedPipeServerStream> _connections = new();
    private readonly ConcurrentDictionary<int, Task> _handlers = new();
    private readonly SessionManager _sessions;
    private readonly OperationDispatcher _dispatcher;
    private readonly Task _acceptTask;
    private readonly EventHandler _processExitHandler;
    private int _connectionId;
    private int _disposed;

    private RuntimeHost(AgentConfiguration configuration, RuntimeHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Alias))
        {
            throw new ArgumentException("The target alias cannot be empty.", nameof(options));
        }
        if (options.HandleLease <= TimeSpan.Zero ||
            options.SessionLease <= TimeSpan.Zero ||
            options.MaximumPreviewLength < 1 ||
            options.MaximumHandlesPerSession < 1 ||
            options.MaximumSessions < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Lease durations and resource limits must be positive.");
        }

        var process = Process.GetCurrentProcess();
        var targetId = Guid.NewGuid().ToString("N");
        var pipeName = $"scry-{Guid.NewGuid():N}";
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Metadata = new(
            targetId,
            options.Alias,
            Environment.ProcessId,
            process.ProcessName,
            Environment.Version.ToString(),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            process.StartTime.ToUniversalTime());
        Descriptor = new(
            ProtocolConstants.Version,
            Metadata,
            pipeName,
            token,
            DateTimeOffset.UtcNow);
        DescriptorPath = TargetDiscovery.GetDescriptorPath(targetId);

        _sessions = new(
            targetId,
            options.HandleLease,
            options.SessionLease,
            options.MaximumPreviewLength,
            options.MaximumHandlesPerSession,
            options.MaximumSessions);
        _dispatcher = new(Metadata, configuration);
        PublishDescriptor();
        _processExitHandler = (_, _) => CleanupDescriptor();
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
        _acceptTask = AcceptConnectionsAsync(_stopping.Token);
    }

    public TargetMetadata Metadata { get; }

    public ConnectionDescriptor Descriptor { get; }

    public string DescriptorPath { get; }

    public static RuntimeHost Start(AgentConfiguration configuration, RuntimeHostOptions? options = null) =>
        new(configuration, options ?? new RuntimeHostOptions());

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
        await _stopping.CancelAsync().ConfigureAwait(false);
        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }

        try
        {
            await _acceptTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await Task.WhenAll(_handlers.Values)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or OperationCanceledException or ObjectDisposedException or
            ProtocolException or TimeoutException)
        {
        }

        _sessions.Dispose();
        CleanupDescriptor();
        _stopping.Dispose();
    }

    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    Descriptor.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var id = Interlocked.Increment(ref _connectionId);
                _connections[id] = pipe;
                var handler = HandleConnectionAsync(id, pipe, cancellationToken);
                _handlers[id] = handler;
                _ = handler.ContinueWith(
                    (completedTask, state) =>
                    {
                        var tuple = ((RuntimeHost Host, int Id))state!;
                        tuple.Host._handlers.TryRemove(tuple.Id, out _);
                        _ = completedTask.Exception;
                    },
                    (this, id),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                pipe?.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleConnectionAsync(
        int connectionId,
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        string? sessionId = null;
        var ephemeralSession = false;
        try
        {
            var handshakeFrame = await FrameCodec.ReadAsync<ProtocolRequest>(pipe, cancellationToken)
                .ConfigureAwait(false);
            if (handshakeFrame is null)
            {
                return;
            }

            if (handshakeFrame.Operation != "handshake")
            {
                await WriteFailureAsync(
                    pipe,
                    handshakeFrame.RequestId,
                    null,
                    "handshake_required",
                    "The first request on a connection must be a handshake.",
                    null,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            HandshakeRequest handshake;
            try
            {
                if (handshakeFrame.Payload.ValueKind != JsonValueKind.Object)
                {
                    throw new ProtocolException("Handshake payload must be an object.");
                }

                handshake = handshakeFrame.Payload.Deserialize<HandshakeRequest>(ScryJson.Options)
                    ?? throw new ProtocolException("Handshake payload is missing.");
                if (string.IsNullOrEmpty(handshake.CapabilityToken))
                {
                    throw new ProtocolException("Handshake capabilityToken is required.");
                }
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidOperationException or ProtocolException)
            {
                await WriteFailureAsync(
                    pipe,
                    handshakeFrame.RequestId,
                    null,
                    "invalid_handshake",
                    exception.Message,
                    exception,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!TokenMatches(handshake.CapabilityToken))
            {
                await WriteFailureAsync(
                    pipe,
                    handshakeFrame.RequestId,
                    null,
                    "authentication_failed",
                    "The capability token is invalid.",
                    null,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (handshake.MinimumVersion > ProtocolConstants.Version ||
                handshake.MaximumVersion < ProtocolConstants.Version)
            {
                await WriteFailureAsync(
                    pipe,
                    handshakeFrame.RequestId,
                    null,
                    "protocol_version_mismatch",
                    $"The target supports protocol version {ProtocolConstants.Version}.",
                    null,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var session = handshake.SessionId is null
                ? _sessions.Create()
                : _sessions.Resume(handshake.SessionId);
            sessionId = session.Id;
            ephemeralSession = handshake.EphemeralSession && handshake.SessionId is null;
            var result = new HandshakeResult(
                ProtocolConstants.Version,
                Metadata,
                session.Id,
                ProtocolConstants.AllCapabilities,
                session.ExpiresAt);
            await FrameCodec.WriteAsync(
                pipe,
                ProtocolResponse.Succeeded(handshakeFrame.RequestId, session.Id, result),
                cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
            {
                var request = await FrameCodec.ReadAsync<ProtocolRequest>(pipe, cancellationToken)
                    .ConfigureAwait(false);
                if (request is null)
                {
                    break;
                }

                ProtocolResponse response;
                try
                {
                    if (request.ProtocolVersion != ProtocolConstants.Version)
                    {
                        throw new ScryOperationException(
                            "protocol_version_mismatch",
                            $"The target supports protocol version {ProtocolConstants.Version}.");
                    }

                    session.Touch();
                    using var operationLease = session.EnterOperation();
                    var operationResult = await _dispatcher.DispatchAsync(
                        request.Operation,
                        request.Payload,
                        session,
                        cancellationToken).ConfigureAwait(false);
                    response = ProtocolResponse.Succeeded(request.RequestId, session.Id, operationResult);
                }
                catch (Exception exception)
                {
                    var actual = exception is TargetInvocationException { InnerException: { } inner }
                        ? inner
                        : exception;
                    var code = actual is ScryOperationException operationException
                        ? operationException.Code
                        : "operation_failed";
                    response = ProtocolResponse.Failed(
                        request.RequestId,
                        session.Id,
                        new(code, actual.Message, ExceptionDetail.FromException(actual)));
                }

                await FrameCodec.WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
            {
                try
                {
                    await WriteFailureAsync(
                        pipe,
                        "unknown",
                        sessionId,
                        exception is ScryOperationException operationException
                            ? operationException.Code
                            : "protocol_error",
                        exception.Message,
                        exception,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception writeException) when (
                    writeException is IOException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }
        finally
        {
            if (ephemeralSession && sessionId is not null)
            {
                _sessions.Remove(sessionId);
            }

            _connections.TryRemove(connectionId, out _);
            pipe.Dispose();
        }
    }

    private async ValueTask WriteFailureAsync(
        Stream stream,
        string requestId,
        string? sessionId,
        string code,
        string message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        var response = ProtocolResponse.Failed(
            requestId,
            sessionId,
            new(code, message, exception is null ? null : ExceptionDetail.FromException(exception)));
        await FrameCodec.WriteAsync(stream, response, cancellationToken).ConfigureAwait(false);
    }

    private bool TokenMatches(string candidate)
    {
        var expected = Encoding.UTF8.GetBytes(Descriptor.CapabilityToken);
        var supplied = Encoding.UTF8.GetBytes(candidate);
        return expected.Length == supplied.Length &&
            CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private void PublishDescriptor()
    {
        Directory.CreateDirectory(TargetDiscovery.DirectoryPath);
        var temporaryPath = $"{DescriptorPath}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(Descriptor, ScryJson.Options);
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, DescriptorPath);
    }

    private void CleanupDescriptor()
    {
        try
        {
            File.Delete(DescriptorPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
