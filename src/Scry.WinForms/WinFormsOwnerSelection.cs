using System.Collections;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Scry.WinForms;

/// <summary>
/// The single implementation of "which open form is the marshal owner", shared between initial
/// wiring (<c>DesktopAdapterWiring.ApplyWinForms</c>, reached via reflection since that payload
/// assembly cannot reference <c>System.Windows.Forms</c> directly) and <see cref="WinFormsDispatcher"/>'s
/// own recovery when its current owner becomes invalid.
/// <para>
/// <c>Application.OpenForms</c> is ordered by creation, not by which form is the application's
/// "main" one - a hidden utility/listener form (a third-party UI library's own internal window,
/// created before the application's real main form and left permanently invisible, is a real case
/// this was written against) can land first and never receive genuine UI interaction. Prefer the
/// first <em>visible</em> open form; fall back to the first form of any visibility only when none
/// are visible yet (e.g. before the main form has shown).
/// </para>
/// <para>
/// Confirmed live against a real Control Center process stuck in exactly this state: a Syncfusion
/// <c>XPThemes.ThemeChangeListenerForm</c> - a hidden helper window created once at startup, before
/// the real main form - has a native window handle that Win32's own <c>IsWindow</c> reports as
/// already destroyed, while .NET's own <see cref="Control.IsHandleCreated"/>/
/// <see cref="Control.IsDisposed"/> bookkeeping (driven by message processing on the thread that
/// created it, which had already exited) still reports it as live. It landed in the fallback branch
/// because, at the exact moment a login dialog closed, the real main form had not yet turned
/// <see cref="Control.Visible"/> - so <em>no</em> open form was visible yet and the fallback fell
/// through to "first of any visibility" by creation order, which is this listener form. Once
/// selected, nothing ever re-validated it, so the marshal owner was stuck on a window that can
/// never again receive a posted message, and every call through it hung until its caller's own
/// timeout - forever, since nothing else ever forced re-resolution. Filtering to windows Win32
/// itself still considers alive, here and in <see cref="WinFormsDispatcher.ResolveOwner"/>'s own
/// revalidation of its cached owner, closes both the initial mis-pick and (had it happened to be
/// alive at pick time and die later) the permanent lock-in.
/// </para>
/// </summary>
public static class WinFormsOwnerSelection
{
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    /// <summary>
    /// Whether <paramref name="control"/>'s native window still exists per Win32 itself, rather
    /// than per .NET's own <see cref="Control.IsHandleCreated"/>/<see cref="Control.IsDisposed"/>
    /// bookkeeping - which only updates via message processing on the thread that created the
    /// window, and so can keep reporting a window as live long after the thread that owned it has
    /// exited and the OS has torn the window down. Takes <see cref="Control"/> rather than
    /// <see cref="Form"/> so <see cref="WinFormsDispatcher"/> can also use this to revalidate its
    /// own cached owner, which is a <see cref="Control"/> in general, not necessarily a
    /// <see cref="Form"/>.
    /// <para>
    /// Reading <see cref="Control.Handle"/> itself enforces Windows Forms' own cross-thread guard,
    /// which throws <see cref="InvalidOperationException"/> when called from a thread other than
    /// the one that created the control - but, confirmed empirically, only when that creating
    /// thread is still alive to enforce it. A candidate whose creating thread has already exited
    /// (exactly the dead-handle case this method exists to catch) reads back with no such guard,
    /// since there is no longer a live thread context to enforce it against. So catching that
    /// exception and treating it as "alive" is not a fallback guess: the exception firing at all is
    /// itself proof the owning thread is still there. This matters because both the initial owner
    /// selection (<c>DesktopAdapterWiring.ApplyWinForms</c>, called from the freshly injected entry
    /// thread - never the target's own UI thread) and <see cref="WinFormsDispatcher.ResolveOwner"/>
    /// (called from whichever thread makes a call, ahead of marshaling onto the owner) routinely
    /// evaluate candidates living on a different, perfectly healthy UI thread.
    /// </para>
    /// </summary>
    public static bool IsAlive(Control control)
    {
        if (control.IsDisposed || control.Disposing || !control.IsHandleCreated)
        {
            return false;
        }

        IntPtr handle;
        try
        {
            handle = control.Handle;
        }
        catch (InvalidOperationException)
        {
            return true;
        }

        return IsWindow(handle);
    }

    /// <summary>
    /// Takes a non-generic <see cref="IEnumerable"/> - typically <c>Application.OpenForms</c>
    /// itself, a <c>FormCollection</c> which predates generic collections - rather than
    /// <c>IEnumerable&lt;Form&gt;</c>, so a reflection caller that only has the raw property value
    /// (and no compile-time reference to <see cref="Form"/>) can invoke this directly.
    /// </summary>
    public static Form? SelectOwner(IEnumerable openForms)
    {
        if (openForms is null)
        {
            throw new ArgumentNullException(nameof(openForms));
        }

        var forms = openForms.Cast<Form>().Where(IsAlive).ToArray();
        return forms.FirstOrDefault(form => form.Visible) ?? forms.FirstOrDefault();
    }
}
