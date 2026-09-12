using System.Text.Json;
using Scry.Contracts;
using Scry.Endpoint;
using Scry.Client;

namespace Scry.Tests;

/// <summary>
/// Covers reuse of compiled submissions. Compiling a submission builds a metadata reference for
/// every loaded assembly and then runs Roslyn codegen, which measured seconds per attempt in a real
/// application - so <c>wait</c>, which re-evaluates one expression until it holds, was paying that
/// cost on every poll.
/// <para>
/// Asserted through the <c>compilationCached</c> flag rather than by timing, so the tests state the
/// actual contract and cannot flake on a slow machine.
/// </para>
/// </summary>
[Collection("Scry integration")]
public sealed class ScriptCacheTests
{
    [Fact]
    public async Task Repeating_a_submission_reuses_the_compiled_script()
    {
        await using var context = await CacheHost.StartAsync();

        var first = await context.EvaluateAsync(new ExecutionRequest("1 + 1"));
        var second = await context.EvaluateAsync(new ExecutionRequest("1 + 1"));

        Assert.False(CompilationCached(first), "The first compile cannot be a cache hit.");
        Assert.True(CompilationCached(second), "The second identical submission should reuse it.");
        Assert.Equal(2, Value(first).GetInt32());
        Assert.Equal(2, Value(second).GetInt32());
    }

    [Fact]
    public async Task Distinct_sources_do_not_share_an_entry()
    {
        await using var context = await CacheHost.StartAsync();

        await context.EvaluateAsync(new ExecutionRequest("1 + 1"));
        var other = await context.EvaluateAsync(new ExecutionRequest("2 + 2"));

        Assert.False(CompilationCached(other));
        Assert.Equal(4, Value(other).GetInt32());
    }

    [Fact]
    public async Task Imports_and_references_are_part_of_the_key()
    {
        await using var context = await CacheHost.StartAsync();

        await context.EvaluateAsync(new ExecutionRequest("1 + 1"));

        // Same source, different compilation inputs, so the cached script does not apply.
        var withImports = await context.EvaluateAsync(
            new ExecutionRequest("1 + 1", Imports: new[] { "System.Text" }));
        Assert.False(CompilationCached(withImports));

        var withReferences = await context.EvaluateAsync(
            new ExecutionRequest("1 + 1", References: new[] { "Scry.Contracts" }));
        Assert.False(CompilationCached(withReferences));

        // ...and each of those is itself cached on repeat.
        var repeated = await context.EvaluateAsync(
            new ExecutionRequest("1 + 1", Imports: new[] { "System.Text" }));
        Assert.True(CompilationCached(repeated));
    }

    [Fact]
    public async Task An_expression_and_a_statement_body_with_the_same_text_do_not_share_an_entry()
    {
        await using var context = await CacheHost.StartAsync();

        var evaluated = await context.EvaluateAsync(new ExecutionRequest("41 + 1"));
        Assert.False(CompilationCached(evaluated));

        // execute wraps the source, so the cache key differs even though the request text matches.
        var executed = await context.Client.RequestAsync(
            "execute",
            new ExecutionRequest("return 41 + 1;"));
        Assert.True(executed.Success, executed.Error?.Message);
        Assert.False(executed.Result!.Value.GetProperty("compilationCached").GetBoolean());
    }

    [Fact]
    public async Task A_failed_compilation_is_not_cached_so_it_can_start_working_later()
    {
        await using var context = await CacheHost.StartAsync();

        // Caching a failure would permanently pin an expression whose type merely has not been
        // loaded yet, so a failure must stay recompilable.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failed = await context.Client.RequestAsync(
                "evaluate",
                new ExecutionRequest("ThisTypeDoesNotExist.Nope"));
            Assert.False(failed.Success);
            Assert.Equal("compilation_failed", failed.Error?.Code);
        }

        // A valid submission still works afterwards, so a failure did not poison the cache.
        Assert.Equal(2, Value(await context.EvaluateAsync(new ExecutionRequest("1 + 1"))).GetInt32());
    }

    [Fact]
    public async Task The_cache_is_bounded_and_evicts_the_least_recently_used_entry()
    {
        await using var context = await CacheHost.StartAsync(maximumCachedScripts: 2);

        await context.EvaluateAsync(new ExecutionRequest("1 + 1"));
        await context.EvaluateAsync(new ExecutionRequest("2 + 2"));

        // Touch the first so it is the warmer of the two before the cache overflows.
        Assert.True(CompilationCached(await context.EvaluateAsync(new ExecutionRequest("1 + 1"))));

        await context.EvaluateAsync(new ExecutionRequest("3 + 3"));

        Assert.True(
            CompilationCached(await context.EvaluateAsync(new ExecutionRequest("1 + 1"))),
            "The recently used entry should have survived eviction.");
        Assert.False(
            CompilationCached(await context.EvaluateAsync(new ExecutionRequest("2 + 2"))),
            "The least recently used entry should have been evicted.");
    }

    [Fact]
    public async Task Wait_recompiles_once_no_matter_how_many_times_it_polls()
    {
        await using var context = await CacheHost.StartAsync();

        var result = await context.Client.RequestAsync(
            "wait",
            new ConditionRequest(
                "false",
                TimeoutMilliseconds: 400,
                PollIntervalMilliseconds: 20));

        Assert.True(result.Success, result.Error?.Message);
        var attempts = result.Result!.Value.GetProperty("attempts").GetInt32();
        Assert.True(attempts > 2, $"Expected several poll attempts, saw {attempts}.");

        // Every attempt after the first reused the compiled script, which is the whole point: the
        // same expression is now cached, so a fresh evaluate of it is a hit.
        Assert.True(CompilationCached(await context.EvaluateAsync(new ExecutionRequest("false"))));
    }

    private static bool CompilationCached(ExecutionResult result) => result.CompilationCached;

    private static JsonElement Value(ExecutionResult result) => result.Value.Value!.Value;

    private sealed class CacheHost : IAsyncDisposable
    {
        private CacheHost(EndpointHost host, ScryClient client)
        {
            Host = host;
            Client = client;
        }

        public EndpointHost Host { get; }

        public ScryClient Client { get; }

        public static async Task<CacheHost> StartAsync(int? maximumCachedScripts = null)
        {
            var host = maximumCachedScripts is null
                ? EndpointHost.Start(builder => builder.RegisterValue("value", 1))
                : EndpointHost.Start(
                    builder => builder.RegisterValue("value", 1),
                    new EndpointOptions { MaximumCachedScripts = maximumCachedScripts.Value });
            var client = await ScryClient.ConnectAsync(host.DescriptorPath);
            return new(host, client);
        }

        public async Task<ExecutionResult> EvaluateAsync(ExecutionRequest request)
        {
            var response = await Client.EvaluateAsync(request);
            return response;
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            Host.Dispose();
        }
    }
}
