using System.Collections.Concurrent;
using Scry.Contracts;
using Scry.Endpoint;
using Scry.Client;

namespace Scry.Tests;

/// <summary>
/// Covers the diagnostic fields added to <see cref="ExecutionResult"/>: whether a submission
/// actually ran through the host's marshaller, which thread it completed on, and the split
/// between compile time and run time.
///
/// <para>
/// Before these existed, the engine already knew all three - <c>marshalToUiThread</c> was
/// computed, the completing thread was whichever one happened to run the continuation, and the
/// compile step was a distinct phase inside the same stopwatch window - but none of it reached
/// the caller. A request that asked for <c>"marshal": "ui"</c> and got a normal success response
/// had no way to confirm marshalling actually happened, short of it failing.
/// </para>
/// </summary>
[Collection("Scry integration")]
public sealed class ExecutionDiagnosticsTests
{
    [Fact]
    public async Task Marshalled_submission_reports_marshalled_true_and_the_marshaller_thread()
    {
        using var executor = new OneShotExecutor();
        await using var host = EndpointHost.Start(
            builder => builder.UseExecutionMarshaller(executor.Marshal));
        await using var client = await ScryClient.ConnectAsync(host.DescriptorPath);

        var result = await client.EvaluateAsync(
            new ExecutionRequest("1 + 1", Marshal: ExecutionMarshalTargets.UiThread));

        Assert.True(result.Marshalled);
        Assert.Equal(executor.ThreadId, result.ThreadId);
    }

    [Fact]
    public async Task Unmarshalled_submission_reports_marshalled_false_and_a_different_thread()
    {
        using var executor = new OneShotExecutor();
        await using var host = EndpointHost.Start(
            builder => builder.UseExecutionMarshaller(executor.Marshal));
        await using var client = await ScryClient.ConnectAsync(host.DescriptorPath);

        var result = await client.EvaluateAsync(new ExecutionRequest("1 + 1"));

        Assert.False(result.Marshalled);
        Assert.NotEqual(executor.ThreadId, result.ThreadId);
    }

    [Fact]
    public async Task Compile_time_is_present_only_on_the_cold_compile()
    {
        await using var host = EndpointHost.Start(builder => builder.RegisterValue("value", 1));
        await using var client = await ScryClient.ConnectAsync(host.DescriptorPath);

        var cold = await client.EvaluateAsync(new ExecutionRequest("40 + 2"));
        Assert.True(cold.CompileMilliseconds.HasValue, "The first submission cannot be a cache hit.");
        Assert.False(cold.CompilationCached);
        Assert.True(cold.RunMilliseconds >= 0);
        Assert.True(
            cold.CompileMilliseconds!.Value + cold.RunMilliseconds <= cold.ElapsedMilliseconds + 1,
            "Compile plus run should not exceed the total elapsed time (allowing 1ms of rounding).");

        var warm = await client.EvaluateAsync(new ExecutionRequest("40 + 2"));
        Assert.True(warm.CompilationCached);
        Assert.Null(warm.CompileMilliseconds);
        Assert.Equal(warm.ElapsedMilliseconds, warm.RunMilliseconds);
    }

    /// <summary>
    /// Runs exactly one marshalled callback on a dedicated background thread and reports that
    /// thread's ID, so a test can assert a marshalled submission completed there. Unlike
    /// <c>ExecutionMarshallingTests.SingleThreadExecutor</c> this installs no
    /// <see cref="SynchronizationContext"/> and does not support more than one submission - these
    /// tests only need to know which thread the work ran on, not that a continuation resumes on
    /// it too.
    /// </summary>
    private sealed class OneShotExecutor : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public OneShotExecutor()
        {
            var started = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                started.Set();
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    work();
                }
            })
            {
                IsBackground = true,
                Name = "Scry.Tests.OneShotExecutor"
            };
            _thread.Start();
            started.Wait();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public Task<object?> Marshal(Func<Task<object?>> callback, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try
                {
                    // Blocking on purpose: this executor makes no attempt to resume continuations
                    // on the same thread, so the awaited call must fully finish here before
                    // control returns, or ThreadId would not describe where it actually ran.
                    completion.TrySetResult(callback().GetAwaiter().GetResult());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });

            return completion.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
        }
    }
}
