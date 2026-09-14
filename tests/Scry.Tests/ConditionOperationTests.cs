using System.Text;
using System.Text.Json;
using Scry.Contracts;
using Scry.Endpoint;
using Scry.Client;

namespace Scry.Tests;

/// <summary>
/// Covers the framework-neutral <c>wait</c> and <c>assert</c> operations. These exist because the
/// only waits and assertions previously available were <c>wpf.*</c>/<c>winforms.*</c> conditions
/// over a bounded UI-tree projection, which left non-UI targets - the plan's first-class case -
/// with no deterministic validation primitive at all, and could not see view-model state even in a
/// desktop target.
/// </summary>
[Collection("Scry integration")]
public sealed class ConditionOperationTests
{
    [Fact]
    public async Task Assert_passes_on_a_true_condition_and_reports_the_value()
    {
        await using var context = await ConditionHost.StartAsync();

        var result = await context.Client.RequestAsync(
            "assert",
            new ConditionRequest("(int)Context.Roots[\"counter\"] > 0"));

        Assert.True(result.Success, result.Error?.Message);
        var payload = result.Result!.Value;
        Assert.True(payload.GetProperty("satisfied").GetBoolean());
        Assert.Equal(1, payload.GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task Assert_fails_with_a_described_comparison()
    {
        await using var context = await ConditionHost.StartAsync();

        var result = await context.Client.RequestAsync(
            "assert",
            new ConditionRequest(
                "(int)Context.Roots[\"counter\"]",
                ConditionOperators.EqualTo,
                Expected: JsonNumber(9999)));

        Assert.False(result.Success);
        Assert.Equal("assertion_failed", result.Error?.Code);
        // The message has to say what was compared, or a failing assertion is unactionable.
        Assert.Contains("equals", result.Error!.Message);
        Assert.Contains("9999", result.Error.Message);
    }

    [Theory]
    [InlineData(ConditionOperators.EqualTo, "\"ready\"", true)]
    [InlineData(ConditionOperators.NotEqualTo, "\"ready\"", false)]
    [InlineData(ConditionOperators.Contains, "\"rea\"", true)]
    [InlineData(ConditionOperators.Contains, "\"absent\"", false)]
    public async Task String_operators_compare_against_the_clr_value(
        string conditionOperator,
        string expectedJson,
        bool satisfied)
    {
        await using var context = await ConditionHost.StartAsync();

        var result = await context.Client.RequestAsync(
            "wait",
            new ConditionRequest(
                "Context.Roots[\"state\"].ToString()",
                conditionOperator,
                Expected: JsonDocument.Parse(expectedJson).RootElement,
                TimeoutMilliseconds: 0));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(satisfied, result.Result!.Value.GetProperty("satisfied").GetBoolean());
    }

    [Fact]
    public async Task Wait_polls_until_a_condition_becomes_true()
    {
        await using var context = await ConditionHost.StartAsync();
        const string condition = "Context.Roots[\"state\"].ToString()";

        // Compile the condition before timing anything. The first submission of a given source
        // pays a cold Roslyn compile, which can exceed the flip delay below - so without this
        // the state is already "arrived" by the time attempt 1 evaluates, the wait is satisfied
        // immediately, and the attempts assertion fails. The test then passes or fails according
        // to whether an earlier test happened to warm the cache, which is why it passed in a
        // full run and failed when run alone.
        await context.Client.RequestAsync(
            "wait",
            new ConditionRequest(
                condition,
                ConditionOperators.EqualTo,
                Expected: JsonDocument.Parse("\"arrived\"").RootElement,
                TimeoutMilliseconds: 1,
                PollIntervalMilliseconds: 1));

        // Flips to true shortly after the wait starts, so this can only pass by actually polling.
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            context.State.Clear().Append("arrived");
        });

        var result = await context.Client.RequestAsync(
            "wait",
            new ConditionRequest(
                condition,
                ConditionOperators.EqualTo,
                Expected: JsonDocument.Parse("\"arrived\"").RootElement,
                TimeoutMilliseconds: 10_000,
                PollIntervalMilliseconds: 50));

        Assert.True(result.Success, result.Error?.Message);
        var payload = result.Result!.Value;
        Assert.True(payload.GetProperty("satisfied").GetBoolean());
        Assert.True(payload.GetProperty("attempts").GetInt32() > 1, "The wait should have polled more than once.");
    }

    [Fact]
    public async Task Wait_returns_unsatisfied_rather_than_failing_on_timeout()
    {
        await using var context = await ConditionHost.StartAsync();

        var result = await context.Client.RequestAsync(
            "wait",
            new ConditionRequest(
                "Context.Roots[\"state\"].ToString()",
                ConditionOperators.EqualTo,
                Expected: JsonDocument.Parse("\"never\"").RootElement,
                TimeoutMilliseconds: 250,
                PollIntervalMilliseconds: 50));

        // Deliberately a successful response carrying satisfied=false, matching the
        // wpf.wait/winforms.wait convention. assert is the operation that fails.
        Assert.True(result.Success, result.Error?.Message);
        Assert.False(result.Result!.Value.GetProperty("satisfied").GetBoolean());
        Assert.True(result.Result!.Value.GetProperty("attempts").GetInt32() >= 1);
    }

    [Fact]
    public async Task Null_operators_do_not_need_an_expected_operand()
    {
        await using var context = await ConditionHost.StartAsync();

        var isNotNull = await context.Client.RequestAsync(
            "assert",
            new ConditionRequest("Context.Roots[\"state\"]", ConditionOperators.IsNotNull));
        Assert.True(isNotNull.Success, isNotNull.Error?.Message);

        var isNull = await context.Client.RequestAsync(
            "assert",
            new ConditionRequest("(string)null", ConditionOperators.IsNull));
        Assert.True(isNull.Success, isNull.Error?.Message);
    }

    [Fact]
    public async Task Invalid_conditions_are_rejected_with_invalid_request()
    {
        await using var context = await ConditionHost.StartAsync();

        var unknownOperator = await context.Client.RequestAsync(
            "assert",
            new ConditionRequest("true", "isSortOfTrue"));
        Assert.Equal("invalid_request", unknownOperator.Error?.Code);

        var missingOperand = await context.Client.RequestAsync(
            "assert",
            new ConditionRequest("1", ConditionOperators.EqualTo));
        Assert.Equal("invalid_request", missingOperand.Error?.Code);

        // Structured operands have no meaningful equality against a bounded projection, so this is
        // refused rather than silently compared against a preview string.
        var structuredOperand = await context.Client.RequestAsync(
            "assert",
            new ConditionRequest(
                "1",
                ConditionOperators.EqualTo,
                Expected: JsonDocument.Parse("""{"a":1}""").RootElement));
        Assert.Equal("invalid_request", structuredOperand.Error?.Code);

        var badPollInterval = await context.Client.RequestAsync(
            "wait",
            new ConditionRequest("true", PollIntervalMilliseconds: 0));
        Assert.Equal("invalid_request", badPollInterval.Error?.Code);
    }

    [Fact]
    public async Task Ui_marshalling_is_rejected_on_a_target_without_a_marshaller()
    {
        await using var context = await ConditionHost.StartAsync();

        var result = await context.Client.RequestAsync(
            "wait",
            new ConditionRequest("true", Marshal: ExecutionMarshalTargets.UiThread));

        // Same refusal evaluate gives, because both go through the one shared resolver.
        Assert.Equal("marshal_target_unavailable", result.Error?.Code);
    }

    [Fact]
    public async Task Wait_and_assert_are_advertised_as_core_operations()
    {
        await using var context = await ConditionHost.StartAsync();

        var capabilities = await context.Client.RequestAsync("capabilities");
        var operations = capabilities.Result!.Value
            .GetProperty("operations")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Contains("wait", operations);
        Assert.Contains("assert", operations);
    }

    private static JsonElement JsonNumber(int value) =>
        JsonDocument.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement;

    private sealed class ConditionHost : IAsyncDisposable
    {
        private ConditionHost(EndpointHost host, ScryClient client, StringBuilder state)
        {
            Host = host;
            Client = client;
            State = state;
        }

        public EndpointHost Host { get; }

        public ScryClient Client { get; }

        /// <summary>
        /// A StringBuilder rather than a test-local type on purpose: condition expressions are
        /// compiled against the target's loaded assemblies, so a private nested class would not be
        /// nameable from a script and the expression would fail to compile rather than evaluate.
        /// </summary>
        public StringBuilder State { get; }

        public static async Task<ConditionHost> StartAsync()
        {
            var state = new StringBuilder("ready");
            var host = EndpointHost.Start(builder => builder
                .RegisterValue("state", state)
                .RegisterValue("counter", 7));
            var client = await ScryClient.ConnectAsync(host.DescriptorPath);
            return new(host, client, state);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            Host.Dispose();
        }
    }
}
