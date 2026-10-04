using System.Reflection;
using System.Runtime.CompilerServices;
using Scry.Client;
using Scry.Contracts;
using Scry.Endpoint;
using Scry.Runtime;

namespace Scry.Tests;

/// <summary>
/// Targets for method hooks. Public so a predicate can name the type, and every method carries
/// <see cref="MethodImplOptions.NoInlining"/> so a Release build cannot inline the call being hooked
/// away - the hook itself is what is under test, not the JIT.
/// </summary>
public sealed class HookTargets
{
    private int _calls;

    public int Calls => _calls;

    public bool RunPush(params string[] ids) => PushGroups(ids);

    public string RunDescribe(int value) => Describe(value);

    public string RunDescribe(string value) => Describe(value);

    public void RunExplode(string message) => Explode(message);

    public void RunTouch() => Touch();

    public int RunTinyGetter() => TinyValue;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool PushGroups(string[] ids)
    {
        _calls++;
        return ids.Length > 0 && ids[0] != "bad";
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private string Describe(int value) => "int:" + value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private string Describe(string value) => "string:" + value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Add(int left, int right) => left + right;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Explode(string message) => throw new InvalidOperationException(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Touch() => _calls++;

    private int TinyValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => 42;
    }
}

[Collection("Scry integration")]
public sealed class HookOperationTests
{
    private const string TargetType = "Scry.Tests.HookTargets";

    private static MethodBase Method(string name, params Type[] parameters) =>
        typeof(HookTargets).GetMethod(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
            null,
            parameters,
            null)!;

    private static bool IsPatched(MethodBase method) =>
        HarmonyLib.Harmony.GetPatchInfo(method) is { Owners.Count: > 0 };

    [Fact]
    public async Task Records_a_private_instance_call_made_through_this()
    {
        await using var context = await HookHost.StartAsync();
        var targets = new HookTargets();
        await using var hook = await context.Client.AddHookAsync(new HookAddRequest(TargetType, "PushGroups"));

        Assert.True(targets.RunPush("1", "2"));
        Assert.False(targets.RunPush("bad"));

        // Taken before the next await, which may resume this test on a different thread.
        var callingThread = Environment.CurrentManagedThreadId;
        var read = await hook.ReadAsync();
        Assert.Equal(2, read.Calls.Count);
        Assert.Equal(new long[] { 0, 1 }, read.Calls.Select(call => call.Sequence));
        Assert.All(read.Calls, call => Assert.Equal(HookOutcomes.Returned, call.Outcome));
        Assert.True(read.Calls[0].GetReturnValue<bool>());
        Assert.False(read.Calls[1].GetReturnValue<bool>());
        Assert.Equal(callingThread, read.Calls[0].ThreadId);

        // a string[] is a reference type: reported as its type, not as a handle
        var argument = Assert.Single(read.Calls[0].Arguments!);
        Assert.Equal("preview", argument.Kind);
        Assert.Equal("System.String[]", argument.Type);
        Assert.Equal(2, read.TotalCalls);
        Assert.Equal(0, read.DroppedCalls);
    }

    [Fact]
    public async Task Selects_an_overload_by_parameter_types_and_rejects_a_bare_name()
    {
        await using var context = await HookHost.StartAsync();
        var targets = new HookTargets();

        var ambiguous = await context.Client.RequestAsync("hook.add", new HookAddRequest(TargetType, "Describe"));
        Assert.Equal("ambiguous_member", ambiguous.Error?.Code);
        Assert.Contains("Describe(", ambiguous.Error!.Message);

        await using var hook = await context.Client.AddHookAsync(
            new HookAddRequest(TargetType, "Describe", ParameterTypes: new[] { "string" }));
        targets.RunDescribe(5);
        targets.RunDescribe("x");

        var read = await hook.ReadAsync();
        var call = Assert.Single(read.Calls);
        Assert.Equal("string:x", call.ReturnValue!.Value!.Value.GetString());
        Assert.Equal("x", Assert.Single(call.Arguments!).Value!.Value.GetString());
    }

    [Fact]
    public async Task Records_a_static_method()
    {
        await using var context = await HookHost.StartAsync();
        await using var hook = await context.Client.AddHookAsync(new HookAddRequest(TargetType, "Add"));

        Assert.Equal(5, HookTargets.Add(2, 3));

        var call = Assert.Single((await hook.ReadAsync()).Calls);
        Assert.Equal(new[] { 2, 3 }, call.Arguments!.Select(argument => argument.Value!.Value.GetInt32()));
        Assert.Equal(5, call.GetReturnValue<int>());
    }

    [Fact]
    public async Task Records_a_throwing_method_and_the_caller_still_sees_the_same_exception()
    {
        await using var context = await HookHost.StartAsync();
        var targets = new HookTargets();
        await using var hook = await context.Client.AddHookAsync(new HookAddRequest(TargetType, "Explode"));

        var thrown = Assert.Throws<InvalidOperationException>(() => targets.RunExplode("boom"));
        Assert.Equal("boom", thrown.Message);

        var call = Assert.Single((await hook.ReadAsync()).Calls);
        Assert.Equal(HookOutcomes.Threw, call.Outcome);
        Assert.Null(call.ReturnValue);
        Assert.Equal("System.InvalidOperationException", call.Exception!.Type);
        Assert.Equal("boom", call.Exception.Message);
        Assert.Throws<InvalidOperationException>(() => call.GetReturnValue<bool>());
    }

    [Fact]
    public async Task Records_a_void_method_without_a_return_value()
    {
        await using var context = await HookHost.StartAsync();
        var targets = new HookTargets();
        await using var hook = await context.Client.AddHookAsync(new HookAddRequest(TargetType, "Touch"));

        targets.RunTouch();

        var call = Assert.Single((await hook.ReadAsync()).Calls);
        Assert.Equal(HookOutcomes.Returned, call.Outcome);
        Assert.Null(call.ReturnValue);
        Assert.Equal(1, targets.Calls);
    }

    [Fact]
    public async Task Capture_options_control_what_is_recorded_and_the_instance_is_a_handle_on_request()
    {
        await using var context = await HookHost.StartAsync();
        var targets = new HookTargets();
        await using var hook = await context.Client.AddHookAsync(new HookAddRequest(
            TargetType,
            "PushGroups",
            CaptureArguments: false,
            CaptureReturnValue: false,
            CaptureInstance: true));

        targets.RunPush("1");

        var plain = Assert.Single((await hook.ReadAsync()).Calls);
        Assert.Null(plain.Arguments);
        Assert.Null(plain.ReturnValue);
        Assert.Equal("preview", plain.Instance!.Kind);

        var withHandle = Assert.Single((await hook.ReadAsync(includeReferences: true)).Calls);
        Assert.Equal("reference", withHandle.Instance!.Kind);
        Assert.NotNull(withHandle.Instance.Reference);
    }

    [Fact]
    public async Task Adding_the_same_method_twice_in_a_session_returns_the_existing_hook()
    {
        await using var context = await HookHost.StartAsync();
        await using var first = await context.Client.AddHookAsync(new HookAddRequest(TargetType, "PushGroups"));
        await using var second = await context.Client.AddHookAsync(new HookAddRequest(TargetType, "PushGroups"));

        Assert.True(first.Info.Created);
        Assert.False(second.Info.Created);
        Assert.Equal(first.Handle, second.Handle);
        Assert.Single((await context.Client.ListHooksAsync()).Hooks);
    }

    [Fact]
    public async Task A_full_buffer_drops_the_oldest_calls_and_reports_it()
    {
        await using var context = await HookHost.StartAsync();
        var targets = new HookTargets();
        await using var hook = await context.Client.AddHookAsync(
            new HookAddRequest(TargetType, "PushGroups", Capacity: 3));

        for (var index = 0; index < 5; index++)
        {
            targets.RunPush(index.ToString());
        }

        var read = await hook.ReadAsync();
        Assert.Equal(new long[] { 2, 3, 4 }, read.Calls.Select(call => call.Sequence));
        Assert.True(read.Truncated);
        Assert.Equal(5, read.TotalCalls);
        Assert.Equal(2, read.DroppedCalls);
        Assert.Equal(2, read.OldestCursor);
    }

    [Fact]
    public async Task Drain_returns_the_calls_and_empties_the_buffer()
    {
        await using var context = await HookHost.StartAsync();
        var targets = new HookTargets();
        await using var hook = await context.Client.AddHookAsync(new HookAddRequest(TargetType, "PushGroups"));
        targets.RunPush("1");
        targets.RunPush("2");

        var drained = await hook.DrainAsync();
        Assert.Equal(2, drained.Calls.Count);

        var after = await hook.ReadAsync();
        Assert.Empty(after.Calls);
        Assert.Equal(2, after.NextCursor);
        targets.RunPush("3");
        Assert.Equal(2, Assert.Single((await hook.ReadAsync(after.NextCursor)).Calls).Sequence);
    }

    [Fact]
    public async Task Wait_returns_the_first_call_the_predicate_matches_against_the_real_arguments()
    {
        await using var context = await HookHost.StartAsync();
        var targets = new HookTargets();
        await using var hook = await context.Client.AddHookAsync(new HookAddRequest(TargetType, "PushGroups"));

        targets.RunPush("1");
        var waiting = hook.WaitForCallAsync("((string[])Args[0]).Contains(\"9\") && (bool)ReturnValue", TimeSpan.FromSeconds(20));
        await Task.Delay(100);
        targets.RunPush("2");
        targets.RunPush("9", "10");

        var call = await waiting;
        Assert.Equal(2, call.Sequence);
        Assert.True(call.GetReturnValue<bool>());

        var result = await hook.TryWaitForCallAsync("Call.Sequence == 0", TimeSpan.FromSeconds(5));
        Assert.True(result.Satisfied);
        Assert.Equal(0, result.Call!.Sequence);
        Assert.Equal(1, result.NextCursor);
    }

    [Fact]
    public async Task Wait_sees_the_exception_and_instance_of_a_call()
    {
        await using var context = await HookHost.StartAsync();
        var targets = new HookTargets();
        await using var hook = await context.Client.AddHookAsync(
            new HookAddRequest(TargetType, "Explode", CaptureInstance: true));

        Assert.Throws<InvalidOperationException>(() => targets.RunExplode("late"));

        var call = await hook.WaitForCallAsync(
            "Exception != null && Exception.Message == \"late\" && Instance is Scry.Tests.HookTargets && Call.Threw",
            TimeSpan.FromSeconds(10));
        Assert.Equal(HookOutcomes.Threw, call.Outcome);
    }

    [Fact]
    public async Task Wait_reports_a_timeout_without_throwing_and_explains_a_hook_that_never_fired()
    {
        await using var context = await HookHost.StartAsync();
        await using var hook = await context.Client.AddHookAsync(new HookAddRequest(TargetType, "PushGroups"));

        var result = await hook.TryWaitForCallAsync("true", TimeSpan.FromMilliseconds(300));

        Assert.False(result.Satisfied);
        Assert.True(result.TimedOut);
        Assert.Null(result.Call);
        Assert.Contains(result.Diagnostics, line => line.Contains("No call has been recorded"));
        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => hook.WaitForCallAsync("true", TimeSpan.FromMilliseconds(200)));
        Assert.Contains("No call has been recorded", thrown.Message);
    }

    [Fact]
    public async Task Wait_rejects_a_bad_predicate_immediately_and_distinctly()
    {
        await using var context = await HookHost.StartAsync();
        var targets = new HookTargets();
        await using var hook = await context.Client.AddHookAsync(new HookAddRequest(TargetType, "PushGroups"));

        var broken = await context.Client.RequestAsync(
            "hook.wait",
            new HookWaitRequest(hook.Handle, "this is not csharp", TimeoutMilliseconds: 60_000));
        Assert.Equal("compilation_failed", broken.Error?.Code);

        targets.RunPush("1");
        var notBool = await context.Client.RequestAsync(
            "hook.wait",
            new HookWaitRequest(hook.Handle, "42", TimeoutMilliseconds: 60_000));
        Assert.Equal("invalid_predicate", notBool.Error?.Code);

        var throwing = await context.Client.RequestAsync(
            "hook.wait",
            new HookWaitRequest(hook.Handle, "((string[])Args[5]).Length > 0", TimeoutMilliseconds: 60_000));
        Assert.Equal("predicate_failed", throwing.Error?.Code);
        Assert.Equal("0", throwing.Error!.Data!["sequence"]);
    }

    [Fact]
    public async Task A_hook_belongs_to_its_session()
    {
        await using var context = await HookHost.StartAsync();
        await using var other = await ScryClient.ConnectAsync(context.Host.DescriptorPath);
        await using var hook = await context.Client.AddHookAsync(new HookAddRequest(TargetType, "PushGroups"));

        var foreign = await other.RequestAsync("hook.read", new HookReadRequest(hook.Handle));
        Assert.Equal("hook_scope_mismatch", foreign.Error?.Code);
        Assert.Empty((await other.ListHooksAsync()).Hooks);

        var missing = await context.Client.RequestAsync(
            "hook.read",
            new HookReadRequest(hook.Handle with { HookId = "nope" }));
        Assert.Equal("hook_not_found", missing.Error?.Code);
    }

    [Fact]
    public async Task Unknown_or_unpatchable_methods_are_rejected_with_specific_codes()
    {
        await using var context = await HookHost.StartAsync();

        Assert.Equal(
            "type_not_found",
            (await context.Client.RequestAsync("hook.add", new HookAddRequest("No.Such.Type", "X"))).Error?.Code);
        Assert.Equal(
            "member_not_found",
            (await context.Client.RequestAsync("hook.add", new HookAddRequest(TargetType, "Nope"))).Error?.Code);
        Assert.Equal(
            "invalid_request",
            (await context.Client.RequestAsync(
                "hook.add",
                new HookAddRequest(TargetType, "Add", Capacity: 0))).Error?.Code);
        Assert.Equal(
            "method_not_patchable",
            (await context.Client.RequestAsync(
                "hook.add",
                new HookAddRequest("System.Collections.Generic.List`1", "Add"))).Error?.Code);
    }

    [Fact]
    public async Task Removing_a_hook_restores_the_original_method()
    {
        await using var context = await HookHost.StartAsync();
        var targets = new HookTargets();
        var method = Method("PushGroups", typeof(string[]));
        var hook = await context.Client.AddHookAsync(new HookAddRequest(TargetType, "PushGroups"));
        Assert.True(IsPatched(method));
        Assert.True(targets.RunPush("1"));

        await hook.RemoveAsync();

        Assert.False(IsPatched(method));
        Assert.True(targets.RunPush("2"));
        Assert.False(targets.RunPush("bad"));
        Assert.Equal(3, targets.Calls);
        var gone = await context.Client.RequestAsync("hook.read", new HookReadRequest(hook.Handle));
        Assert.Equal("hook_not_found", gone.Error?.Code);
        await hook.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_the_handle_unpatches_and_two_hooks_on_one_method_share_one_patch()
    {
        await using var context = await HookHost.StartAsync();
        await using var secondClient = await ScryClient.ConnectAsync(context.Host.DescriptorPath);
        var targets = new HookTargets();
        var method = Method("PushGroups", typeof(string[]));
        var first = await context.Client.AddHookAsync(new HookAddRequest(TargetType, "PushGroups"));
        var second = await secondClient.AddHookAsync(new HookAddRequest(TargetType, "PushGroups"));

        targets.RunPush("1");
        Assert.Single((await first.ReadAsync()).Calls);
        Assert.Single((await second.ReadAsync()).Calls);

        await first.DisposeAsync();
        Assert.True(IsPatched(method));
        targets.RunPush("2");
        Assert.Equal(2, (await second.ReadAsync()).Calls.Count);

        await second.DisposeAsync();
        Assert.False(IsPatched(method));
    }

    [Fact]
    public async Task Ending_an_ephemeral_session_unpatches_what_it_hooked()
    {
        using var host = EndpointHost.Start(builder => builder.RegisterValue("targets", new HookTargets()));
        var method = Method("PushGroups", typeof(string[]));
        var client = await ScryClient.ConnectAsync(host.DescriptorPath, ephemeralSession: true);
        await client.AddHookAsync(new HookAddRequest(TargetType, "PushGroups"));
        Assert.True(IsPatched(method));

        await client.DisposeAsync();

        await WaitUntilAsync(() => !IsPatched(method));
        Assert.False(IsPatched(method));
    }

    [Fact]
    public async Task Disposing_the_endpoint_unpatches_every_hook()
    {
        var host = EndpointHost.Start(builder => builder.RegisterValue("targets", new HookTargets()));
        var method = Method("PushGroups", typeof(string[]));
        await using (var client = await ScryClient.ConnectAsync(host.DescriptorPath))
        {
            await client.AddHookAsync(new HookAddRequest(TargetType, "PushGroups"));
            Assert.True(IsPatched(method));
        }

        host.Dispose();

        Assert.False(IsPatched(method));
    }

    [Fact]
    public async Task A_wait_longer_than_the_client_request_timeout_is_not_cut_short()
    {
        await using var context = await HookHost.StartAsync();
        var targets = new HookTargets();
        context.Client.RequestTimeout = TimeSpan.FromSeconds(2);
        await using var hook = await context.Client.AddHookAsync(new HookAddRequest(TargetType, "PushGroups"));
        _ = Task.Run(async () =>
        {
            await Task.Delay(4_000);
            targets.RunPush("late");
        });

        var result = await hook.TryWaitForCallAsync("true", TimeSpan.FromSeconds(10));

        Assert.True(result.Satisfied);
    }

    [Fact]
    public async Task Inlining_risk_is_reported_and_a_high_risk_hook_carries_a_warning()
    {
        await using var context = await HookHost.StartAsync();

        await using var safe = await context.Client.AddHookAsync(new HookAddRequest(TargetType, "PushGroups"));
        Assert.Equal(HookInliningRisks.None, safe.Inlining.Risk);
        Assert.DoesNotContain(safe.Warnings, warning => warning.StartsWith("inlining_risk", StringComparison.Ordinal));

        await using var risky = await context.Client.AddHookAsync(new HookAddRequest(TargetType, "get_TinyValue"));
        Assert.Equal(HookInliningRisks.High, risky.Inlining.Risk);
        Assert.Contains(risky.Warnings, warning => warning.StartsWith("inlining_risk_high", StringComparison.Ordinal));

        var summaries = (await context.Client.ListHooksAsync()).Hooks;
        Assert.Contains(summaries, summary => summary.Inlining.Risk == HookInliningRisks.High);
    }

    [Fact]
    public async Task Reports_the_harmony_this_runtime_resolved_and_refuses_an_older_one()
    {
        await using var context = await HookHost.StartAsync();
        await using var hook = await context.Client.AddHookAsync(new HookAddRequest(TargetType, "PushGroups"));

        Assert.StartsWith("2.4.2", hook.Info.Harmony.Version, StringComparison.Ordinal);
        Assert.Empty(hook.Info.Harmony.ForeignCopies);

        var older = new HookHarmonyInfo("2.2.0.0", @"C:\app\0Harmony.dll", Array.Empty<HookForeignHarmony>());
        var refused = Assert.Throws<ScryOperationException>(() => HarmonyEnvironment.ThrowIfUnusable(older));
        Assert.Equal("hooks_unavailable", refused.Code);
        Assert.Equal(@"C:\app\0Harmony.dll", refused.ErrorData!["location"]);
    }

    /// <summary>
    /// Lets a run that is supposed to be 32-bit (or 64-bit) prove it was: <c>validate.ps1</c> sets
    /// <c>SCRY_EXPECT_BITNESS</c> for its .NET 8 x86 hook step, where nothing else would notice the
    /// test host silently falling back to x64. Does nothing when the variable is unset.
    /// </summary>
    [Fact]
    public void The_process_has_the_bitness_the_run_was_asked_for()
    {
        var expected = Environment.GetEnvironmentVariable("SCRY_EXPECT_BITNESS");
        if (expected is null)
        {
            return;
        }

        Assert.Equal(expected == "x86", !Environment.Is64BitProcess);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(50);
        }
    }

    private sealed class HookHost : IAsyncDisposable
    {
        private HookHost(EndpointHost host, ScryClient client)
        {
            Host = host;
            Client = client;
        }

        public EndpointHost Host { get; }

        public ScryClient Client { get; }

        public static async Task<HookHost> StartAsync()
        {
            var host = EndpointHost.Start(builder => builder.RegisterValue("targets", new HookTargets()));
            return new(host, await ScryClient.ConnectAsync(host.DescriptorPath));
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            Host.Dispose();
        }
    }
}
