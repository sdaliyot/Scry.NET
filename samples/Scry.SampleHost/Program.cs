using Scry.Sdk;

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
            "Returns the supplied message."),
    new AgentHostOptions { Alias = "scry-sample" });

Console.WriteLine($"Target: {host.TargetId}");
Console.WriteLine($"Descriptor: {host.DescriptorPath}");
Console.WriteLine("Press Ctrl+C to stop.");

var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.TrySetResult();
};
await stopping.Task.ConfigureAwait(false);

internal sealed class SampleState
{
    public int Count { get; set; } = 7;

    public string Name { get; set; } = "sample";

    public List<int> Numbers { get; } = [1, 2, 3, 4, 5];

    private string Secret { get; set; } = "visible only when explicitly requested";

    public string Greet(string name) => $"Hello, {name}.";

    public void Fail() => throw new InvalidOperationException(
        "Sample failure.",
        new ArgumentException("Inner sample failure."));
}
