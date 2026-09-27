using System.Text.Json;
using Scry.Contracts;
using Scry.Endpoint;
using Scry.Client;

namespace Scry.Tests;

/// <summary>
/// Covers the fix for a real memory-growth report: a caller baking a runtime-varying literal
/// directly into a script's source text (because <see cref="ExecutionRequest"/> had no way to pass
/// data separately) turned every distinct value into a fresh, permanently-cached compile, each
/// bound against the full loaded-assembly set. <see cref="ExecutionRequest.Arguments"/> lets the
/// same script body vary only data and stay a cache hit; <see cref="ExecutionRequest.References"/>
/// actually narrowing the compiled reference set (previously validated but silently ignored - see
/// <c>AssemblyCatalog.GetMetadataReferences</c>) is what helps the cases that still vary by source
/// text regardless (e.g. a caller-supplied predicate expression).
/// </summary>
[Collection("Scry integration")]
public sealed class ExecutionArgumentsAndReferencesTests
{
    /// <summary>Referenced only by name from script source below - proves a narrowed reference set
    /// genuinely excludes this test assembly unless named, and includes it once it is.</summary>
    public sealed class ReferenceNarrowingMarker
    {
        public const string Value = "marker";
    }

    [Fact]
    public async Task Same_script_with_different_arguments_stays_a_cache_hit()
    {
        await using var host = EndpointHost.Start(builder => builder.RegisterValue("value", 1));
        await using var client = await ScryClient.ConnectAsync(host.DescriptorPath);

        var first = await client.EvaluateAsync(
            new ExecutionRequest(
                "Context.GetArgument<int>(\"value\")",
                Arguments: JsonSerializer.SerializeToElement(new { value = 41 }, ScryJson.Options)));
        Assert.False(first.CompilationCached, "The first compile cannot be a cache hit.");
        Assert.Equal(41, first.Value.Value!.Value.GetInt32());

        var second = await client.EvaluateAsync(
            new ExecutionRequest(
                "Context.GetArgument<int>(\"value\")",
                Arguments: JsonSerializer.SerializeToElement(new { value = 99 }, ScryJson.Options)));
        Assert.True(
            second.CompilationCached,
            "Identical source with only the argument value changed should reuse the compiled script.");
        Assert.Equal(99, second.Value.Value!.Value.GetInt32());
    }

    [Fact]
    public async Task Missing_argument_reports_a_clear_error()
    {
        await using var host = EndpointHost.Start(builder => builder.RegisterValue("value", 1));
        await using var client = await ScryClient.ConnectAsync(host.DescriptorPath);

        var response = await client.RequestAsync(
            "evaluate",
            new ExecutionRequest("Context.GetArgument<int>(\"missing\")"));

        Assert.False(response.Success);
        Assert.Equal("operation_failed", response.Error?.Code);
        Assert.Contains("missing", response.Error?.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", response.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task References_narrows_the_compiled_reference_set()
    {
        await using var host = EndpointHost.Start(builder => builder.RegisterValue("value", 1));
        await using var client = await ScryClient.ConnectAsync(host.DescriptorPath);
        const string source =
            "Scry.Tests.ExecutionArgumentsAndReferencesTests.ReferenceNarrowingMarker.Value";

        // A non-empty references list that names a real, loaded, compatible assembly but not this
        // test assembly - narrowed compilation should not be able to see the marker type at all,
        // proving the reference set really is narrower than the unfiltered default (which the bug
        // already made "still works" true for, so that alone would not be a meaningful assertion).
        var narrowed = await client.RequestAsync(
            "evaluate",
            new ExecutionRequest(source, References: new[] { "Scry.Contracts" }));
        Assert.False(narrowed.Success, "The test assembly should not be reachable without naming it.");
        Assert.Equal("compilation_failed", narrowed.Error?.Code);

        var named = await client.RequestAsync(
            "evaluate",
            new ExecutionRequest(source, References: new[] { "Scry.Contracts", "Scry.Tests" }));
        Assert.True(named.Success, named.Error?.Message);
        Assert.Equal(
            ReferenceNarrowingMarker.Value,
            named.Result!.Value.GetProperty("value").GetProperty("value").GetString());
    }
}
