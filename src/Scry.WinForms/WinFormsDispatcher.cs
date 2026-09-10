using System.Windows.Forms;

namespace Scry.WinForms;

public sealed class WinFormsDispatcher
{
    private readonly Control _owner;

    public WinFormsDispatcher(Control owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.IsDisposed || owner.Disposing)
        {
            throw new ObjectDisposedException(owner.GetType().FullName);
        }
        if (!owner.IsHandleCreated)
        {
            throw new InvalidOperationException(
                "The Windows Forms dispatcher owner must have a created handle.");
        }
        if (owner.InvokeRequired)
        {
            throw new InvalidOperationException(
                "Create the Windows Forms dispatcher on the owner's UI thread.");
        }

        _owner = owner;
    }

    public Task<T> InvokeAsync<T>(Func<T> callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
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
