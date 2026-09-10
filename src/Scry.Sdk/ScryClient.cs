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
        ArgumentNullException.ThrowIfNull(descriptor);
        var pipe = new NamedPipeClientStream(
            ".",
            descriptor.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
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
            await pipe.DisposeAsync().ConfigureAwait(false);
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

    public async Task<ProtocolResponse> RequestAsync(
        string operation,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var ioStarted = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var request = new ProtocolRequest(
                ProtocolConstants.Version,
                Guid.NewGuid().ToString("N"),
                operation,
                JsonSerializer.SerializeToElement(payload ?? new { }, ScryJson.Options));
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
                await _pipe.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }
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
