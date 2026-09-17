using System.Collections.Concurrent;
using System.Diagnostics;
using Scry.Contracts;
using Scry.Endpoint;
using Scry.Client;

namespace Scry.Tests;

/// <summary>
/// Covers what happens to the *client* when a target stops answering.
///
/// <para>
/// A submission marshalled onto the host thread holds that thread for its whole duration, and
/// <see cref="ExecutionRequest.TimeoutMilliseconds"/> is cooperative - it can only end a
/// submission that observes its cancellation token. A submission that does not observe it never
/// returns, so the target never writes a response. That much is by design and documented.
/// </para>
///
/// <para>
/// What these tests pin down is that it stops there. Before the client carried a request
/// deadline, such a submission wedged the caller too: the read never completed, and the request
/// lock meant every later request on that client waited behind it forever.
/// </para>
///
/// <para>
/// Each test wedges a dedicated executor thread on purpose and never recovers it. The thread is
/// a background thread so the test process can still exit, and the executor is deliberately left
/// undisposed - joining it would wait on the very thread the test just froze. That is why no
/// test like this existed before; it is not an oversight to be tidied up.
/// </para>
/// </summary>
[Collection("Scry integration")]
public sealed class ClientWatchdogTests
{
    /// <summary>The wedging submission: no token observation, so nothing can end it.</summary>
    private const string NonCooperativeSource = "while (true) { } return 1;";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Generous relative to the 2-second deadline: this asserts the call returns at all, not how
    /// promptly. A tight bound here would fail on a loaded machine for no useful reason.
    /// </summary>
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_target_that_never_answers_fails_the_request_instead_of_hanging()
    {
        var executor = new WedgeableExecutor();
        using var host = EndpointHost.Start(
            builder => builder.UseExecutionMarshaller(executor.Marshal));
        await using var client = await ScryClient.ConnectAsync(host.DescriptorPath);
        client.RequestTimeout = RequestTimeout;

        var stopwatch = Stopwatch.StartNew();
        var request = client.RequestAsync(
            "evaluate",
            new ExecutionRequest(NonCooperativeSource, Marshal: ExecutionMarshalTargets.UiThread));

        // Bounded so the suite reports a failure rather than hanging when the deadline is absent.
        var finished = await Task.WhenAny(request, Task.Delay(TestBudget));
        Assert.True(
            ReferenceEquals(finished, request),
            $"The request never returned within {TestBudget.TotalSeconds:0} seconds, so the " +
            "client has no request deadline and a wedged target takes the caller with it.");

        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => request);
        Assert.Contains("did not respond", timeout.Message, StringComparison.OrdinalIgnoreCase);
        stopwatch.Stop();

        // The executor thread is still wedged and stays that way; see the class remarks.
        Assert.True(executor.IsWedged);
    }

    [Fact]
    public async Task A_timed_out_client_is_faulted_rather_than_silently_reused()
    {
        var executor = new WedgeableExecutor();
        using var host = EndpointHost.Start(
            builder => builder.UseExecutionMarshaller(executor.Marshal));
        await using var client = await ScryClient.ConnectAsync(host.DescriptorPath);
        client.RequestTimeout = RequestTimeout;

        var wedge = client.RequestAsync(
            "evaluate",
            new ExecutionRequest(NonCooperativeSource, Marshal: ExecutionMarshalTargets.UiThread));
        var finished = await Task.WhenAny(wedge, Task.Delay(TestBudget));
        Assert.True(ReferenceEquals(finished, wedge), "The wedging request never returned.");
        await Assert.ThrowsAsync<TimeoutException>(() => wedge);

        // The abandoned request's response may still arrive on that pipe, and reading it as the
        // reply to a later request would be worse than failing. So the client retires itself.
        var afterwards = client.RequestAsync("capabilities");
        var second = await Task.WhenAny(afterwards, Task.Delay(TestBudget));
        Assert.True(
            ReferenceEquals(second, afterwards),
            "A request after a timeout blocked, so the request lock is still held by the " +
            "abandoned request.");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => afterwards);
    }

    [Fact]
    public async Task A_wedged_target_does_not_stop_a_fresh_client_from_failing_fast()
    {
        var executor = new WedgeableExecutor();
        using var host = EndpointHost.Start(
            builder => builder.UseExecutionMarshaller(executor.Marshal));

        await using (var wedging = await ScryClient.ConnectAsync(host.DescriptorPath))
        {
            wedging.RequestTimeout = RequestTimeout;
            var wedge = wedging.RequestAsync(
                "evaluate",
                new ExecutionRequest(NonCooperativeSource, Marshal: ExecutionMarshalTargets.UiThread));
            var finished = await Task.WhenAny(wedge, Task.Delay(TestBudget));
            Assert.True(ReferenceEquals(finished, wedge), "The wedging request never returned.");
            await Assert.ThrowsAsync<TimeoutException>(() => wedge);
        }

        // A second connection is served on its own pipe, so unmarshalled work is unaffected -
        // this is what distinguishes "the host thread is busy" from "the endpoint is dead".
        await using var fresh = await ScryClient.ConnectAsync(host.DescriptorPath);
        fresh.RequestTimeout = RequestTimeout;
        var unmarshalled = fresh.EvaluateAsync(new ExecutionRequest("1 + 1"));
        var second = await Task.WhenAny(unmarshalled, Task.Delay(TestBudget));
        Assert.True(
            ReferenceEquals(second, unmarshalled),
            "A fresh client could not run unmarshalled work while the host thread was wedged.");
        Assert.Equal(2, (await unmarshalled).Value.Value!.Value.GetInt32());

        // And a marshalled request on the fresh client still times out rather than hanging,
        // because the thread it needs is gone for good.
        var marshalled = fresh.RequestAsync(
            "evaluate",
            new ExecutionRequest("1 + 1", Marshal: ExecutionMarshalTargets.UiThread));
        var third = await Task.WhenAny(marshalled, Task.Delay(TestBudget));
        Assert.True(ReferenceEquals(third, marshalled), "The marshalled request never returned.");
        await Assert.ThrowsAsync<TimeoutException>(() => marshalled);
    }

    /// <summary>
    /// The same wedged-target scenario as
    /// <see cref="A_target_that_never_answers_fails_the_request_instead_of_hanging"/>, but over
    /// TCP. Worth its own test because a socket receive that has already started ignores
    /// cancellation on <em>both</em> frameworks - unlike the named pipe, where that is a .NET
    /// Framework-only quirk. See the "Racing a delay" remark on
    /// <c>ScryClient.WithDeadlineAsync</c>, which used to describe this as .NET-Framework-only before
    /// this test existed to disprove that on modern .NET too.
    /// </summary>
    [Fact]
    public async Task A_wedged_target_over_tcp_fails_the_request_instead_of_hanging()
    {
        var executor = new WedgeableExecutor();
        await using var host = EndpointHost.Start(
            builder => builder.UseExecutionMarshaller(executor.Marshal),
            new EndpointOptions { TcpPort = 0 });
        await using var client = await ScryClient.ConnectOverTcpAsync(host.DescriptorPath);
        Assert.Equal(ScryTransports.Tcp, client.Transport);
        client.RequestTimeout = RequestTimeout;

        var request = client.RequestAsync(
            "evaluate",
            new ExecutionRequest(NonCooperativeSource, Marshal: ExecutionMarshalTargets.UiThread));
        var finished = await Task.WhenAny(request, Task.Delay(TestBudget));
        Assert.True(
            ReferenceEquals(finished, request),
            $"The TCP request never returned within {TestBudget.TotalSeconds:0} seconds.");

        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => request);
        Assert.Contains("did not respond", timeout.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(executor.IsWedged);
    }

    /// <summary>
    /// A one-thread executor that can be wedged and is never expected to recover. Deliberately
    /// not <see cref="IDisposable"/>: there is nothing safe to do in a Dispose, because the
    /// thread it owns is the thread the test froze.
    /// </summary>
    private sealed class WedgeableExecutor
    {
        private readonly BlockingCollection<Action> _queue = new();
        private volatile bool _wedged;

        public WedgeableExecutor()
        {
            var started = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new QueueContext(_queue));
                started.Set();
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    work();
                }
            })
            {
                IsBackground = true,
                Name = "Scry.Tests.WedgeableExecutor"
            };
            thread.Start();
            started.Wait();
        }

        public bool IsWedged => _wedged;

        public Task<object?> Marshal(Func<Task<object?>> callback, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                _wedged = true;
                try
                {
                    callback().ContinueWith(
                        task =>
                        {
                            if (task.IsFaulted)
                            {
                                completion.TrySetException(task.Exception!.InnerExceptions);
                            }
                            else if (task.IsCanceled)
                            {
                                completion.TrySetCanceled();
                            }
                            else
                            {
                                completion.TrySetResult(task.Result);
                            }
                        },
                        TaskScheduler.Default);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });

            return completion.Task;
        }

        private sealed class QueueContext(BlockingCollection<Action> queue) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback callback, object? state)
            {
                try
                {
                    queue.Add(() => callback(state));
                }
                catch (InvalidOperationException)
                {
                    // Queue closed; nothing left to run the continuation on.
                }
            }

            public override void Send(SendOrPostCallback callback, object? state) =>
                Post(callback, state);
        }
    }
}
