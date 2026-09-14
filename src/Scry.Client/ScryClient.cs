using System.IO.Pipes;
using System.Text.Json;
using Scry.Contracts;

namespace Scry.Client;

public sealed class ScryClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private int _disposed;

    private ScryClient(
        NamedPipeClientStream pipe,
        ConnectionDescriptor descriptor,
        HandshakeResult handshake)
    {
        _pipe = pipe;
        Descriptor = descriptor;
        Handshake = handshake;
    }

    public ConnectionDescriptor Descriptor { get; }

    public HandshakeResult Handshake { get; }

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
            await FrameCodec.WriteAsync(pipe, request, timeoutSource.Token).ConfigureAwait(false);
            var response = await FrameCodec.ReadAsync<ProtocolResponse>(pipe, timeoutSource.Token)
                .ConfigureAwait(false)
                ?? throw new ProtocolException("The target closed during handshake.");
            if (!response.Success)
            {
                throw new ScryRemoteException(response);
            }

            var handshake = response.Result?.Deserialize<HandshakeResult>(ScryJson.Options)
                ?? throw new ProtocolException("The target returned an invalid handshake.");
            return new(pipe, descriptor, handshake);
        }
        catch
        {
            await DisposePipeAsync(pipe).ConfigureAwait(false);
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
    /// once it has started - the token is checked on the way in and then ignored. Disposing the
    /// stream is what actually ends such a read, and the caller's catch does that. Without this
    /// race the deadline works on .NET 9 and silently does nothing on .NET Framework, which is
    /// the worse of the two failures because it only shows up on the target most likely to be
    /// running a UI.
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

        // The read is still blocked and only ends when the pipe is disposed, which happens in the
        // caller's catch. Observe its eventual fault so it does not surface as an unobserved
        // task exception and tear down the host process later.
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
            await FrameCodec.WriteAsync(_pipe, request, deadline.Token).ConfigureAwait(false);
            var response = await WithDeadlineAsync<ProtocolResponse>(
                    FrameCodec.ReadAsync<ProtocolResponse>(_pipe, deadline.Token).AsTask(),
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
                await DisposePipeAsync(_pipe).ConfigureAwait(false);
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
                await DisposePipeAsync(_pipe).ConfigureAwait(false);
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
            return DisposePipeAsync(_pipe);
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

    private static ValueTask DisposePipeAsync(NamedPipeClientStream pipe)
    {
#if NETFRAMEWORK
        pipe.Dispose();
        return default;
#else
        return pipe.DisposeAsync();
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
