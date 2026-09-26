using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace Scry.AttachWinFormsTarget;

/// <summary>
/// A minimal .NET Framework Windows Forms process that deliberately does not reference Scry.NET.
/// Attaching to it is what proves attach mode works on an unmodified application: nothing here
/// registers a root, starts a host, or cooperates in any way. Mirrors Scry.AttachWpfTarget, but for
/// the WinForms adapter.
/// </summary>
internal static class Program
{
    /// <summary>Discovered by tests through the control tree, so keep it stable.</summary>
    internal const string ProbeButtonName = "attach-probe-button";

    [STAThread]
    private static void Main()
    {
        // WinForms only enforces thread affinity when this is true (it defaults to false unless a
        // debugger happens to be attached), so a target run standalone would otherwise let an
        // unmarshalled cross-thread property access silently succeed - explicitly opt in, so an
        // attach test that exercises "marshal": "ui" is actually proving something.
        Control.CheckForIllegalCrossThreadCalls = true;

        // Reproduces a real bug report: some third-party UI libraries create their own hidden
        // utility window on their own dedicated STA thread and message loop before the
        // application's real main form exists - Application.OpenForms is process-wide, not
        // per-thread, so that hidden form can land at index 0. DesktopAdapterWiring.ApplyWinForms
        // used to just take Application.OpenForms.FirstOrDefault() as the marshal owner, wiring
        // every UI-marshalled call to the wrong thread's message loop instead of the one the real
        // form (and its controls) actually lives on. Starting this before the main form below is
        // what makes that ordering adversarial on purpose.
        using var listenerReady = new ManualResetEventSlim();
        var listenerThread = new Thread(() =>
        {
            // Application.OpenForms membership is registered by Show(), not just handle creation -
            // Show, then Hide immediately, to land in OpenForms while ending up genuinely hidden
            // (Visible false), matching the real report. Application.Run(Form) would instead leave
            // it shown for the rest of the process, which is not the scenario being reproduced.
            using var listener = new Form { Name = "hidden-listener", ShowInTaskbar = false };
            listener.Show();
            listener.Hide();
            listenerReady.Set();
            Application.Run();
        })
        {
            IsBackground = true
        };
        listenerThread.SetApartmentState(ApartmentState.STA);
        listenerThread.Start();
        listenerReady.Wait(TimeSpan.FromSeconds(10));

        var button = new Button { Name = ProbeButtonName, Text = "Probe" };

        using var form = new Form
        {
            Name = "main",
            Text = "Scry Attach WinForms Target",
            Width = 320,
            Height = 200
        };
        form.Controls.Add(button);

        // The PID goes to stdout so a test can attach without guessing, and is written only once
        // the handle is created so the control tree is actually there to be snapshotted.
        form.HandleCreated += (_, _) =>
        {
            using var process = Process.GetCurrentProcess();
            Console.Out.WriteLine(process.Id);
            Console.Out.Flush();
        };

        // Don't outlive a test run that fails before it can kill us.
        var lifetime = new System.Threading.Timer(
            _ => form.BeginInvoke((MethodInvoker)Application.Exit),
            null,
            TimeSpan.FromMinutes(3),
            Timeout.InfiniteTimeSpan);

        try
        {
            Application.Run(form);
        }
        finally
        {
            lifetime.Dispose();
        }
    }
}
