using System.Windows.Forms;
using Scry.Endpoint;
using Scry.WinForms;

namespace Scry.SampleWinForms;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var alias = ReadAlias(args, "scry-winforms-sample");
        var state = new OrderState();
        ApplicationConfiguration.Initialize();
        using var form = new Form
        {
            Name = "main",
            Text = "Scry WinForms embedded sample",
            Width = 480,
            Height = 240
        };
        var status = new Label
        {
            Name = "status",
            Text = state.Status,
            Dock = DockStyle.Fill
        };
        form.Controls.Add(status);
        _ = form.Handle;
        var dispatcher = new WinFormsDispatcher(form);

        using var host = EndpointHost.Start(
            builder => builder
                .RegisterRoot("orders", () => state, "Live order processing state.")
                .RegisterValue("sample.kind", "winforms", "Identifies this embedded sample process.")
                .RegisterOperation(
                    "orders.set-status",
                    async (arguments, cancellationToken) =>
                    {
                        var value = arguments.GetProperty("status").GetString()
                            ?? throw new ArgumentException("status is required.");
                        await dispatcher.InvokeAsync(
                            () =>
                            {
                                state.Status = value;
                                status.Text = value;
                                return true;
                            },
                            cancellationToken);
                        return state.Status;
                    },
                    "Updates order status on the Windows Forms owner thread.",
                    new OperationPolicy
                    {
                        ExecutionPolicy = OperationExecutionPolicy.UiOwner,
                        RequiresConfirmation = true
                    })
                .RegisterJobOperation(
                    "orders.import",
                    async (arguments, context) =>
                    {
                        context.Log("Order import started.");
                        await Task.Delay(
                            arguments.GetProperty("milliseconds").GetInt32(),
                            context.CancellationToken);
                        var count = arguments.GetProperty("count").GetInt32();
                        await dispatcher.InvokeAsync(
                            () => state.ImportedOrders += count,
                            context.CancellationToken);
                        context.Log("Order import completed.");
                        return state.ImportedOrders;
                    },
                    "Imports orders asynchronously and publishes the result on the owner thread.",
                    new OperationPolicy
                    {
                        ExecutionPolicy = OperationExecutionPolicy.UiOwner,
                        RequiresConfirmation = true
                    })
                .UseWinForms(form, winForms => winForms.RegisterRoot("main", form)),
            new EndpointOptions { Alias = alias, Aliases = ["scry-winforms-sample"] });

        Console.WriteLine($"Target: {host.TargetId}");
        Console.WriteLine($"Descriptor: {host.DescriptorPath}");
        Console.WriteLine("Press Enter to stop.");
        _ = StopOnInputAsync(form);
        Application.Run(form);
    }

    private static async Task StopOnInputAsync(Form form)
    {
        await Task.Run(Console.ReadLine).ConfigureAwait(false);
        if (!form.IsDisposed)
        {
            form.BeginInvoke(form.Close);
        }
    }

    private static string ReadAlias(string[] args, string defaultAlias) =>
        args.Select((value, index) => (value, index))
            .Where(item => item.value == "--alias" && item.index + 1 < args.Length)
            .Select(item => args[item.index + 1])
            .FirstOrDefault() ?? defaultAlias;
}

internal sealed class OrderState
{
    public string Status { get; set; } = "Ready";

    public int ImportedOrders { get; set; }
}
