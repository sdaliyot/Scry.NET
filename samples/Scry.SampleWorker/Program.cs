using Scry.Sdk;

var alias = SampleArguments.Alias(args, "scry-worker-sample");
var state = new WorkerState();
using var stopping = new CancellationTokenSource();
var worker = RunWorkerAsync(state, stopping.Token);
await using var host = AgentHost.Start(
    builder => builder
        .RegisterRoot("worker", () => state, "Live queue worker state.")
        .RegisterValue("service.name", "invoice-processor", "Stable worker service identity.")
        .RegisterOperation(
            "queue.set-mode",
            arguments =>
            {
                state.Mode = arguments.GetProperty("mode").GetString()
                    ?? throw new ArgumentException("mode is required.");
                return state.Mode;
            },
            "Changes how the worker consumes queued invoices.",
            new AgentOperationPolicy { RequiresConfirmation = true })
        .RegisterJobOperation(
            "queue.drain",
            async (arguments, context) =>
            {
                context.Log("Queue drain started.");
                await Task.Delay(
                    arguments.GetProperty("milliseconds").GetInt32(),
                    context.CancellationToken);
                state.Processed += arguments.GetProperty("items").GetInt32();
                context.Log("Queue drain completed.");
                return state.Processed;
            },
            "Simulates a cancellable, long-running queue drain.",
            new AgentOperationPolicy { RequiresConfirmation = true }),
    new AgentHostOptions { Alias = alias, Aliases = ["scry-worker-sample"] });

Console.WriteLine($"Target: {host.TargetId}");
Console.WriteLine($"Descriptor: {host.DescriptorPath}");
Console.WriteLine("Press Enter to stop.");
await Console.In.ReadLineAsync().ConfigureAwait(false);
await stopping.CancelAsync();
await worker.ConfigureAwait(false);

static async Task RunWorkerAsync(WorkerState state, CancellationToken cancellationToken)
{
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref state.Heartbeats);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
}

internal static class SampleArguments
{
    public static string Alias(string[] args, string defaultAlias) =>
        args.Select((value, index) => (value, index))
            .Where(item => item.value == "--alias" && item.index + 1 < args.Length)
            .Select(item => args[item.index + 1])
            .FirstOrDefault() ?? defaultAlias;
}

internal sealed class WorkerState
{
    public int Heartbeats;

    public string Mode { get; set; } = "normal";

    public int Processed { get; set; }
}
