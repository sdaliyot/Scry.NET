using System.Windows.Forms;
using Scry.Runtime;

namespace Scry.WinForms;

public sealed class WinFormsDispatcher
{
    private Control _owner;

    public WinFormsDispatcher(Control owner)
    {
        if (owner is null)
        {
            throw new ArgumentNullException(nameof(owner));
        }
        if (owner.IsDisposed || owner.Disposing)
        {
            throw new ObjectDisposedException(owner.GetType().FullName);
        }
        if (!owner.IsHandleCreated)
        {
            throw new InvalidOperationException(
                "The Windows Forms dispatcher owner must have a created handle.");
        }

        // Deliberately no owner.InvokeRequired check here (unlike the embedded sample's own usage,
        // which does construct on the owner's UI thread) - see Scry.Wpf.WpfDispatcher, which is
        // likewise thread-agnostic at construction and only checks CheckAccess() per invocation.
        // An attach-mode injection's entry point never runs on the target's UI thread (it runs on a
        // freshly created thread - see docs/threat-model.md), so requiring on-thread construction
        // made DesktopAdapterWiring.ApplyWinForms (which calls UseWinForms from that entry point)
        // fail unconditionally. A Control handle can be safely captured from any thread; only
        // actual member access needs to marshal, which InvokeAsync below already does.
        _owner = owner;
    }

    public Task<T> InvokeAsync<T>(Func<T> callback, CancellationToken cancellationToken = default)
    {
        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }
        cancellationToken.ThrowIfCancellationRequested();

        var owner = ResolveOwner();

        if (!owner.InvokeRequired)
        {
            return Task.FromResult(callback());
        }

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<T>)state!).TrySetCanceled(),
            completion);
        try
        {
            owner.BeginInvoke((MethodInvoker)(() =>
            {
                try
                {
                    if (!completion.Task.IsCompleted)
                    {
                        completion.TrySetResult(callback());
                    }
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    registration.Dispose();
                }
            }));
        }
        catch
        {
            registration.Dispose();
            throw;
        }

        return completion.Task;
    }

    /// <summary>
    /// Returns a currently-usable marshal owner, re-resolving from <c>Application.OpenForms</c>
    /// (via the same heuristic used at initial wiring - see <see cref="WinFormsOwnerSelection"/>)
    /// and swapping <see cref="_owner"/> when the current one has become invalid.
    /// <para>
    /// The owner is captured once at injection time and never otherwise re-evaluated. Without
    /// this, any app whose first visible window is transient - a login dialog that closes once
    /// the main window appears is the motivating case - would wedge permanently: once the
    /// original owner's handle is destroyed, every future call would keep throwing against it
    /// forever, including the very <c>winforms.wait</c> poll a caller would use to detect the
    /// transition, with no way to recover from outside the target process (the adapter is wired
    /// once at injection and never re-wired).
    /// </para>
    /// <para>
    /// Only throws when no open form exists at all right now - a brief window-close/window-open
    /// gap - which stays a fast, distinguishable failure rather than a hang, but not a permanent
    /// one: the next call tries again. Thrown as <see cref="ScryOperationException"/> with a
    /// dedicated code (rather than a bare <see cref="InvalidOperationException"/>) specifically so
    /// <c>OperationDispatcher</c>'s <c>wait</c> polling loop can classify and tolerate this exact
    /// gap - the same way it already tolerates a per-iteration <c>execution_timed_out</c> - instead
    /// of every exception type alike falling into the generic <c>operation_failed</c> bucket and
    /// failing the whole wait on the very first iteration that hits the gap.
    /// </para>
    /// </summary>
    private Control ResolveOwner()
    {
        var current = _owner;
        if (!current.IsDisposed && !current.Disposing && current.IsHandleCreated)
        {
            return current;
        }

        var resolved = WinFormsOwnerSelection.SelectOwner(Application.OpenForms);
        if (resolved is null || resolved.IsDisposed || resolved.Disposing || !resolved.IsHandleCreated)
        {
            throw new ScryOperationException(
                "dispatcher_owner_unavailable",
                "The Windows Forms dispatcher owner handle is unavailable, and no open form is " +
                "currently available to recover through. Retry once a form is open.");
        }

        // A benign race: a concurrent call may already have swapped in a different, equally
        // valid resolution between the read above and this exchange. Either way, re-reading
        // _owner after the attempt returns whichever resolution actually won.
        Interlocked.CompareExchange(ref _owner, resolved, current);
        return _owner;
    }
}
