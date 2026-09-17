using System.Text.Json;
using System.Diagnostics;
using System.IO.Pipes;
using Scry.Contracts;
using Scry.Endpoint;
using Scry.Client;

namespace Scry.Tests;

[Collection("Scry integration")]
public sealed class EmbeddedHostTests
{
    [Fact]
    public async Task Runtime_registrations_replace_unregister_and_surface_agent_policy()
    {
        await using var fixture = TestHost.Start();
        await using var client = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath);
        var registry = fixture.Host.Registrations;

        registry.RegisterValue("dynamic", 1, "Initial dynamic value.");
        Assert.Throws<ArgumentException>(() => registry.RegisterValue("dynamic", 2));
        registry.RegisterValue(
            "dynamic",
            2,
            "Replacement dynamic value.",
            RegistrationMode.ReplaceExisting);
        registry.RegisterOperation(
            "state.update",
            arguments => arguments.GetProperty("value").GetInt32(),
            "Updates sample state after explicit approval.",
            new OperationPolicy
            {
                RequiresConfirmation = true
            });
        registry.RegisterOperation(
            "state.read",
            _ => fixture.State.Count,
            "Reads sample state without mutation.",
            new OperationPolicy
            {
                ExecutionPolicy = OperationExecutionPolicy.UiOwner,
                IsReadOnly = true
            });

        var roots = await client.RequestAsync("roots");
        var dynamicRoot = roots.Result!.Value.GetProperty("roots")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "dynamic");
        Assert.Equal("Replacement dynamic value.", dynamicRoot.GetProperty("description").GetString());
        Assert.Equal(2, dynamicRoot.GetProperty("value").GetProperty("value").GetInt32());

        var capabilities = await client.RequestAsync("capabilities");
        var registered = capabilities.Result!.Value.GetProperty("registeredOperations")
            .EnumerateArray()
            .ToDictionary(item => item.GetProperty("name").GetString()!, StringComparer.Ordinal);
        Assert.Equal(
            "Updates sample state after explicit approval.",
            registered["state.update"].GetProperty("description").GetString());
        Assert.Equal(
            "worker-thread",
            registered["state.update"].GetProperty("executionPolicy").GetString());
        Assert.False(registered["state.update"].GetProperty("isReadOnly").GetBoolean());
        Assert.True(registered["state.update"].GetProperty("requiresConfirmation").GetBoolean());
        Assert.Equal("ui-owner", registered["state.read"].GetProperty("executionPolicy").GetString());
        Assert.True(registered["state.read"].GetProperty("isReadOnly").GetBoolean());

        registry.RegisterJobOperation(
            "state.update",
            (arguments, _) => new ValueTask<object?>(arguments.GetProperty("value").GetInt32() * 2),
            "Replacement job-capable updater.",
            mode: RegistrationMode.ReplaceExisting);
        var replaced = await client.RequestAsync("invoke", new
        {
            registeredOperation = "state.update",
            arguments = new { value = 3 }
        });
        Assert.Equal(6, ScalarFrom(replaced));

        Assert.True(registry.UnregisterRoot("dynamic"));
        Assert.False(registry.UnregisterRoot("dynamic"));
        Assert.True(registry.UnregisterOperation("state.update"));
        Assert.False(registry.UnregisterOperation("state.update"));
        Assert.DoesNotContain(
            (await client.RequestAsync("roots")).Result!.Value.GetProperty("roots").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "dynamic");
        var removed = await client.RequestAsync("invoke", new
        {
            registeredOperation = "state.update",
            arguments = new { value = 3 }
        });
        Assert.Equal("operation_not_found", removed.Error?.Code);
    }

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
            Assert.Contains("bounded-value-projection", client.Handshake.Capabilities);

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

#if NETFRAMEWORK
    [Fact]
    public async Task Net48_embedded_host_runs_protocol_reflection_and_roslyn_in_the_default_appdomain()
    {
        await using var fixture = TestHost.Start();
        Assert.Contains(".NET Framework", fixture.Host.Metadata.FrameworkDescription);
        Assert.Equal(Environment.Is64BitProcess ? "x64" : "x86", fixture.Host.Metadata.Architecture);
#if SCRY_EXPECT_X86
        Assert.False(Environment.Is64BitProcess);
#elif SCRY_EXPECT_X64
        Assert.True(Environment.Is64BitProcess);
#endif

        await using var client = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath);
        var roots = await client.RequestAsync("roots");
        var state = RootReference(roots, "state");
        var count = await client.RequestAsync("get", new { reference = state, member = "Count" });
        Assert.Equal(7, ScalarFrom(count));

        var evaluated = await client.EvaluateAsync(new ExecutionRequest(
            "return ((Scry.Tests.EmbeddedHostTests.TestState)Context.GetRoot(\"state\")!).Count * 6;",
            References: new[] { typeof(EmbeddedHostTests).Assembly.GetName().Name! }));
        Assert.Equal(42, evaluated.Value.Value!.Value.GetInt32());

        var isolated = await client.RequestAsync(
            "load-assembly",
            new LoadAssemblyRequest(typeof(ExternalReference).Assembly.Location, "isolated"));
        Assert.Equal("load_policy_not_supported", isolated.Error?.Code);
    }

    [Fact]
    public async Task Net48_pipe_dacl_is_protected_and_grants_only_the_current_user()
    {
        await using var fixture = TestHost.Start();
        var descriptor = await TargetDiscovery.ReadAsync(fixture.Host.DescriptorPath);
        using var pipe = await ConnectPipeAsync(descriptor);
        var security = pipe.GetAccessControl();
        var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().User;
        var world = new System.Security.Principal.SecurityIdentifier(
            System.Security.Principal.WellKnownSidType.WorldSid,
            null);
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.IO.Pipes.PipeAccessRule>()
            .ToArray();

        Assert.True(security.AreAccessRulesProtected);
        Assert.Contains(
            rules,
            rule => rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow &&
                Equals(rule.IdentityReference, currentUser));
        Assert.DoesNotContain(
            rules,
            rule => rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow &&
                Equals(rule.IdentityReference, world));
    }
#endif

    [Fact]
    public async Task Discovery_ignores_and_removes_malformed_descriptors()
    {
        // A private directory rather than the live %LOCALAPPDATA%\Scry\targets, so this test
        // cannot race a real endpoint's own descriptor running on the same machine.
        var directory = Path.Combine(
            Path.GetTempPath(),
            "scry-embedded-host-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        // The bare 32-character-hex shape a real targetId has (RuntimeHost.cs), not a prefixed
        // name: FindAsync only treats a file shaped like a descriptor as a cleanup candidate at all.
        var path = TargetDiscovery.GetDescriptorPath(Guid.NewGuid().ToString("N"), directory);
        File.WriteAllText(path, "{}");

        _ = await TargetDiscovery.FindAsync(directory);
        Assert.False(File.Exists(path));
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

        using (var pipe = await ConnectPipeAsync(descriptor))
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

        using (var pipe = await ConnectPipeAsync(descriptor))
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
        await using var fixture = TestHost.Start(handleLease: TimeSpan.FromSeconds(1));
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
        await Task.Delay(1250);
        var expired = await first.RequestAsync("inspect", new { reference = expiringReference });
        Assert.Equal("handle_not_found", expired.Error?.Code);
    }

    [Fact]
    public async Task Struct_reads_use_bounded_value_projection_without_consuming_handles()
    {
        await using var fixture = TestHost.Start(maximumHandlesPerSession: 1);
        await using var client = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath);
        JsonElement projectedPosition = default;

        for (var index = 0; index < 10; index++)
        {
            var response = await client.RequestAsync("get", new
            {
                root = "state",
                member = "Position"
            });
            Assert.True(response.Success, response.Error?.Message);
            var remoteValue = response.Result!.Value.GetProperty("value");
            Assert.Equal("value", remoteValue.GetProperty("kind").GetString());
            Assert.False(remoteValue.TryGetProperty("reference", out _));

            var projection = remoteValue.GetProperty("value");
            Assert.Equal(12, projection.GetProperty("X").GetInt32());
            Assert.Equal(34, projection.GetProperty("Y").GetInt32());
            Assert.Equal(
                640,
                projection.GetProperty("Size").GetProperty("Width").GetInt32());
            Assert.Equal(
                typeof(StructSize).FullName,
                projection.GetProperty("Size").GetProperty("$type").GetString());
            Assert.True(
                projection.GetProperty("Owner").GetProperty("$reference").GetBoolean());
            Assert.Equal(
                1024,
                projection
                    .GetProperty("Description")
                    .GetProperty("$value")
                    .GetString()!
                    .Length);
            Assert.Equal(
                "string",
                projection
                    .GetProperty("Description")
                    .GetProperty("$truncated")
                    .GetString());
            projectedPosition = projection.Clone();
        }

        var incompleteArgument = await client.RequestAsync("invoke", new
        {
            root = "state",
            member = "AcceptPosition",
            arguments = new[] { projectedPosition }
        });
        Assert.Equal("incomplete_value_projection", incompleteArgument.Error?.Code);

        var boxed = await client.RequestAsync("get", new
        {
            root = "state",
            member = "Position",
            asReference = true
        });
        var boxedReference = ReferenceFrom(boxed.Result!.Value.GetProperty("value"));
        var inspected = await client.RequestAsync("inspect", new { reference = boxedReference });
        Assert.True(inspected.Success, inspected.Error?.Message);
        Assert.Contains(
            inspected.Result!.Value.GetProperty("members").EnumerateArray(),
            member => member.GetProperty("name").GetString() == "Size");
        var released = await client.RequestAsync("release", new
        {
            handleId = boxedReference.HandleId
        });
        Assert.Equal(1, released.Result!.Value.GetProperty("released").GetInt32());

        var child = await client.RequestAsync("get", new
        {
            root = "state",
            member = "Child"
        });
        Assert.True(child.Success, child.Error?.Message);
        Assert.Equal(
            "reference",
            child.Result!.Value.GetProperty("value").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Struct_projection_is_bounded_and_reports_member_failures()
    {
        await using var fixture = TestHost.Start();
        await using var client = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath);

        var fragile = await client.RequestAsync("get", new
        {
            root = "state",
            member = "Fragile"
        });
        Assert.True(fragile.Success, fragile.Error?.Message);
        var fragileProjection = fragile.Result!.Value
            .GetProperty("value")
            .GetProperty("value");
        Assert.Equal(7, fragileProjection.GetProperty("Safe").GetInt32());
        Assert.Equal(
            typeof(InvalidOperationException).FullName,
            fragileProjection.GetProperty("Explodes").GetProperty("$error").GetString());

        var deep = await client.RequestAsync("get", new
        {
            root = "state",
            member = "Deep"
        });
        Assert.True(deep.Success, deep.Error?.Message);
        var deepProjection = deep.Result!.Value
            .GetProperty("value")
            .GetProperty("value");
        Assert.Equal(
            "depth",
            deepProjection
                .GetProperty("Next")
                .GetProperty("Next")
                .GetProperty("Next")
                .GetProperty("Next")
                .GetProperty("$truncated")
                .GetString());

        var fields = await client.RequestAsync("get", new
        {
            root = "state",
            member = "Fields"
        });
        var fieldProjection = fields.Result!.Value
            .GetProperty("value")
            .GetProperty("value")
            .Clone();
        Assert.Equal(23, fieldProjection.GetProperty("Value").GetInt32());
        var roundTrip = await client.RequestAsync("invoke", new
        {
            root = "state",
            member = "ReadFields",
            arguments = new[] { fieldProjection }
        });
        Assert.Equal(23, ScalarFrom(roundTrip));
    }

    [Fact]
    public async Task Struct_collection_projection_respects_the_frame_budget()
    {
        await using var fixture = TestHost.Start();
        await using var client = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath);
        var roots = await client.RequestAsync("roots");
        var wideStructs = roots.Result!.Value.GetProperty("roots")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "wideStructs");
        var reference = ReferenceFrom(wideStructs.GetProperty("value"));

        var page = await client.RequestAsync("enumerate", new
        {
            reference,
            limit = 1000
        });

        Assert.True(page.Success, page.Error?.Message);
        Assert.True(page.Result!.Value.GetProperty("hasMore").GetBoolean());
        var returned = page.Result.Value.GetProperty("returned").GetInt32();
        Assert.InRange(returned, 1, 999);
        Assert.All(
            page.Result.Value.GetProperty("items").EnumerateArray(),
            item => Assert.Equal("value", item.GetProperty("kind").GetString()));
        Assert.True(
            JsonSerializer.SerializeToUtf8Bytes(page, ScryJson.Options).Length <
            ProtocolConstants.MaximumFrameBytes);
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
    public async Task Csharp_execution_evaluates_executes_awaits_logs_and_projects_values()
    {
        await using var fixture = TestHost.Start();
        await using var client = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath);

        var evaluated = await client.EvaluateAsync(new(
            """
            Context.Log("starting");
            await Task.Delay(1, Context.CancellationToken);
            return ((Scry.Tests.EmbeddedHostTests.TestState)Context.GetRoot("state")!).Count * 2;
            """,
            References: [typeof(EmbeddedHostTests).Assembly.GetName().Name!]));

        Assert.Equal(14, evaluated.Value.Value!.Value.GetInt32());
        Assert.Single(evaluated.Logs);
        Assert.Equal("starting", evaluated.Logs[0].Message);
        Assert.Equal(0, evaluated.DroppedLogEntries);

        var projectedStruct = await client.EvaluateAsync(new(
            """
            return ((Scry.Tests.EmbeddedHostTests.TestState)Context.GetRoot("state")!).Position;
            """,
            References: [typeof(EmbeddedHostTests).Assembly.GetName().Name!]));
        Assert.Equal("value", projectedStruct.Value.Kind);
        Assert.Equal(
            640,
            projectedStruct.Value.Value!.Value
                .GetProperty("Size")
                .GetProperty("Width")
                .GetInt32());
        Assert.Null(projectedStruct.Value.Reference);

        var executed = await client.ExecuteAsync(new(
            """
            var state = (Scry.Tests.EmbeddedHostTests.TestState)Context.GetRoot("state")!;
            await Task.Yield();
            state.Count = 19;
            return state;
            """));

        Assert.Equal(19, fixture.State.Count);
        Assert.Equal("reference", executed.Value.Kind);
        Assert.Equal(typeof(TestState).FullName, executed.Value.Type);
    }

    [Fact]
    public async Task Csharp_execution_reports_diagnostics_exceptions_limits_and_cooperative_timeout()
    {
        await using var fixture = TestHost.Start(
            maximumSourceLength: 64,
            maximumLogEntries: 1);
        await using var client = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath);

        var compilation = await client.RequestAsync(
            "evaluate",
            new ExecutionRequest("return 1 +;"));
        Assert.Equal("compilation_failed", compilation.Error?.Code);
        var diagnostic = Assert.Single(compilation.Error?.Diagnostics ?? []);
        Assert.Equal("Error", diagnostic.Severity);
        Assert.NotNull(diagnostic.StartLine);
        Assert.NotNull(diagnostic.StartColumn);

        var failure = await client.RequestAsync(
            "evaluate",
            new ExecutionRequest("throw new InvalidOperationException(\"script boom\");"));
        Assert.Equal("operation_failed", failure.Error?.Code);
        Assert.Equal(typeof(InvalidOperationException).FullName, failure.Error?.Exception?.Type);
        Assert.Equal("script boom", failure.Error?.Exception?.Message);

        var timedOut = await client.RequestAsync(
            "evaluate",
            new ExecutionRequest(
                "await Task.Delay(5000, Context.CancellationToken); return 1;",
                TimeoutMilliseconds: 25));
        Assert.Equal("execution_timed_out", timedOut.Error?.Code);
        Assert.Equal("cooperative", timedOut.Error?.Data?["cancellation"]);

        var oversized = await client.RequestAsync(
            "evaluate",
            new ExecutionRequest(new string('x', 65)));
        Assert.Equal("request_limit_exceeded", oversized.Error?.Code);

        var logged = await client.EvaluateAsync(new(
            "Context.Log(\"one\"); Context.Log(\"two\"); return 1;"));
        Assert.Single(logged.Logs);
        Assert.Equal(1, logged.DroppedLogEntries);
    }

    [Fact]
    public async Task Assembly_operations_list_load_find_and_describe_target_types()
    {
        await using var fixture = TestHost.Start();
        await using var client = await ScryClient.ConnectAsync(fixture.Host.DescriptorPath);

        var assemblies = await client.ListAssembliesAsync();
        Assert.Contains(
            assemblies.Assemblies,
            assembly => assembly.Name == typeof(EmbeddedHostTests).Assembly.GetName().Name);

        var types = await client.FindTypesAsync(new(
            Query: nameof(TestState),
            Assembly: typeof(EmbeddedHostTests).Assembly.GetName().Name));
        Assert.Contains(types.Types, type => type.FullName == typeof(TestState).FullName);

        var description = await client.DescribeTypeAsync(new(
            typeof(TestState).FullName!,
            typeof(EmbeddedHostTests).Assembly.GetName().Name));
        Assert.Equal(typeof(TestState).FullName, description.Type.FullName);
#if NETFRAMEWORK
        Assert.Equal("DefaultAppDomain", description.Type.LoadContext);
#else
        Assert.Equal("Default", description.Type.LoadContext);
#endif
        Assert.Contains(description.Members, member => member.Name == nameof(TestState.Count));

        var defaultLoaded = await client.LoadAssemblyAsync(new(
            typeof(ExternalReference).Assembly.Location,
            "default"));
        Assert.True(defaultLoaded.Assembly.IsDefaultLoadContext);
        Assert.False(defaultLoaded.Assembly.IsCollectible);

#if NETFRAMEWORK
        var isolated = await client.RequestAsync(
            "load-assembly",
            new LoadAssemblyRequest(typeof(ExternalReference).Assembly.Location, "isolated"));
        Assert.Equal("load_policy_not_supported", isolated.Error?.Code);

        // loadContext targets a modern-.NET-only concept (AssemblyLoadContext); .NET Framework has
        // no load contexts at all, so it must fail clearly rather than being silently ignored.
        var loadContextUnsupported = await client.RequestAsync(
            "evaluate",
            new ExecutionRequest("return 1;", LoadContext: "Whatever"));
        Assert.Equal("load_context_not_supported", loadContextUnsupported.Error?.Code);
#else
        var cliAssemblyPath = Directory.EnumerateFiles(
                Path.Combine(FindRepositoryRoot(), "src", "Scry.Cli", "bin"),
                "scry.dll",
                SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
        var loaded = await client.LoadAssemblyAsync(new(cliAssemblyPath, "isolated"));
        Assert.True(loaded.Assembly.IsCollectible);
        Assert.False(loaded.Assembly.IsDefaultLoadContext);
        Assert.StartsWith("Scry.Isolated.", loaded.Assembly.LoadContext, StringComparison.Ordinal);

        var incompatibleReference = await client.RequestAsync(
            "evaluate",
            new ExecutionRequest(
                "return 1;",
                References: [loaded.Assembly.FullName]));
        Assert.Equal("assembly_not_compatible", incompatibleReference.Error?.Code);

        // A bogus loadContext must fail clearly rather than silently falling back to the default set.
        var loadContextNotFound = await client.RequestAsync(
            "evaluate",
            new ExecutionRequest("return 1;", LoadContext: "Scry.Isolated.does-not-exist"));
        Assert.Equal("load_context_not_found", loadContextNotFound.Error?.Code);

        // Naming the isolated context in loadContext widens execution to reach it - the same
        // reference that was rejected above now compiles and runs.
        var widenedReference = await client.RequestAsync(
            "evaluate",
            new ExecutionRequest(
                "return 1;",
                References: [loaded.Assembly.FullName],
                LoadContext: loaded.Assembly.LoadContext));
        Assert.True(widenedReference.Success, widenedReference.Error?.Message);
        Assert.Equal(1, widenedReference.Result!.Value.GetProperty("value").GetProperty("value").GetInt32());
#endif
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

#if !NETFRAMEWORK
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

    [Fact]
    public async Task Cli_evaluates_source_from_a_file()
    {
        await using var fixture = TestHost.Start();
        var root = FindRepositoryRoot();
        var sourcePath = Path.Combine(Path.GetTempPath(), $"scry-{Guid.NewGuid():N}.csx");
        await File.WriteAllTextAsync(sourcePath, "return 6 * 7;");
        try
        {
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
            startInfo.ArgumentList.Add("evaluate");
            startInfo.ArgumentList.Add("--descriptor");
            startInfo.ArgumentList.Add(fixture.Host.DescriptorPath);
            startInfo.ArgumentList.Add("--source");
            startInfo.ArgumentList.Add(sourcePath);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the scry CLI.");
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.True(process.ExitCode == 0, error);
            using var document = JsonDocument.Parse(output);
            Assert.Equal(
                42,
                document.RootElement.GetProperty("result")
                    .GetProperty("value")
                    .GetProperty("value")
                    .GetInt32());
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }
#endif

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
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        return pipe;
    }

    private sealed class TestHost : IAsyncDisposable
    {
        private TestHost(EndpointHost host, TestState state)
        {
            Host = host;
            State = state;
        }

        public EndpointHost Host { get; }

        public TestState State { get; }

        public static TestHost Start(
            TimeSpan? handleLease = null,
            TimeSpan? sessionLease = null,
            int maximumSessions = 256,
            int maximumHandlesPerSession = 4096,
            int maximumSourceLength = 256 * 1024,
            int maximumLogEntries = 256)
        {
            var state = new TestState();
            var host = EndpointHost.Start(
                builder => builder
                    .RegisterValue("state", state)
                    .RegisterValue("numbers", state.Numbers)
                    .RegisterValue("wideStructs", state.WideStructs)
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
                new EndpointOptions
                {
                    Alias = $"test-{Guid.NewGuid():N}",
                    HandleLease = handleLease ?? TimeSpan.FromMinutes(1),
                    SessionLease = sessionLease ?? TimeSpan.FromMinutes(5),
                    MaximumSessions = maximumSessions,
                    MaximumHandlesPerSession = maximumHandlesPerSession,
                    MaximumSourceLength = maximumSourceLength,
                    MaximumLogEntries = maximumLogEntries
                });
            return new(host, state);
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    public sealed class TestState : TestStateBase
    {
        public TestState()
        {
            var text = new string('w', 2048);
            WideStructs = Enumerable.Repeat(
                    new WideStruct(text, text, text, text, text, text, text, text),
                    1000)
                .ToList();
        }

        public int Count { get; set; } = 7;

        public List<int> Numbers { get; } = [1, 2, 3, 4];

        public List<WideStruct> WideStructs { get; }

        public int PublicWithPrivateSetter { get; private set; } = 3;

        public StructPosition Position { get; } =
            new(
                12,
                34,
                new StructSize(640, 480),
                new ChildState(),
                new string('p', 2048));

        public FragileStruct Fragile => new();

        public FieldStruct Fields => new() { Value = 23 };

        public DeepLevel1 Deep => new(
            new DeepLevel2(
                new DeepLevel3(
                    new DeepLevel4(
                        new DeepLevel5(5)))));

        public ChildState Child { get; } = new();

        private string Secret { get; set; } = "hidden";

        public string Greet(string name) => $"Hello {name}";

        public async ValueTask<int> DoubleAsync(int value)
        {
            await Task.Yield();
            return value * 2;
        }

        public string ReadHandleId(HandlePayload payload) => payload.HandleId;

        public int AcceptPosition(StructPosition position) => position.X;

        public int ReadFields(FieldStruct value) => value.Value;

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

    public sealed class ChildState
    {
    }

    public readonly record struct StructPosition(
        int X,
        int Y,
        StructSize Size,
        ChildState Owner,
        string Description);

    public readonly record struct StructSize(int Width, int Height);

    public readonly struct FragileStruct
    {
        public int Explodes => throw new InvalidOperationException("Projection failure.");

        public int Safe => 7;
    }

    public readonly record struct DeepLevel1(DeepLevel2 Next);

    public readonly record struct DeepLevel2(DeepLevel3 Next);

    public readonly record struct DeepLevel3(DeepLevel4 Next);

    public readonly record struct DeepLevel4(DeepLevel5 Next);

    public readonly record struct DeepLevel5(int Value);

    public readonly record struct WideStruct(
        string A,
        string B,
        string C,
        string D,
        string E,
        string F,
        string G,
        string H);

    public struct FieldStruct
    {
        public int Value;
    }
}
