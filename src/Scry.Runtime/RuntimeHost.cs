using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Scry.Contracts;

namespace Scry.Runtime;

public sealed class RuntimeHost : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Caps concurrent, not-yet-handshaken TCP connections. Unlike the named pipe - which Windows
    /// refuses to any other user before a single byte is read - any local process can open a TCP
    /// socket, and the accept loop spawns a handler and writes an audit record per connection
    /// before the capability token is even checked. Without a cap, that is free amplification
    /// against the audit log's file-rotation budget. The pipe has no equivalent cap because the OS
    /// peer check already stands in front of it.
    /// </summary>
    private const int MaximumPendingTcpConnections = 256;

    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<int, Stream> _connections = new();
    private readonly ConcurrentDictionary<int, Task> _handlers = new();
    private readonly SemaphoreSlim _tcpConnectionGate = new(MaximumPendingTcpConnections, MaximumPendingTcpConnections);
    private readonly SessionManager _sessions;
    private readonly OperationDispatcher _dispatcher;
    private readonly JobManager _jobs;
    private readonly AuditLog _audit;
    private readonly Task _pipeAcceptTask;
    private readonly Task _tcpAcceptTask;
    private readonly TcpListener? _tcpListener;
    private readonly EventHandler _processExitHandler;
    private readonly string _targetsDirectory;
    private int _connectionId;
    private int _disposed;

    private RuntimeHost(EndpointConfiguration configuration, RuntimeHostOptions options)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Alias))
        {
            throw new ArgumentException("The target alias cannot be empty.", nameof(options));
        }
        if (options.Aliases.Any(string.IsNullOrWhiteSpace) ||
            options.HandleLease <= TimeSpan.Zero ||
            options.SessionLease <= TimeSpan.Zero ||
            options.JobRetention <= TimeSpan.Zero ||
            options.MaximumPreviewLength < 1 ||
            options.MaximumHandlesPerSession < 1 ||
            options.MaximumSessions < 1 ||
            options.MaximumSourceLength < 1 ||
            options.MaximumExecutionMilliseconds < 1 ||
            options.DefaultExecutionMilliseconds < 1 ||
            options.DefaultExecutionMilliseconds > options.MaximumExecutionMilliseconds ||
            options.MaximumExecutionReferences < 1 ||
            options.MaximumExecutionImports < 1 ||
            options.MaximumLogEntries < 1 ||
            options.MaximumLogMessageLength < 1 ||
            options.MaximumTypeResults < 1 ||
            options.MaximumTypeMembers < 1 ||
            options.MaximumAssemblyBytes < 1 ||
            options.MaximumJobs < 1 ||
            options.MaximumJobLogEntries < 1 ||
            options.MaximumJobLogMessageLength < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Lease durations and resource limits must be positive.");
        }

        var process = Process.GetCurrentProcess();
        var targetId = Guid.NewGuid().ToString("N");
        var pipeName = $"scry-{Guid.NewGuid():N}";
        var token = Convert.ToBase64String(RuntimeCompatibility.GetRandomBytes(32));

        if (options.TcpPort is { } requestedPort && requestedPort is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "TcpPort must be 0-65535, or null to start no TCP listener.");
        }

        // Resolved and validated before the TCP bind below: a host that cannot publish its
        // descriptor must never be left holding a bound socket.
        var targetsDirectory = TargetDiscovery.ResolveDirectory(options.TargetsDirectory);

        // The socket must be bound before Descriptor is constructed, so the descriptor can
        // publish the port actually bound (which matters when the caller asked for port 0). That
        // puts a bind ahead of the rest of this constructor, which can still throw - so from here
        // on everything is wrapped in a try/catch that stops a bound-but-unpublished listener
        // rather than leaking it until GC.
        try
        {
            if (options.TcpPort is { } port)
            {
                _tcpListener = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    _tcpListener.Start();
                }
                catch (SocketException exception)
                {
                    throw new InvalidOperationException(
                        $"Could not bind TCP port {port}: {exception.Message}", exception);
                }
            }

            var boundTcpPort = _tcpListener is null
                ? (int?)null
                : ((IPEndPoint)_tcpListener.LocalEndpoint).Port;

            // Not threaded through options: reading AppDomain.CurrentDomain directly means every
            // endpoint reports where it actually runs, with no separate value to keep in sync. On
            // modern .NET IsDefaultAppDomain() is always true, so both fields stay null there with
            // no extra guard needed.
            var currentDomain = AppDomain.CurrentDomain;
            var isDefaultDomain = currentDomain.IsDefaultAppDomain();

            Metadata = new(
                targetId,
                options.Alias,
                process.Id,
                process.ProcessName,
                Environment.Version.ToString(),
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                process.StartTime.ToUniversalTime())
            {
                Aliases = options.Aliases
                    .Append(options.Alias)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                AppDomainId = isDefaultDomain ? null : currentDomain.Id,
                AppDomainFriendlyName = isDefaultDomain ? null : currentDomain.FriendlyName,
                AppDomainSelectionWarning = options.AppDomainSelectionWarning
            };
            Descriptor = new(
                ProtocolConstants.Version,
                Metadata,
                pipeName,
                token,
                DateTimeOffset.UtcNow,
                TcpAddress: boundTcpPort is null ? null : IPAddress.Loopback.ToString(),
                TcpPort: boundTcpPort,
                MachineName: Environment.MachineName);
            _targetsDirectory = targetsDirectory;
            DescriptorPath = TargetDiscovery.GetDescriptorPath(targetId, targetsDirectory);

            _sessions = new(
                targetId,
                options.HandleLease,
                options.SessionLease,
                options.MaximumPreviewLength,
                options.MaximumHandlesPerSession,
                options.MaximumSessions);
            var assemblies = new AssemblyCatalog(options);
            var execution = new ExecutionEngine(configuration, options, assemblies);
            _dispatcher = new(Metadata, configuration, assemblies, execution);
            _jobs = new(
                _dispatcher,
                targetId,
                options.JobRetention,
                options.MaximumJobs,
                options.MaximumJobLogEntries,
                options.MaximumJobLogMessageLength);
            _audit = new(
                options.AuditEnabled,
                options.AuditDirectory,
                options.AuditCallback,
                process.Id);
            PublishDescriptor();
            // After the publish, not before: a publish failure must not be reported as a successful
            // start.
            _audit.Write(new(
                AuditEventKinds.EndpointStarted,
                DateTimeOffset.UtcNow,
                targetId,
                AuditOutcomes.Started)
            {
                Alias = options.Alias,
                ProcessId = process.Id,
                HostUser = AuditLog.CurrentHostUser(),
                Detail = boundTcpPort is null ? null : $"tcp=127.0.0.1:{boundTcpPort}"
            });
            _processExitHandler = (_, _) => CleanupDescriptor();
            AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
            // DomainUnload, not only ProcessExit: an endpoint hosted in a non-default AppDomain
            // (see AppDomain targeting) dies when that domain unloads, which ProcessExit never
            // fires for. The default domain itself never raises DomainUnload while the process is
            // alive, so subscribing here unconditionally is harmless for every ordinary endpoint.
            AppDomain.CurrentDomain.DomainUnload += _processExitHandler;
            _pipeAcceptTask = AcceptPipeConnectionsAsync(_stopping.Token);
            _tcpAcceptTask = _tcpListener is null
                ? Task.CompletedTask
                : AcceptTcpConnectionsAsync(_stopping.Token);
        }
        catch
        {
            _tcpListener?.Stop();
            throw;
        }
    }

    public TargetMetadata Metadata { get; }

    public ConnectionDescriptor Descriptor { get; }

    public string DescriptorPath { get; }

    public static RuntimeHost Start(EndpointConfiguration configuration, RuntimeHostOptions? options = null) =>
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
        AppDomain.CurrentDomain.DomainUnload -= _processExitHandler;
#if NETFRAMEWORK
        _stopping.Cancel();
#else
        await _stopping.CancelAsync().ConfigureAwait(false);
#endif
        // Stopped before awaiting the accept task: this is what unblocks a pending AcceptSocketAsync
        // on .NET Framework, which has no cancellable overload, and it also ensures the port is
        // refused to any new caller immediately rather than only once the accept task notices.
        _tcpListener?.Stop();
        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }

        try
        {
            await _pipeAcceptTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await _tcpAcceptTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        try
        {
            await RuntimeCompatibility.AwaitWithTimeoutAsync(
                Task.WhenAll(_handlers.Values),
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or OperationCanceledException or ObjectDisposedException or
            ProtocolException or TimeoutException)
        {
        }

        _jobs.Dispose();
        _sessions.Dispose();
        CleanupDescriptor();
        _stopping.Dispose();

        // Enqueued after the descriptor is gone and connections are refused, so it is genuinely
        // the last record - but before the log itself stops draining.
        _audit.Write(new(
            AuditEventKinds.EndpointStopped,
            DateTimeOffset.UtcNow,
            Metadata.TargetId,
            AuditOutcomes.Succeeded));
        await _audit.DisposeAsync().ConfigureAwait(false);
    }

    private async Task AcceptPipeConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                RegisterConnection(pipe, ScryTransports.Pipe, null, releaseTcpGate: false, cancellationToken);
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

    private async Task AcceptTcpConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket? socket = null;
            try
            {
#if NETFRAMEWORK
                socket = await _tcpListener!.AcceptSocketAsync().ConfigureAwait(false);
#else
                socket = await _tcpListener!.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
#endif
                if (!_tcpConnectionGate.Wait(0))
                {
                    // The pre-handshake cap is full: refuse before spawning a handler or writing
                    // an audit record for this connection, since neither is free.
                    socket.Dispose();
                    continue;
                }

                socket.NoDelay = true;
                var peerAddress = socket.RemoteEndPoint?.ToString();
                var stream = new NetworkStream(socket, ownsSocket: true);
                RegisterConnection(stream, ScryTransports.Tcp, peerAddress, releaseTcpGate: true, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                socket?.Dispose();
                break;
            }
            // A SocketException here matters more than the pipe's IOException equivalent: without
            // catching it, one transient accept failure would fault this loop and TCP would stop
            // accepting silently for the rest of the host's lifetime. ObjectDisposedException and
            // SocketException both also occur here, expectedly, once Dispose stops the listener.
            catch (Exception exception) when (
                exception is IOException or SocketException or ObjectDisposedException)
            {
                socket?.Dispose();
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The tail shared by both accept loops once each has obtained a <see cref="Stream"/>: id
    /// assignment, dictionary bookkeeping, spawning the handler, and its cleanup continuation. The
    /// two loops differ only in how they obtain that stream.
    /// </summary>
    private void RegisterConnection(
        Stream stream,
        string transport,
        string? peerAddress,
        bool releaseTcpGate,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _connectionId);
        _connections[id] = stream;
        var handler = HandleConnectionAsync(id, stream, transport, peerAddress, cancellationToken);
        _handlers[id] = handler;
        _ = handler.ContinueWith(
            (completedTask, state) =>
            {
                var tuple = ((RuntimeHost Host, int Id, bool ReleaseTcpGate))state!;
                tuple.Host._handlers.TryRemove(tuple.Id, out _);
                if (tuple.ReleaseTcpGate)
                {
                    tuple.Host._tcpConnectionGate.Release();
                }

                _ = completedTask.Exception;
            },
            (this, id, releaseTcpGate),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleConnectionAsync(
        int connectionId,
        Stream stream,
        string transport,
        string? peerAddress,
        CancellationToken cancellationToken)
    {
        string? sessionId = null;
        var ephemeralSession = false;
        try
        {
            var handshakeFrame = await FrameCodec.ReadAsync<ProtocolRequest>(stream, cancellationToken)
                .ConfigureAwait(false);
            if (handshakeFrame is null)
            {
                return;
            }

            if (handshakeFrame.Operation != "handshake")
            {
                _audit.Write(DeniedHandshake(connectionId, transport, peerAddress, "handshake_required"));
                await WriteFailureAsync(
                    stream,
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
                _audit.Write(DeniedHandshake(connectionId, transport, peerAddress, "invalid_handshake"));
                await WriteFailureAsync(
                    stream,
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
                // Never the supplied token, even in the audit log - a wrong token here could be a
                // valid token for a different live target, and there can be several.
                _audit.Write(DeniedHandshake(connectionId, transport, peerAddress, "authentication_failed", handshake.ClientName));
                await WriteFailureAsync(
                    stream,
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
                _audit.Write(DeniedHandshake(connectionId, transport, peerAddress, "protocol_version_mismatch", handshake.ClientName));
                await WriteFailureAsync(
                    stream,
                    handshakeFrame.RequestId,
                    null,
                    "protocol_version_mismatch",
                    $"The target supports protocol version {ProtocolConstants.Version}.",
                    null,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var clientName = AuditLog.Bound(handshake.ClientName);
            var session = handshake.SessionId is null
                ? _sessions.Create(clientName)
                : _sessions.Resume(handshake.SessionId, clientName);
            sessionId = session.Id;
            ephemeralSession = handshake.EphemeralSession && handshake.SessionId is null;
            var result = new HandshakeResult(
                ProtocolConstants.Version,
                Metadata,
                session.Id,
                ProtocolConstants.AllCapabilities,
                session.ExpiresAt);
            await FrameCodec.WriteAsync(
                stream,
                ProtocolResponse.Succeeded(handshakeFrame.RequestId, session.Id, result),
                cancellationToken).ConfigureAwait(false);
            _audit.Write(new(
                AuditEventKinds.Handshake,
                DateTimeOffset.UtcNow,
                Metadata.TargetId,
                AuditOutcomes.Succeeded)
            {
                ConnectionId = connectionId,
                Transport = transport,
                PeerAddress = peerAddress,
                SessionId = session.Id,
                ClientName = clientName,
                Detail = handshake.SessionId is null ? "created" : "resumed"
            });

            while (!cancellationToken.IsCancellationRequested)
            {
                var request = await FrameCodec.ReadAsync<ProtocolRequest>(stream, cancellationToken)
                    .ConfigureAwait(false);
                if (request is null)
                {
                    break;
                }

                ProtocolResponse response;
                var operationId = Guid.NewGuid().ToString("N");
                var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                    ? operationId
                    : request.CorrelationId ?? operationId;
                var operationStopwatch = Stopwatch.StartNew();
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
                    var operationResult = request.Operation.StartsWith("job.", StringComparison.Ordinal)
                        ? await _jobs.DispatchAsync(
                            request.Operation,
                            request.Payload,
                            session,
                            correlationId,
                            cancellationToken).ConfigureAwait(false)
                        : await _dispatcher.DispatchAsync(
                            request.Operation,
                            request.Payload,
                            session,
                            new(cancellationToken, operationId, correlationId)).ConfigureAwait(false);
                    response = ProtocolResponse.Succeeded(
                        request.RequestId,
                        session.Id,
                        operationResult,
                        operationId,
                        correlationId);
                }
                catch (Exception exception)
                {
                    var actual = exception is TargetInvocationException { InnerException: { } inner }
                        ? inner
                        : exception;
                    var code = actual switch
                    {
                        ScryOperationException operationException => operationException.Code,
                        ScryCompilationException => "compilation_failed",
                        OperationCanceledException => "operation_canceled",
                        _ => "operation_failed"
                    };
                    response = ProtocolResponse.Failed(
                        request.RequestId,
                        session.Id,
                        new(
                            code,
                            actual.Message,
                            ExceptionDetail.FromException(actual),
                            (actual as ScryOperationException)?.ErrorData,
                            (actual as ScryCompilationException)?.Diagnostics),
                        operationId,
                        correlationId);
                }

                _audit.Write(new(
                    AuditEventKinds.Operation,
                    DateTimeOffset.UtcNow,
                    Metadata.TargetId,
                    response.Success ? AuditOutcomes.Succeeded : AuditOutcomes.Failed)
                {
                    ConnectionId = connectionId,
                    Transport = transport,
                    PeerAddress = peerAddress,
                    SessionId = session.Id,
                    ClientName = session.ClientName,
                    Operation = AuditLog.Bound(request.Operation),
                    OperationId = operationId,
                    CorrelationId = AuditLog.Bound(correlationId),
                    ElapsedMilliseconds = operationStopwatch.ElapsedMilliseconds,
                    ErrorCode = response.Error?.Code,
                    Detail = AuditLog.DescribePayload(request.Operation, request.Payload)
                });

                await FrameCodec.WriteAsync(stream, response, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // Cancellation here means normal shutdown (the host is disposing), not a fault -
            // recording every connection torn down at shutdown would swamp the log with noise
            // that has nothing to do with what any caller did.
            if (!cancellationToken.IsCancellationRequested)
            {
                _audit.Write(new(
                    AuditEventKinds.ConnectionFaulted,
                    DateTimeOffset.UtcNow,
                    Metadata.TargetId,
                    AuditOutcomes.Failed)
                {
                    ConnectionId = connectionId,
                    Transport = transport,
                    PeerAddress = peerAddress,
                    SessionId = sessionId,
                    ErrorCode = exception is ScryOperationException operationException
                        ? operationException.Code
                        : "protocol_error",
                    Detail = AuditLog.Bound(exception.Message)
                });
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await WriteFailureAsync(
                        stream,
                        "unknown",
                        sessionId,
                        exception is ScryOperationException operationException2
                            ? operationException2.Code
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
            stream.Dispose();
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

    private AuditRecord DeniedHandshake(
        int connectionId,
        string transport,
        string? peerAddress,
        string errorCode,
        string? clientName = null) =>
        new(
            AuditEventKinds.Handshake,
            DateTimeOffset.UtcNow,
            Metadata.TargetId,
            AuditOutcomes.Denied)
        {
            ConnectionId = connectionId,
            Transport = transport,
            PeerAddress = peerAddress,
            ErrorCode = errorCode,
            ClientName = AuditLog.Bound(clientName)
        };

    private bool TokenMatches(string candidate)
    {
        var expected = Encoding.UTF8.GetBytes(Descriptor.CapabilityToken);
        var supplied = Encoding.UTF8.GetBytes(candidate);
        var difference = expected.Length ^ supplied.Length;
        for (var index = 0; index < expected.Length; index++)
        {
            difference |= expected[index] ^ (index < supplied.Length ? supplied[index] : 0);
        }

        return difference == 0;
    }

    private NamedPipeServerStream CreatePipe()
    {
#if NETFRAMEWORK
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException("The current Windows identity has no security identifier.");
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            user,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return new NamedPipeServerStream(
            Descriptor.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
#else
        return new NamedPipeServerStream(
            Descriptor.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#endif
    }

    private void PublishDescriptor()
    {
        Directory.CreateDirectory(_targetsDirectory);
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
