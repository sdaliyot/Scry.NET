using System.Diagnostics;
using Scry.Sdk;

var alias = args
    .Select((value, index) => (value, index))
    .Where(item => item.value == "--alias" && item.index + 1 < args.Length)
    .Select(item => args[item.index + 1])
    .FirstOrDefault() ?? "scry-sample";
var waitForStdin = args.Contains("--wait-for-stdin", StringComparer.Ordinal);
var state = new SampleState();
await using var host = AgentHost.Start(
    builder => builder
        .RegisterValue("app", state, "Mutable non-UI sample state.")
        .RegisterValue("numbers", state.Numbers, "A pageable collection.")
        .RegisterOperation(
            "echo",
            arguments => new
            {
                message = arguments.TryGetProperty("message", out var value)
                    ? value.GetString()
                    : null
            },
            "Returns the supplied message.")
        .RegisterJobOperation(
            "delay",
            async (arguments, context) =>
            {
                var startedAt = DateTimeOffset.UtcNow.UtcTicks;
                context.Log("Delay requested.");
                await Task.Delay(arguments.GetProperty("milliseconds").GetInt32(), context.CancellationToken);
                context.Log("Delay completed.");
                return $"{startedAt}:{DateTimeOffset.UtcNow.UtcTicks}:{Process.GetCurrentProcess().Id}";
            },
            "Waits cooperatively and returns the process identity."),
    new AgentHostOptions { Alias = alias, Aliases = ["scry-sample"] });

Console.WriteLine($"Target: {host.TargetId}");
Console.WriteLine($"Descriptor: {host.DescriptorPath}");
Console.WriteLine(waitForStdin ? "Press Enter to stop." : "Press Ctrl+C to stop.");

if (waitForStdin)
{
    await Console.In.ReadLineAsync().ConfigureAwait(false);
}
else
{
    var stopping = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        stopping.TrySetResult(null);
    };
    await stopping.Task.ConfigureAwait(false);
}

internal sealed class SampleState
{
    public int Count { get; set; } = 7;

    public string Name { get; set; } = "sample";

    public List<int> Numbers { get; } = new() { 1, 2, 3, 4, 5 };

    private string Secret { get; set; } = "visible only when explicitly requested";

    public string Greet(string name) => $"Hello, {name}.";

    public void Fail() => throw new InvalidOperationException(
        "Sample failure.",
        new ArgumentException("Inner sample failure."));
}
