using System.Windows.Threading;

namespace Scry.Wpf;

public sealed class WpfDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ThrowIfUnavailable();
    }

    public Dispatcher Dispatcher => _dispatcher;

    public Task<T> InvokeAsync<T>(
        Func<T> callback,
        CancellationToken cancellationToken = default)
    {
        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();

        if (_dispatcher.CheckAccess())
        {
            try
            {
                return Task.FromResult(callback());
            }
            catch (Exception exception)
            {
                return Task.FromException<T>(exception);
            }
        }

        try
        {
            return _dispatcher.InvokeAsync(
                callback,
                DispatcherPriority.Send,
                cancellationToken).Task;
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
    }

    public async Task InvokeAsync(
        Action callback,
        CancellationToken cancellationToken = default)
    {
        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }
        await InvokeAsync(
            () =>
            {
                callback();
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfUnavailable()
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            throw new InvalidOperationException("The WPF dispatcher is shutting down or has shut down.");
        }
    }
}
