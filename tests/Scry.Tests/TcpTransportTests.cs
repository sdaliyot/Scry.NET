using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Scry.Contracts;
using Scry.Endpoint;
using Scry.Client;

namespace Scry.Tests;

/// <summary>
/// Covers the loopback TCP transport: a target started with a TCP listener alongside its named
/// pipe, reachable through an in-process forwarder that stands in for a real port forward
/// (<c>ssh -L</c>, <c>netsh portproxy</c>). Everything here can run on one machine because a
/// loopback listener plus a second loopback listener pumping both directions with
/// <see cref="Stream.CopyToAsync(Stream)"/> is exactly that shape.
/// </summary>
[Collection("Scry integration")]
public sealed class TcpTransportTests
{
    [Fact]
    public async Task Tcp_is_off_by_default_and_connecting_over_it_names_the_option()
    {
        await using var host = EndpointHost.Start();

        var json = File.ReadAllText(host.DescriptorPath);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("tcpAddress", out _));
        Assert.False(document.RootElement.TryGetProperty("tcpPort", out _));
        Assert.Null(host.TcpPort);

        var descriptor = await TargetDiscovery.ReadAsync(host.DescriptorPath);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ScryClient.ConnectOverTcpAsync(descriptor));
        Assert.Contains("TcpPort", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protocol_parity_sessions_and_handles_work_over_tcp_and_sessions_cross_transports()
    {
        await using var host = EndpointHost.Start(
            builder => builder
                .RegisterValue("greeting", "hello")
                .RegisterValue("widget", new Widget()),
            new EndpointOptions { TcpPort = 0 });
        Assert.NotNull(host.TcpPort);

        await using (var client = await ScryClient.ConnectOverTcpAsync(host.DescriptorPath))
        {
            Assert.Equal(ScryTransports.Tcp, client.Transport);

            var roots = await client.RequestAsync("roots");
            Assert.True(roots.Success);

            var evaluated = await client.EvaluateAsync(new ExecutionRequest("1 + 1"));
            Assert.Equal(2, evaluated.Value.Value!.Value.GetInt32());

            // "Self" is a reference type (not a scalar), so Encode always leases it regardless of
            // asReference - the shape that actually exercises a handle over TCP.
            var leased = await client.RequestAsync(
                "get",
                new { root = "widget", member = "Self", asReference = true });
            Assert.True(leased.Success, leased.Error?.Message);
            var reference = leased.Result!.Value.GetProperty("value").GetProperty("reference")
                .Deserialize<ExternalReference>(ScryJson.Options)!;

            var invoked = await client.RequestAsync(
                "invoke",
                new { reference, member = "GetName" });
            Assert.True(invoked.Success, invoked.Error?.Message);
            Assert.Equal(
                "scry",
                invoked.Result!.Value.GetProperty("value").GetProperty("value").GetString());

            var released = await client.RequestAsync(
                "release",
                new { handleId = reference.HandleId });
            Assert.True(released.Success);

            // The real proof sessions are transport-independent: resume the TCP session over the
            // pipe.
            await using var overPipe = await ScryClient.ConnectAsync(
                host.DescriptorPath,
                sessionId: client.SessionId);
            Assert.Equal(ScryTransports.Pipe, overPipe.Transport);
            Assert.Equal(client.SessionId, overPipe.SessionId);
            var capabilities = await overPipe.RequestAsync("capabilities");
            Assert.True(capabilities.Success);
        }

        // And the other direction: a session created over the pipe, resumed over TCP.
        await using var pipeFirst = await ScryClient.ConnectAsync(host.DescriptorPath);
        await using var overTcp = await ScryClient.ConnectOverTcpAsync(
            host.DescriptorPath,
            sessionId: pipeFirst.SessionId);
        Assert.Equal(pipeFirst.SessionId, overTcp.SessionId);
        Assert.True((await overTcp.RequestAsync("capabilities")).Success);
    }

    [Fact]
    public async Task The_tcp_listener_binds_loopback_only()
    {
        await using var host = EndpointHost.Start(options: new EndpointOptions { TcpPort = 0 });
        var port = host.TcpPort!.Value;

        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        Assert.Contains(listeners, endpoint => endpoint.Address.Equals(IPAddress.Loopback) && endpoint.Port == port);
        Assert.DoesNotContain(listeners, endpoint => endpoint.Address.Equals(IPAddress.Any) && endpoint.Port == port);
        Assert.DoesNotContain(listeners, endpoint => endpoint.Address.Equals(IPAddress.IPv6Any) && endpoint.Port == port);
    }

    [Fact]
    public async Task An_address_override_reaches_the_listener_through_a_forwarder_despite_a_bogus_descriptor_port()
    {
        await using var host = EndpointHost.Start(options: new EndpointOptions { TcpPort = 0 });
        var descriptor = await TargetDiscovery.ReadAsync(host.DescriptorPath);

        await using var forwarder = await Forwarder.StartAsync(descriptor.TcpPort!.Value);

        // The descriptor's own port is deliberately wrong; the token still has to come from the
        // descriptor while the address comes entirely from the override.
        var bogusDescriptor = descriptor with { TcpPort = 1 };
        await using var client = await ScryClient.ConnectOverTcpAsync(
            bogusDescriptor,
            new ScryEndpointAddress("127.0.0.1", forwarder.Port));
        Assert.True((await client.RequestAsync("capabilities")).Success);

#if NET9_0_OR_GREATER
        // Repeated through the CLI, with the (bogus-port) descriptor written to a path outside
        // the targets directory - the shape of the actual remote-descriptor workflow.
        var descriptorPath = Path.Combine(Path.GetTempPath(), $"scry-forwarded-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            descriptorPath,
            JsonSerializer.Serialize(bogusDescriptor, ScryJson.Options));
        try
        {
            var cliPath = FindBuiltCli();
            var result = await RunCliAsync(
                cliPath,
                [
                    "capabilities",
                    "--descriptor", descriptorPath,
                    "--address", $"127.0.0.1:{forwarder.Port}"
                ]);
            Assert.True(
                result.ExitCode == 0,
                $"Expected exit 0, got {result.ExitCode}: {result.StandardOutput}{result.StandardError}");
        }
        finally
        {
            File.Delete(descriptorPath);
        }
#endif
    }

    [Fact]
    public async Task A_denied_tcp_handshake_is_attributable_without_leaking_the_presented_token()
    {
        var records = new List<AuditRecord>();
        await using var host = EndpointHost.Start(options: new EndpointOptions
        {
            TcpPort = 0,
            AuditEnabled = false,
            AuditCallback = record => { lock (records) { records.Add(record); } }
        });
        var descriptor = await TargetDiscovery.ReadAsync(host.DescriptorPath);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, descriptor.TcpPort!.Value);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        const string presentedToken = "definitely-not-the-real-token";
        var request = new ProtocolRequest(
            ProtocolConstants.Version,
            Guid.NewGuid().ToString("N"),
            "handshake",
            JsonSerializer.SerializeToElement(
                new HandshakeRequest(presentedToken),
                ScryJson.Options));
        await FrameCodec.WriteAsync(stream, request);
        var response = await FrameCodec.ReadAsync<ProtocolResponse>(stream);
        Assert.False(response!.Success);
        Assert.Equal("authentication_failed", response.Error!.Code);

        // Give the audit drain a moment to catch up; it is enqueue-only and asynchronous.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        AuditRecord? denied;
        do
        {
            lock (records)
            {
                denied = records.FirstOrDefault(record =>
                    record.Kind == AuditEventKinds.Handshake && record.Outcome == AuditOutcomes.Denied);
            }

            if (denied is not null)
            {
                break;
            }

            await Task.Delay(20);
        } while (DateTime.UtcNow < deadline);

        Assert.NotNull(denied);
        Assert.Equal(ScryTransports.Tcp, denied!.Transport);
        Assert.StartsWith("127.0.0.1:", denied.PeerAddress, StringComparison.Ordinal);

        lock (records)
        {
            Assert.All(records, record =>
            {
                Assert.DoesNotContain(presentedToken, record.Detail ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain(presentedToken, record.ClientName ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain(presentedToken, record.ErrorCode ?? string.Empty, StringComparison.Ordinal);
            });
        }
    }

    [Fact]
    public async Task A_foreign_descriptor_survives_discovery_while_a_stale_local_one_is_still_deleted()
    {
        Directory.CreateDirectory(TargetDiscovery.DirectoryPath);

        using var process = Process.GetCurrentProcess();
        var foreignId = Guid.NewGuid().ToString("N");
        var foreignDescriptor = new ConnectionDescriptor(
            ProtocolConstants.Version,
            new TargetMetadata(
                foreignId,
                "foreign-alias",
                process.Id,
                process.ProcessName,
                Environment.Version.ToString(),
                ".NET",
                "x64",
                process.StartTime.ToUniversalTime()),
            "scry-foreign-pipe",
            "foreign-token",
            DateTimeOffset.UtcNow,
            MachineName: "OTHER-HOST-" + Guid.NewGuid().ToString("N"));
        var foreignPath = TargetDiscovery.GetDescriptorPath(foreignId);
        File.WriteAllText(
            foreignPath,
            JsonSerializer.Serialize(foreignDescriptor, ScryJson.Options));

        // The negative twin: a null MachineName (an old-shaped descriptor) pointing at a process
        // ID that is essentially guaranteed not to be running with a matching start time, which is
        // what today's staleness check already deletes.
        var staleId = Guid.NewGuid().ToString("N");
        var staleDescriptor = new ConnectionDescriptor(
            ProtocolConstants.Version,
            new TargetMetadata(
                staleId,
                "stale-alias",
                int.MaxValue - 1,
                "nonexistent",
                Environment.Version.ToString(),
                ".NET",
                "x64",
                DateTimeOffset.UtcNow.AddDays(-1)),
            "scry-stale-pipe",
            "stale-token",
            DateTimeOffset.UtcNow);
        var stalePath = TargetDiscovery.GetDescriptorPath(staleId);
        File.WriteAllText(
            stalePath,
            JsonSerializer.Serialize(staleDescriptor, ScryJson.Options));

        try
        {
            var found = await TargetDiscovery.FindAsync();
            Assert.DoesNotContain(found, item => item.Descriptor.Target.TargetId == foreignId);
            Assert.DoesNotContain(found, item => item.Descriptor.Target.TargetId == staleId);
            Assert.True(File.Exists(foreignPath), "A foreign descriptor must not be deleted.");
            Assert.False(File.Exists(stalePath), "A stale local descriptor must still be deleted.");
        }
        finally
        {
            TryDeleteFile(foreignPath);
            TryDeleteFile(stalePath);
        }
    }

    /// <summary>A reference type (not a scalar) so leasing it exercises a real handle over TCP.</summary>
    private sealed class Widget
    {
        public string Name { get; set; } = "scry";

        public Widget Self => this;

        public string GetName() => Name;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void An_old_five_field_descriptor_round_trips_on_this_tfm()
    {
        const string json = """
            {
                "protocolVersion": 1,
                "target": {
                    "targetId": "abc123",
                    "alias": "legacy-app",
                    "processId": 4242,
                    "processName": "legacy",
                    "runtimeVersion": "9.0.0",
                    "frameworkDescription": ".NET 9.0",
                    "architecture": "x64",
                    "startedAt": "2024-01-01T00:00:00Z"
                },
                "pipeName": "scry-legacy",
                "capabilityToken": "legacy-token",
                "publishedAt": "2024-01-01T00:00:01Z"
            }
            """;
        var descriptor = JsonSerializer.Deserialize<ConnectionDescriptor>(json, ScryJson.Options);
        Assert.NotNull(descriptor);
        Assert.Null(descriptor!.TcpAddress);
        Assert.Null(descriptor.TcpPort);
        Assert.Null(descriptor.MachineName);
        Assert.Equal("legacy-app", descriptor.Target.Alias);
    }

    [Fact]
    public async Task Disposal_unblocks_a_pending_tcp_read_and_the_port_is_refused_afterwards()
    {
        int port;
        {
            await using var host = EndpointHost.Start(options: new EndpointOptions { TcpPort = 0 });
            port = host.TcpPort!.Value;
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port);
            using var stream = new NetworkStream(socket, ownsSocket: false);

            // Start a read that will never get a reply and is only unblocked by host disposal.
            var pendingRead = FrameCodec.ReadAsync<ProtocolResponse>(stream).AsTask();
            var finished = await Task.WhenAny(pendingRead, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.NotEqual(pendingRead, finished);

            // host disposal happens at the end of this block; the read must not still be pending
            // after it, and it must not hang the test doing so.
            var disposeThenRead = Task.Run(async () =>
            {
                await host.DisposeAsync();
                return pendingRead;
            });
            var overall = await Task.WhenAny(disposeThenRead, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Equal(disposeThenRead, overall);
        }

        // Disposed: a fresh connection to the same port must be refused, not silently accepted.
        using var refused = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await Assert.ThrowsAsync<SocketException>(
            () => refused.ConnectAsync(IPAddress.Loopback, port));
    }

#if NET9_0_OR_GREATER
    [Fact]
    public async Task Schema_lists_address_under_common_options_and_tcp_port_under_attach()
    {
        var result = await RunCliAsync(FindBuiltCli(), ["schema"]);
        Assert.Equal(0, result.ExitCode);
        using var schema = JsonDocument.Parse(result.StandardOutput);
        var commonOptionNames = schema.RootElement.GetProperty("commonOptions")
            .EnumerateArray()
            .Select(field => field.GetProperty("name").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("--address", commonOptionNames);

        var attachCommand = schema.RootElement.GetProperty("commands")
            .EnumerateArray()
            .Single(command => command.GetProperty("command").GetString() == "attach");
        var attachFieldNames = attachCommand.GetProperty("request")
            .GetProperty("fields")
            .EnumerateArray()
            .Select(field => field.GetProperty("name").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("--tcp-port", attachFieldNames);
    }

    private static string FindBuiltCli()
    {
        var root = FindRepositoryRoot();
        var targetFramework = new DirectoryInfo(AppContext.BaseDirectory).Name;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new DirectoryNotFoundException("Could not determine the test configuration.");
        var path = Path.Combine(root, "src", "Scry.Cli", "bin", configuration, targetFramework, "scry.dll");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Could not locate a current CLI assembly.", path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scry.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static async Task<ProcessResult> RunCliAsync(
        string cliPath,
        IReadOnlyList<string> arguments,
        string? standardInput = null)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(cliPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the scry CLI.");
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode, await output, await error);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
#endif

    /// <summary>
    /// A loopback TCP listener that pumps both directions to another loopback port, standing in
    /// for a real port forward (<c>ssh -L</c>, <c>netsh portproxy</c>) so the address-override
    /// behaviour can be exercised on one machine.
    /// </summary>
    private sealed class Forwarder : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _targetPort;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _acceptLoop;

        private Forwarder(TcpListener listener, int targetPort)
        {
            _listener = listener;
            _targetPort = targetPort;
            _acceptLoop = AcceptAsync(_stopping.Token);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static Task<Forwarder> StartAsync(int targetPort)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new Forwarder(listener, targetPort));
        }

        private async Task AcceptAsync(CancellationToken cancellationToken)
        {
            // Portable across net472 and net9.0: neither TcpListener.AcceptSocketAsync nor
            // Socket.ConnectAsync(IPAddress,int) took a CancellationToken until well after
            // net472, so this relies on Stop()/Dispose() to unblock a pending accept, the same
            // way RuntimeHost's own TCP accept loop does.
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Socket inbound;
                    try
                    {
                        inbound = await _listener.AcceptSocketAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        exception is SocketException or ObjectDisposedException or InvalidOperationException)
                    {
                        break;
                    }

                    _ = PumpAsync(inbound, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task PumpAsync(Socket inbound, CancellationToken cancellationToken)
        {
            using var inboundSocket = inbound;
            using var outboundSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await outboundSocket.ConnectAsync(IPAddress.Loopback, _targetPort).ConfigureAwait(false);
                inboundSocket.NoDelay = true;
                outboundSocket.NoDelay = true;
                using var inboundStream = new NetworkStream(inboundSocket, ownsSocket: false);
                using var outboundStream = new NetworkStream(outboundSocket, ownsSocket: false);
#if NETFRAMEWORK
                var a = inboundStream.CopyToAsync(outboundStream);
                var b = outboundStream.CopyToAsync(inboundStream);
#else
                var a = inboundStream.CopyToAsync(outboundStream, cancellationToken);
                var b = outboundStream.CopyToAsync(inboundStream, cancellationToken);
#endif
                await Task.WhenAny(a, b).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
#if NETFRAMEWORK
            _stopping.Cancel();
#else
            await _stopping.CancelAsync().ConfigureAwait(false);
#endif
            _listener.Stop();
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }
}
