using System.IO.Pipes;
using System.Text.Json;
using Scry.Contracts;

namespace Scry.Sdk;

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

    public string SessionId => Handshake.SessionId;

    public static async Task<ScryClient> ConnectAsync(
        ConnectionDescriptor descriptor,
        string? sessionId = null,
        TimeSpan? timeout = null,
        string clientName = "Scry.Sdk",
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
#if NET48
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
        string clientName = "Scry.Sdk",
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

    private async Task<ProtocolResponse> RequestCoreAsync(
        string operation,
        object? payload,
        CancellationToken cancellationToken,
        string? correlationId)
    {
        ThrowIfDisposed();
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var ioStarted = false;
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
            await FrameCodec.WriteAsync(_pipe, request, cancellationToken).ConfigureAwait(false);
            var response = await FrameCodec.ReadAsync<ProtocolResponse>(_pipe, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new ProtocolException("The target closed before returning a response.");
            if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
            {
                throw new ProtocolException(
                    $"Response request ID '{response.RequestId}' does not match '{request.RequestId}'.");
            }

            return response;
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
#if NET48
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
