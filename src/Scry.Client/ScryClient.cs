using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Scry.Contracts;

namespace Scry.Client;

public sealed class ScryClient : IAsyncDisposable
{
    private readonly Stream _transport;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private int _disposed;

    private ScryClient(
        Stream transport,
        string transportKind,
        ConnectionDescriptor descriptor,
        HandshakeResult handshake)
    {
        _transport = transport;
        Transport = transportKind;
        Descriptor = descriptor;
        Handshake = handshake;
    }

    public ConnectionDescriptor Descriptor { get; }

    public HandshakeResult Handshake { get; }

    /// <summary><see cref="ScryTransports.Pipe"/> or <see cref="ScryTransports.Tcp"/> - which
    /// transport this connection is actually using.</summary>
    public string Transport { get; }

    /// <summary>
    /// How long a single request waits for its response before failing with a
    /// <see cref="TimeoutException"/>. Defaults to 60 seconds.
    /// <para>
    /// This exists because a target can legitimately stop answering and never resume. A
    /// submission marshalled onto the host's UI thread holds that thread until it finishes, and
    /// <c>timeoutMilliseconds</c> is cooperative - it ends a submission that observes its
    /// cancellation token and can do nothing about one that does not. Without a deadline here,
    /// such a submission wedges the caller as well as the target.
    /// </para>
    /// <para>
    /// The default is deliberately generous, because a marshalled submission or a cold Roslyn
    /// compile can take seconds. Lower it for interactive use, raise it when driving work that
    /// is legitimately slow, or set <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.
    /// A cancellation token passed to the request still applies either way.
    /// </para>
    /// <para>
    /// Note that the write side has no matching deadline here or over TCP: <see cref="FrameCodec.WriteAsync{T}"/>
    /// races nothing, so a tunnel that stops draining (rather than the target itself going quiet)
    /// can hold a write indefinitely. This is a pre-existing gap, newly reachable through a
    /// forwarded connection; restructuring the request path to close it is out of scope here.
    /// </para>
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public string SessionId => Handshake.SessionId;

    public static async Task<ScryClient> ConnectAsync(
        ConnectionDescriptor descriptor,
        string? sessionId = null,
        TimeSpan? timeout = null,
        string clientName = "Scry.Client",
        bool ephemeralSession = false,
        CancellationToken cancellationToken = default)
    {
        if (descriptor is null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }

        var pipe = new NamedPipeClientStream(
            ".",
            descriptor.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
#if NETFRAMEWORK
            await pipe.ConnectAsync(Timeout.Infinite, timeoutSource.Token).ConfigureAwait(false);
#else
            await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
#endif
            return await HandshakeAsync(
                pipe,
                ScryTransports.Pipe,
                descriptor,
                sessionId,
                clientName,
                ephemeralSession,
                timeoutSource).ConfigureAwait(false);
        }
        catch
        {
            await DisposeTransportAsync(pipe).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<ScryClient> ConnectAsync(
        string descriptorPath,
        string? sessionId = null,
        TimeSpan? timeout = null,
        string clientName = "Scry.Client",
        bool ephemeralSession = false,
        CancellationToken cancellationToken = default)
    {
        var descriptor = await TargetDiscovery.ReadAsync(descriptorPath, cancellationToken)
            .ConfigureAwait(false);
        return await ConnectAsync(
            descriptor,
            sessionId,
            timeout,
            clientName,
            ephemeralSession,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Connects over TCP instead of the named pipe. Never implicit: a descriptor advertising a TCP
    /// listener still connects over the pipe unless this method (or its
    /// <paramref name="descriptorPath"/> overload) is called explicitly - otherwise every existing
    /// local caller of the pipe overloads above would silently lose the OS peer gate the moment a
    /// host happened to have TCP enabled.
    /// <para>
    /// <paramref name="address"/> is where to actually connect - typically the local end of a port
    /// forward, which need not be the same port the endpoint itself bound. Null (or omitted) means
    /// "use the descriptor's own <see cref="ConnectionDescriptor.TcpAddress"/>/
    /// <see cref="ConnectionDescriptor.TcpPort"/>"; if the descriptor has neither and no address
    /// was supplied, this throws rather than silently falling back to the pipe.
    /// </para>
    /// </summary>
    public static async Task<ScryClient> ConnectOverTcpAsync(
        ConnectionDescriptor descriptor,
        ScryEndpointAddress? address = null,
        string? sessionId = null,
        TimeSpan? timeout = null,
        string clientName = "Scry.Client",
        bool ephemeralSession = false,
        CancellationToken cancellationToken = default)
    {
        if (descriptor is null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }

        var resolved = address ?? ResolveDescriptorAddress(descriptor);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await ConnectSocketAsync(socket, resolved, timeoutSource.Token).ConfigureAwait(false);
            socket.NoDelay = true;
            var stream = new NetworkStream(socket, ownsSocket: true);
            return await HandshakeAsync(
                stream,
                ScryTransports.Tcp,
                descriptor,
                sessionId,
                clientName,
                ephemeralSession,
                timeoutSource).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static async Task<ScryClient> ConnectOverTcpAsync(
        string descriptorPath,
        ScryEndpointAddress? address = null,
        string? sessionId = null,
        TimeSpan? timeout = null,
        string clientName = "Scry.Client",
        bool ephemeralSession = false,
        CancellationToken cancellationToken = default)
    {
        var descriptor = await TargetDiscovery.ReadAsync(descriptorPath, cancellationToken)
            .ConfigureAwait(false);
        return await ConnectOverTcpAsync(
            descriptor,
            address,
            sessionId,
            timeout,
            clientName,
            ephemeralSession,
            cancellationToken).ConfigureAwait(false);
    }

    private static ScryEndpointAddress ResolveDescriptorAddress(ConnectionDescriptor descriptor)
    {
        if (descriptor.TcpAddress is null || descriptor.TcpPort is null)
        {
            throw new InvalidOperationException(
                "This descriptor has no TCP listener. Start the host with " +
                "EndpointOptions.TcpPort (or RuntimeHostOptions.TcpPort) set, or pass an explicit " +
                "address to ConnectOverTcpAsync.");
        }

        return new ScryEndpointAddress(descriptor.TcpAddress, descriptor.TcpPort.Value);
    }

#if NETFRAMEWORK
    private static Task ConnectSocketAsync(
        Socket socket,
        ScryEndpointAddress address,
        CancellationToken cancellationToken)
    {
        // .NET Framework has no cancellable Socket.ConnectAsync, so race BeginConnect/EndConnect
        // against the deadline the same way the pipe path already handles the same TFM gap.
        var endpoint = new IPEndPoint(IPAddress.Parse(address.Host), address.Port);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.BeginConnect(endpoint, static asyncResult =>
        {
            var state = ((Socket Socket, TaskCompletionSource<bool> Completion))asyncResult.AsyncState!;
            try
            {
                state.Socket.EndConnect(asyncResult);
                state.Completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                state.Completion.TrySetException(exception);
            }
        }, (socket, completion));
        return WithCancellationAsync(socket, completion, cancellationToken);
    }

    private static async Task WithCancellationAsync(
        Socket socket,
        TaskCompletionSource<bool> completion,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state =>
        {
            var tuple = ((Socket Socket, TaskCompletionSource<bool> Completion))state!;
            tuple.Completion.TrySetCanceled();
            try
            {
                tuple.Socket.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }, (socket, completion));
        await completion.Task.ConfigureAwait(false);
    }
#else
    private static async Task ConnectSocketAsync(
        Socket socket,
        ScryEndpointAddress address,
        CancellationToken cancellationToken)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(address.Host), address.Port);
        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }
#endif

    /// <summary>
    /// Everything shared by both entry points once each has obtained a connected stream: sending
    /// the handshake frame and interpreting the result. The two <c>ConnectAsync</c> overloads above
    /// (pipe) and <see cref="ConnectOverTcpAsync(ConnectionDescriptor,ScryEndpointAddress?,string?,TimeSpan?,string,bool,CancellationToken)"/>
    /// (TCP) converge here; behavior for the pipe overloads is unchanged from before this method
    /// existed.
    /// </summary>
    private static async Task<ScryClient> HandshakeAsync(
        Stream stream,
        string transportKind,
        ConnectionDescriptor descriptor,
        string? sessionId,
        string clientName,
        bool ephemeralSession,
        CancellationTokenSource timeoutSource)
    {
        var request = new ProtocolRequest(
            ProtocolConstants.Version,
            Guid.NewGuid().ToString("N"),
            "handshake",
            JsonSerializer.SerializeToElement(
                new HandshakeRequest(
                    descriptor.CapabilityToken,
                    SessionId: sessionId,
                    ClientName: clientName,
                    EphemeralSession: ephemeralSession),
                ScryJson.Options));
        await FrameCodec.WriteAsync(stream, request, timeoutSource.Token).ConfigureAwait(false);
        var response = await FrameCodec.ReadAsync<ProtocolResponse>(stream, timeoutSource.Token)
            .ConfigureAwait(false)
            ?? throw new ProtocolException("The target closed during handshake.");
        if (!response.Success)
        {
            throw new ScryRemoteException(response);
        }

        var handshake = response.Result?.Deserialize<HandshakeResult>(ScryJson.Options)
            ?? throw new ProtocolException("The target returned an invalid handshake.");
        return new(stream, transportKind, descriptor, handshake);
    }

    public Task<ProtocolResponse> RequestAsync(
        string operation,
        object? payload = null,
        CancellationToken cancellationToken = default) =>
        RequestCoreAsync(operation, payload, cancellationToken, null);

    public Task<ProtocolResponse> RequestCorrelatedAsync(
        string operation,
        object? payload,
        string? correlationId,
        CancellationToken cancellationToken = default) =>
        RequestCoreAsync(operation, payload, cancellationToken, correlationId);

    /// <summary>
    /// Awaits a pending read but gives up after <paramref name="timeout"/>.
    /// <para>
    /// Racing a delay rather than relying on the cancellation token alone, because on .NET
    /// Framework a <see cref="System.IO.Pipes.PipeStream"/> read does not observe cancellation
    /// once it has started - the token is checked on the way in and then ignored. The same is true
    /// of a <see cref="System.Net.Sockets.NetworkStream"/> read on both frameworks: a socket
    /// receive that has already started ignores the token too, so this race is not a .NET
    /// Framework-only concern once TCP is in the mix. Disposing the stream is what actually ends
    /// such a read, and the caller's catch does that. Without this race the deadline would work
    /// only when the underlying read happens to observe cancellation.
    /// </para>
    /// </summary>
    private static async Task<T?> WithDeadlineAsync<T>(Task<T?> pending, TimeSpan timeout, string operation)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return await pending.ConfigureAwait(false);
        }

        using var delayCancellation = new CancellationTokenSource();
        var delay = Task.Delay(timeout, delayCancellation.Token);
        var completed = await Task.WhenAny(pending, delay).ConfigureAwait(false);
        if (ReferenceEquals(completed, pending))
        {
            delayCancellation.Cancel();
            return await pending.ConfigureAwait(false);
        }

        // The read is still blocked and only ends when the transport is disposed, which happens
        // in the caller's catch. Observe its eventual fault so it does not surface as an
        // unobserved task exception and tear down the host process later.
        _ = pending.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        throw new TimeoutException(
            $"The target did not respond to '{operation}' within {timeout.TotalSeconds:0.##} " +
            "seconds. A submission marshalled onto the host's UI thread holds that thread until " +
            "it returns, and a submission that never observes its cancellation token cannot be " +
            "ended by timeoutMilliseconds - so the target may still be running it. This " +
            "connection is now closed; reconnect to continue.");
    }

    private async Task<ProtocolResponse> RequestCoreAsync(
        string operation,
        object? payload,
        CancellationToken cancellationToken,
        string? correlationId)
    {
        ThrowIfDisposed();
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var ioStarted = false;
        var timeout = RequestTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            deadline.CancelAfter(timeout);
        }

        try
        {
            ThrowIfDisposed();
            var request = new ProtocolRequest(
                ProtocolConstants.Version,
                Guid.NewGuid().ToString("N"),
                operation,
                JsonSerializer.SerializeToElement(payload ?? new { }, ScryJson.Options))
            {
                CorrelationId = correlationId
            };
            ioStarted = true;
            await FrameCodec.WriteAsync(_transport, request, deadline.Token).ConfigureAwait(false);
            var response = await WithDeadlineAsync<ProtocolResponse>(
                    FrameCodec.ReadAsync<ProtocolResponse>(_transport, deadline.Token).AsTask(),
                    timeout,
                    operation)
                .ConfigureAwait(false)
                ?? throw new ProtocolException("The target closed before returning a response.");
            if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
            {
                throw new ProtocolException(
                    $"Response request ID '{response.RequestId}' does not match '{request.RequestId}'.");
            }

            return response;
        }
        // Only our own deadline fired, not the caller's token: report it as a timeout rather
        // than as a cancellation, because the caller did not cancel anything and needs to be
        // told the target went quiet.
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (ioStarted && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await DisposeTransportAsync(_transport).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"The target did not respond to '{operation}' within {timeout.TotalSeconds:0.##} " +
                "seconds. A submission marshalled onto the host's UI thread holds that thread " +
                "until it returns, and a submission that never observes its cancellation token " +
                "cannot be ended by timeoutMilliseconds - so the target may still be running it. " +
                "This connection is now closed; reconnect to continue.");
        }
        catch
        {
            if (ioStarted && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await DisposeTransportAsync(_transport).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public Task<ExecutionResult> EvaluateAsync(
        ExecutionRequest request,
        CancellationToken cancellationToken = default) =>
        RequestResultAsync<ExecutionResult>("evaluate", request, cancellationToken);

    public Task<ExecutionResult> ExecuteAsync(
        ExecutionRequest request,
        CancellationToken cancellationToken = default) =>
        RequestResultAsync<ExecutionResult>("execute", request, cancellationToken);

    public Task<LoadAssemblyResult> LoadAssemblyAsync(
        LoadAssemblyRequest request,
        CancellationToken cancellationToken = default) =>
        RequestResultAsync<LoadAssemblyResult>("load-assembly", request, cancellationToken);

    public Task<ListAssembliesResult> ListAssembliesAsync(
        CancellationToken cancellationToken = default) =>
        RequestResultAsync<ListAssembliesResult>("list-assemblies", null, cancellationToken);

    public Task<FindTypesResult> FindTypesAsync(
        FindTypesRequest request,
        CancellationToken cancellationToken = default) =>
        RequestResultAsync<FindTypesResult>("find-types", request, cancellationToken);

    public Task<TypeDescription> DescribeTypeAsync(
        DescribeTypeRequest request,
        CancellationToken cancellationToken = default) =>
        RequestResultAsync<TypeDescription>("describe-type", request, cancellationToken);

    public Task<ProtocolResponse> StartJobAsync(
        string operation,
        object? payload = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default) =>
        RequestCoreAsync(
            "job.start",
            new JobStartRequest(
                operation,
                JsonSerializer.SerializeToElement(payload ?? new { }, ScryJson.Options),
                correlationId),
            cancellationToken,
            correlationId);

    public Task<ProtocolResponse> GetJobStatusAsync(
        JobHandle job,
        CancellationToken cancellationToken = default) =>
        RequestAsync("job.status", new JobQueryRequest(job), cancellationToken);

    public Task<ProtocolResponse> WaitForJobAsync(
        JobHandle job,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var milliseconds = checked((int)Math.Ceiling(timeout.TotalMilliseconds));
        return RequestAsync(
            "job.wait",
            new JobWaitRequest(job, milliseconds),
            cancellationToken);
    }

    public Task<ProtocolResponse> CancelJobAsync(
        JobHandle job,
        CancellationToken cancellationToken = default) =>
        RequestAsync("job.cancel", new JobQueryRequest(job), cancellationToken);

    public Task<ProtocolResponse> ReadJobLogsAsync(
        JobHandle job,
        long cursor = 0,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        RequestAsync("job.logs", new JobLogsRequest(job, cursor, limit), cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            return DisposeTransportAsync(_transport);
        }

        return default;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed != 0)
        {
            throw new ObjectDisposedException(nameof(ScryClient));
        }
    }

    private static ValueTask DisposeTransportAsync(Stream stream)
    {
#if NETFRAMEWORK
        stream.Dispose();
        return default;
#else
        return stream.DisposeAsync();
#endif
    }

    private async Task<T> RequestResultAsync<T>(
        string operation,
        object? payload,
        CancellationToken cancellationToken)
    {
        var response = await RequestAsync(operation, payload, cancellationToken).ConfigureAwait(false);
        if (!response.Success)
        {
            throw new ScryRemoteException(response);
        }

        if (response.Result is not { } result)
        {
            throw new ProtocolException(
                $"Operation '{operation}' returned no {typeof(T).Name} result.");
        }

        return result.Deserialize<T>(ScryJson.Options)
            ?? throw new ProtocolException(
                $"Operation '{operation}' returned an invalid {typeof(T).Name} result.");
    }
}

public sealed class ScryRemoteException : Exception
{
    public ScryRemoteException(ProtocolResponse response)
        : base(response.Error?.Message ?? "The Scry target rejected the request.")
    {
        Response = response;
    }

    public ProtocolResponse Response { get; }
}
