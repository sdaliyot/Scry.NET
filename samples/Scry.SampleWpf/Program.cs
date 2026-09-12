using System.Windows;
using System.Windows.Controls;
using Scry.Endpoint;
using Scry.Wpf;

namespace Scry.SampleWpf;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var alias = ReadAlias(args, "scry-wpf-sample");
        var state = new EditorState();
        var application = new Application();
        var caption = new TextBlock { Name = "caption", Text = state.Title };
        var window = new Window
        {
            Title = "Scry WPF embedded sample",
            Width = 480,
            Height = 240,
            Content = caption
        };
        using var host = EndpointHost.Start(
            builder => builder
                .RegisterRoot("editor", () => state, "Live document editor state.")
                .RegisterValue("sample.kind", "wpf", "Identifies this embedded sample process.")
                .RegisterOperation(
                    "editor.set-title",
                    async (arguments, cancellationToken) =>
                    {
                        var title = arguments.GetProperty("title").GetString()
                            ?? throw new ArgumentException("title is required.");
                        await application.Dispatcher.InvokeAsync(
                            () =>
                            {
                                state.Title = title;
                                caption.Text = title;
                            },
                            System.Windows.Threading.DispatcherPriority.Normal,
                            cancellationToken);
                        return state.Title;
                    },
                    "Updates the editor title on the WPF dispatcher.",
                    new OperationPolicy
                    {
                        ExecutionPolicy = OperationExecutionPolicy.UiOwner,
                        RequiresConfirmation = true
                    })
                .RegisterJobOperation(
                    "editor.load-document",
                    async (arguments, context) =>
                    {
                        context.Log("Document load started.");
                        await Task.Delay(
                            arguments.GetProperty("milliseconds").GetInt32(),
                            context.CancellationToken);
                        var title = arguments.GetProperty("title").GetString()
                            ?? throw new ArgumentException("title is required.");
                        await application.Dispatcher.InvokeAsync(
                            () =>
                            {
                                state.Title = title;
                                state.LoadedDocuments++;
                                caption.Text = title;
                            });
                        context.Log("Document load completed.");
                        return state.LoadedDocuments;
                    },
                    "Loads a document asynchronously, then applies it on the WPF dispatcher.",
                    new OperationPolicy
                    {
                        ExecutionPolicy = OperationExecutionPolicy.UiOwner,
                        RequiresConfirmation = true
                    })
                .UseWpf(application, wpf => wpf.RegisterWindow("main", window)),
            new EndpointOptions { Alias = alias, Aliases = ["scry-wpf-sample"] });

        Console.WriteLine($"Target: {host.TargetId}");
        Console.WriteLine($"Descriptor: {host.DescriptorPath}");
        Console.WriteLine("Press Enter to stop.");
        _ = StopOnInputAsync(application);
        application.Run(window);
    }

    private static async Task StopOnInputAsync(Application application)
    {
        await Task.Run(Console.ReadLine).ConfigureAwait(false);
        await application.Dispatcher.InvokeAsync(application.Shutdown);
    }

    private static string ReadAlias(string[] args, string defaultAlias) =>
        args.Select((value, index) => (value, index))
            .Where(item => item.value == "--alias" && item.index + 1 < args.Length)
            .Select(item => args[item.index + 1])
            .FirstOrDefault() ?? defaultAlias;
}

internal sealed class EditorState
{
    public string Title { get; set; } = "Untitled";

    public int LoadedDocuments { get; set; }
}
