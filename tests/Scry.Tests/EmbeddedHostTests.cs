using System.Text.Json;
using System.Diagnostics;
using System.IO.Pipes;
using Scry.Contracts;
using Scry.Sdk;

namespace Scry.Tests;

public sealed class EmbeddedHostTests
{
    [Fact]
    public async Task Host_supports_handshake_discovery_and_cleans_up()
    {
        string descriptorPath;
        string targetId;
        await using (var fixture = TestHost.Start())
        {
            descriptorPath = fixture.Host.DescriptorPath;
            targetId = fixture.Host.Metadata.TargetId;
            Assert.True(File.Exists(descriptorPath));

            var discovered = await TargetDiscovery.FindAsync();
            Assert.Contains(discovered, item => item.Descriptor.Target.TargetId == targetId);

            await using var client = await ScryClient.ConnectAsync(descriptorPath);
            Assert.Equal(targetId, client.Handshake.Target.TargetId);
            Assert.Contains("inspect", client.Handshake.Capabilities);

            var capabilities = await client.RequestAsync("capabilities");
            Assert.True(capabilities.Success);
            Assert.Equal(
                ProtocolConstants.Version,
                capabilities.Result!.Value.GetProperty("protocolVersion").GetInt32());
        }

        Assert.False(File.Exists(descriptorPath));
        Assert.DoesNotContain(
            await TargetDiscovery.FindAsync(),
            item => item.Descriptor.Target.TargetId == targetId);
    }

    [Fact]
    public async Task Discovery_ignores_and_removes_malformed_descriptors()
    {
        Directory.CreateDirectory(TargetDiscovery.DirectoryPath);
        var path = TargetDiscovery.GetDescriptorPath($"malformed-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, "{}");
        try
        {
            _ = await TargetDiscovery.FindAsync();
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Handshake_rejects_an_invalid_capability_token()
    {
        await using var fixture = TestHost.Start();
        var descriptor = await TargetDiscovery.ReadAsync(fixture.Host.DescriptorPath);
        descriptor = descriptor with { CapabilityToken = Convert.ToBase64String(new byte[32]) };

        var exception = await Assert.ThrowsAsync<ScryRemoteException>(async () =>
            await ScryClient.ConnectAsync(descriptor));

        Assert.Equal("authentication_failed", exception.Response.Error?.Code);
    }

    [Fact]
    public async Task Handshake_negotiates_versions_and_is_required_first()
    {
        await using var fixture = TestHost.Start();
        var descriptor = await TargetDiscovery.ReadAsync(fixture.Host.DescriptorPath);

        await using (var pipe = await ConnectPipeAsync(descriptor))
        {
            var request = new ProtocolRequest(
                ProtocolConstants.Version,
                "wrong-version",
                "handshake",
                JsonSerializer.SerializeToElement(
                    new HandshakeRequest(descriptor.CapabilityToken, 2, 2),
                    ScryJson.Options));
            await FrameCodec.WriteAsync(pipe, request);
            var response = await FrameCodec.ReadAsync<ProtocolResponse>(pipe);
            Assert.Equal("protocol_version_mismatch", response?.Error?.Code);
        }

        await using (var pipe = await ConnectPipeAsync(descriptor))
        {
            var request = new ProtocolRequest(
                ProtocolConstants.Version,
                "no-handshake",
                "capabilities",
                JsonSerializer.SerializeToElement(new { }, ScryJson.Options));
            await FrameCodec.WriteAsync(pipe, request);
            var response = await FrameCodec.ReadAsync<ProtocolResponse>(pipe);
            Assert.Equal("handshake_required", response?.Error?.Code);
        }
    }

    [Fact]
    public async Task Reflection_operations_traverse_mutate_and_report_exceptions()
    {
        await using var fixture = TestHost.Start();
        await using var client = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath);

        var roots = await client.RequestAsync("roots");
        var stateReference = RootReference(roots, "state");

        var get = await client.RequestAsync("get", new
        {
            reference = stateReference,
            member = "Count"
        });
        Assert.Equal(7, ScalarFrom(get));

        var set = await client.RequestAsync("set", new
        {
            reference = stateReference,
            member = "Count",
            value = 42
        });
        Assert.True(set.Success);
        Assert.Equal(42, fixture.State.Count);

        var hidden = await client.RequestAsync("get", new
        {
            reference = stateReference,
            member = "Secret",
            includeNonPublic = true
        });
        Assert.Equal("hidden", ScalarFrom(hidden));

        var deniedSetter = await client.RequestAsync("set", new
        {
            reference = stateReference,
            member = "PublicWithPrivateSetter",
            value = 9
        });
        Assert.Equal("member_not_found", deniedSetter.Error?.Code);
        Assert.Equal(3, fixture.State.PublicWithPrivateSetter);

        var allowedSetter = await client.RequestAsync("set", new
        {
            reference = stateReference,
            member = "PublicWithPrivateSetter",
            value = 9,
            includeNonPublic = true
        });
        Assert.True(allowedSetter.Success);
        Assert.Equal(9, fixture.State.PublicWithPrivateSetter);

        var inheritedHidden = await client.RequestAsync("get", new
        {
            reference = stateReference,
            member = "BaseSecret",
            includeNonPublic = true
        });
        Assert.Equal("base-hidden", ScalarFrom(inheritedHidden));

        var invoked = await client.RequestAsync("invoke", new
        {
            reference = stateReference,
            member = "Greet",
            arguments = new object[] { "Ada" }
        });
        Assert.Equal("Hello Ada", ScalarFrom(invoked));

        var failed = await client.RequestAsync("invoke", new
        {
            reference = stateReference,
            member = "Fail"
        });
        Assert.False(failed.Success);
        Assert.Equal("operation_failed", failed.Error?.Code);
        Assert.Equal(typeof(InvalidOperationException).FullName, failed.Error?.Exception?.Type);
        Assert.Equal("boom", failed.Error?.Exception?.Message);
        Assert.Equal(typeof(ArgumentException).FullName, failed.Error?.Exception?.InnerException?.Type);
        Assert.NotNull(failed.Error?.Exception?.StackTrace);
        Assert.NotEqual(0, failed.Error?.Exception?.HResult);

        var valueTask = await client.RequestAsync("invoke", new
        {
            reference = stateReference,
            member = "DoubleAsync",
            arguments = new object[] { 6 }
        });
        Assert.Equal(12, ScalarFrom(valueTask));

        var ordinaryDto = await client.RequestAsync("invoke", new
        {
            reference = stateReference,
            member = "ReadHandleId",
            arguments = new object[] { new { handleId = "ordinary-data" } }
        });
        Assert.Equal("ordinary-data", ScalarFrom(ordinaryDto));

        var ambiguous = await client.RequestAsync("invoke", new
        {
            reference = stateReference,
            member = "Ambiguous",
            arguments = new object[] { 1 }
        });
        Assert.Equal("ambiguous_member", ambiguous.Error?.Code);
    }

    [Fact]
    public async Task Handles_are_stable_scoped_releasable_and_leased()
    {
        await using var fixture = TestHost.Start(handleLease: TimeSpan.FromMilliseconds(100));
        await using var first = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath);

        var firstRoots = await first.RequestAsync("roots");
        var reference = RootReference(firstRoots, "state");
        var secondRoots = await first.RequestAsync("roots");
        var stableReference = RootReference(secondRoots, "state");
        Assert.Equal(reference.HandleId, stableReference.HandleId);

        await using var otherSession = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath);
        var crossSession = await otherSession.RequestAsync("inspect", new { reference });
        Assert.Equal("reference_scope_mismatch", crossSession.Error?.Code);

        var release = await first.RequestAsync("release", new { handleId = reference.HandleId });
        Assert.Equal(1, release.Result!.Value.GetProperty("released").GetInt32());
        var released = await first.RequestAsync("inspect", new { reference });
        Assert.Equal("handle_not_found", released.Error?.Code);

        var refreshedRoots = await first.RequestAsync("roots");
        var expiringReference = RootReference(refreshedRoots, "state");
        await Task.Delay(250);
        var expired = await first.RequestAsync("inspect", new { reference = expiringReference });
        Assert.Equal("handle_not_found", expired.Error?.Code);
    }

    [Fact]
    public async Task Collections_page_and_registered_operations_execute()
    {
        await using var fixture = TestHost.Start();
        await using var client = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath);
        var roots = await client.RequestAsync("roots");
        var numbers = roots.Result!.Value.GetProperty("roots")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "numbers");
        var numbersReference = ReferenceFrom(numbers.GetProperty("value"));

        var page = await client.RequestAsync("enumerate", new
        {
            reference = numbersReference,
            offset = 1,
            limit = 2
        });
        var items = page.Result!.Value.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(2, items[0].GetProperty("value").GetInt32());
        Assert.Equal(3, items[1].GetProperty("value").GetInt32());
        Assert.True(page.Result.Value.GetProperty("hasMore").GetBoolean());

        var operation = await client.RequestAsync("invoke", new
        {
            registeredOperation = "echo",
            arguments = new { message = "hello" }
        });
        var operationValue = ReferenceFrom(operation.Result!.Value.GetProperty("value"));
        var message = await client.RequestAsync("get", new
        {
            reference = operationValue,
            member = "Message"
        });
        Assert.Equal("hello", ScalarFrom(message));
    }

    [Fact]
    public async Task A_session_can_resume_and_use_existing_handles()
    {
        await using var fixture = TestHost.Start();
        ExternalReference reference;
        string sessionId;
        await using (var first = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath))
        {
            sessionId = first.SessionId;
            var roots = await first.RequestAsync("roots");
            reference = RootReference(roots, "state");
        }

        await using var resumed = await ScryClient.ConnectAsync(
            fixture.Host.DescriptorPath,
            sessionId);
        var response = await resumed.RequestAsync("get", new
        {
            reference,
            member = "Count"
        });
        Assert.Equal(7, ScalarFrom(response));
    }

    [Fact]
    public async Task Expired_sessions_cannot_be_resumed()
    {
        await using var fixture = TestHost.Start(sessionLease: TimeSpan.FromMilliseconds(100));
        string sessionId;
        await using (var client = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath))
        {
            sessionId = client.SessionId;
        }

        await Task.Delay(250);
        var exception = await Assert.ThrowsAsync<ScryRemoteException>(async () =>
            await ScryClient.ConnectAsync(fixture.Host.DescriptorPath, sessionId));
        Assert.Equal("session_not_found", exception.Response.Error?.Code);
    }

    [Fact]
    public async Task Multiple_targets_coexist_in_discovery()
    {
        await using var first = TestHost.Start();
        await using var second = TestHost.Start();

        var discovered = await TargetDiscovery.FindAsync();

        Assert.Contains(discovered, item => item.Descriptor.Target.TargetId == first.Host.TargetId);
        Assert.Contains(discovered, item => item.Descriptor.Target.TargetId == second.Host.TargetId);
        Assert.NotEqual(first.Host.TargetId, second.Host.TargetId);
    }

    [Fact]
    public async Task Client_serializes_concurrent_requests()
    {
        await using var fixture = TestHost.Start();
        await using var client = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => client.RequestAsync("capabilities")));

        Assert.All(responses, response => Assert.True(response.Success));
        Assert.Equal(20, responses.Select(response => response.RequestId).Distinct().Count());
    }

    [Fact]
    public async Task Cancellation_faults_the_client_connection()
    {
        await using var fixture = TestHost.Start();
        await using var client = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.RequestAsync(
                "invoke",
                new { registeredOperation = "slow" },
                cancellation.Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            client.RequestAsync("capabilities"));
    }

    [Fact]
    public async Task Ephemeral_sessions_release_capacity_on_disconnect()
    {
        await using var fixture = TestHost.Start(maximumSessions: 1);
        await using (var ephemeral = await ScryClient.ConnectAsync(
            fixture.Host.DescriptorPath,
            ephemeralSession: true))
        {
            Assert.True((await ephemeral.RequestAsync("capabilities")).Success);
        }

        await Task.Delay(100);
        await using var next = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath);
        Assert.True((await next.RequestAsync("capabilities")).Success);
    }

    [Fact]
    public async Task Cli_connects_by_descriptor_without_exposing_the_token()
    {
        await using var fixture = TestHost.Start();
        var descriptor = await TargetDiscovery.ReadAsync(fixture.Host.DescriptorPath);
        var root = FindRepositoryRoot();
        var cliPath = Directory.EnumerateFiles(
                Path.Combine(root, "src", "Scry.Cli", "bin"),
                "scry.dll",
                SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(cliPath);
        startInfo.ArgumentList.Add("capabilities");
        startInfo.ArgumentList.Add("--descriptor");
        startInfo.ArgumentList.Add(fixture.Host.DescriptorPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the scry CLI.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, error);
        using var document = JsonDocument.Parse(output);
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        Assert.DoesNotContain(descriptor.CapabilityToken, output, StringComparison.Ordinal);
    }

    private static ExternalReference ReferenceFrom(JsonElement remoteValue) =>
        remoteValue.GetProperty("reference").Deserialize<ExternalReference>(ScryJson.Options)
        ?? throw new InvalidOperationException("Expected a remote reference.");

    private static ExternalReference RootReference(ProtocolResponse response, string name)
    {
        var root = response.Result!.Value.GetProperty("roots")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == name);
        return ReferenceFrom(root.GetProperty("value"));
    }

    private static object? ScalarFrom(ProtocolResponse response)
    {
        Assert.True(response.Success, response.Error?.Message);
        var value = response.Result!.Value.GetProperty("value").GetProperty("value");
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt32(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value.GetRawText()
        };
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

    private static async Task<NamedPipeClientStream> ConnectPipeAsync(ConnectionDescriptor descriptor)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            descriptor.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000);
        return pipe;
    }

    private sealed class TestHost : IAsyncDisposable
    {
        private TestHost(AgentHost host, TestState state)
        {
            Host = host;
            State = state;
        }

        public AgentHost Host { get; }

        public TestState State { get; }

        public static TestHost Start(
            TimeSpan? handleLease = null,
            TimeSpan? sessionLease = null,
            int maximumSessions = 256)
        {
            var state = new TestState();
            var host = AgentHost.Start(
                builder => builder
                    .RegisterValue("state", state)
                    .RegisterValue("numbers", state.Numbers)
                    .RegisterOperation(
                        "echo",
                        arguments => new EchoResult(arguments.GetProperty("message").GetString()))
                    .RegisterOperation(
                        "slow",
                        async (_, cancellationToken) =>
                        {
                            await Task.Delay(500, cancellationToken);
                            return null;
                        }),
                new AgentHostOptions
                {
                    Alias = $"test-{Guid.NewGuid():N}",
                    HandleLease = handleLease ?? TimeSpan.FromMinutes(1),
                    SessionLease = sessionLease ?? TimeSpan.FromMinutes(5),
                    MaximumSessions = maximumSessions
                });
            return new(host, state);
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    public sealed class TestState : TestStateBase
    {
        public int Count { get; set; } = 7;

        public List<int> Numbers { get; } = [1, 2, 3, 4];

        public int PublicWithPrivateSetter { get; private set; } = 3;

        private string Secret { get; set; } = "hidden";

        public string Greet(string name) => $"Hello {name}";

        public async ValueTask<int> DoubleAsync(int value)
        {
            await Task.Yield();
            return value * 2;
        }

        public string ReadHandleId(HandlePayload payload) => payload.HandleId;

        public string Ambiguous(int value) => $"int:{value}";

        public string Ambiguous(long value) => $"long:{value}";

        public void Fail() => throw new InvalidOperationException(
            "boom",
            new ArgumentException("inner"));
    }

    public abstract class TestStateBase
    {
        private string BaseSecret { get; } = "base-hidden";
    }

    public sealed record EchoResult(string? Message);

    public sealed record HandlePayload(string HandleId);
}
