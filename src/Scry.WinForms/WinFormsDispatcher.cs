using System.Windows.Forms;

namespace Scry.WinForms;

public sealed class WinFormsDispatcher
{
    private readonly Control _owner;

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

        if (_owner.IsDisposed || _owner.Disposing)
        {
            throw new ObjectDisposedException(_owner.GetType().FullName);
        }
        if (!_owner.IsHandleCreated)
        {
            throw new InvalidOperationException(
                "The Windows Forms dispatcher owner handle is unavailable.");
        }

        if (!_owner.InvokeRequired)
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
            _owner.BeginInvoke((MethodInvoker)(() =>
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
}
