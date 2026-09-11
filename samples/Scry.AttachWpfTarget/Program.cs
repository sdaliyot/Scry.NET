using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Scry.AttachWpfTarget;

/// <summary>
/// A minimal .NET Framework WPF process that deliberately does not reference Scry.NET. Attaching to
/// it is what proves attach mode works on an unmodified application: nothing here registers a root,
/// starts a host, or cooperates in any way.
/// </summary>
internal static class Program
{
    /// <summary>Discovered by tests through the WPF logical tree, so keep it stable.</summary>
    internal const string ProbeAutomationId = "attach-probe-button";

    [STAThread]
    private static void Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };

        var button = new Button { Name = "ProbeButton", Content = "Probe" };
        AutomationProperties.SetAutomationId(button, ProbeAutomationId);

        var window = new Window
        {
            Title = "Scry Attach WPF Target",
            Width = 320,
            Height = 200,
            Content = new StackPanel { Children = { button } }
        };

        // The PID goes to stdout so a test can attach without guessing, and is written only once
        // the window is loaded so the logical tree is actually there to be snapshotted.
        window.Loaded += (_, _) =>
        {
            using var process = Process.GetCurrentProcess();
            Console.Out.WriteLine(process.Id);
            Console.Out.Flush();
        };

        // Don't outlive a test run that fails before it can kill us.
        var lifetime = new Timer(
            _ => application.Dispatcher.BeginInvoke(new Action(application.Shutdown)),
            null,
            TimeSpan.FromMinutes(3),
            Timeout.InfiniteTimeSpan);

        try
        {
            application.Run(window);
        }
        finally
        {
            lifetime.Dispose();
        }
    }
}
