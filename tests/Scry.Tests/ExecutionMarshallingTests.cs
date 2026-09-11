using System.Collections.Concurrent;
using Scry.Contracts;
using Scry.Runtime;
using Scry.Sdk;

namespace Scry.Tests;

/// <summary>
/// Covers opting a submission onto a host-nominated thread. Uses a plain single-threaded executor
/// rather than a UI framework, so the contract is exercised identically on both target frameworks
/// and without a message pump.
/// </summary>
public sealed class ExecutionMarshallingTests
{
    [Fact]
    public async Task Ui_marshalling_is_rejected_and_unadvertised_without_a_marshaller()
    {
        using var host = AgentHost.Start(builder => builder.RegisterValue("value", 1));
        await using var client = await ScryClient.ConnectAsync(host.DescriptorPath);

        var capabilities = await client.RequestAsync("capabilities");
        Assert.DoesNotContain(
            ProtocolConstants.UiThreadMarshallingFeature,
            capabilities.Result!.Value.GetProperty("features").EnumerateArray().Select(item => item.GetString()));

        var rejected = await client.RequestAsync(
            "evaluate",
            new ExecutionRequest("1 + 1", Marshal: ExecutionMarshalTargets.UiThread));
        Assert.Equal("marshal_target_unavailable", rejected.Error?.Code);

        // the same submission still runs when it does not ask to be marshalled
        var evaluated = await client.EvaluateAsync(new ExecutionRequest("1 + 1"));
        Assert.Equal(2, evaluated.Value.Value!.Value.GetInt32());
    }

    [Fact]
    public async Task Unknown_marshal_target_is_rejected()
    {
        using var executor = new SingleThreadExecutor();
        using var host = AgentHost.Start(builder => builder.UseExecutionMarshaller(executor.Marshal));
        await using var client = await ScryClient.ConnectAsync(host.DescriptorPath);

        var rejected = await client.RequestAsync(
            "evaluate",
            new ExecutionRequest("1 + 1", Marshal: "background"));
        Assert.Equal("marshal_target_not_supported", rejected.Error?.Code);
    }

    [Fact]
    public async Task Marshalled_submissions_start_and_resume_on_the_nominated_thread()
    {
        using var executor = new SingleThreadExecutor();
        using var host = AgentHost.Start(builder => builder.UseExecutionMarshaller(executor.Marshal));
        await using var client = await ScryClient.ConnectAsync(host.DescriptorPath);

        var capabilities = await client.RequestAsync("capabilities");
        Assert.Contains(
            ProtocolConstants.UiThreadMarshallingFeature,
            capabilities.Result!.Value.GetProperty("features").EnumerateArray().Select(item => item.GetString()));

        // await mid-submission: the continuation must stay on the marshalled thread, otherwise a
        // script that awaits anything would silently fall off the UI thread half-way through
        var marshalled = await client.EvaluateAsync(new ExecutionRequest(
            """
            var before = System.Threading.Thread.CurrentThread.ManagedThreadId;
            await System.Threading.Tasks.Task.Yield();
            var after = System.Threading.Thread.CurrentThread.ManagedThreadId;
            return before + ":" + after;
            """,
            Marshal: ExecutionMarshalTargets.UiThread));

        var expected = $"{executor.ThreadId}:{executor.ThreadId}";
        Assert.Equal(expected, marshalled.Value.Value!.Value.GetString());

        // and without the flag it runs wherever the endpoint served the request
        var unmarshalled = await client.EvaluateAsync(new ExecutionRequest(
            "System.Threading.Thread.CurrentThread.ManagedThreadId"));
        Assert.NotEqual(executor.ThreadId, unmarshalled.Value.Value!.Value.GetInt32());
    }

    [Fact]
    public async Task Marshalled_submission_failures_surface_as_normal_execution_errors()
    {
        using var executor = new SingleThreadExecutor();
        using var host = AgentHost.Start(builder => builder.UseExecutionMarshaller(executor.Marshal));
        await using var client = await ScryClient.ConnectAsync(host.DescriptorPath);

        // a statement body, so this throws at run time on the marshalled thread rather than
        // failing to compile as an expression
        var failed = await client.RequestAsync(
            "execute",
            new ExecutionRequest(
                "throw new System.InvalidOperationException(\"boom\");",
                Marshal: ExecutionMarshalTargets.UiThread));

        Assert.False(failed.Success);
        Assert.Equal("operation_failed", failed.Error?.Code);
        Assert.Contains("boom", failed.Error!.Message);

        // the executor thread survives a failed submission and still serves the next one
        var recovered = await client.EvaluateAsync(new ExecutionRequest(
            "System.Threading.Thread.CurrentThread.ManagedThreadId",
            Marshal: ExecutionMarshalTargets.UiThread));
        Assert.Equal(executor.ThreadId, recovered.Value.Value!.Value.GetInt32());
    }

    /// <summary>
    /// Stands in for a UI dispatcher: one dedicated thread draining a queue, with a
    /// SynchronizationContext installed so awaited continuations come back to it - which is the
    /// behaviour a real dispatcher provides and that the marshalling contract depends on.
    /// </summary>
    private sealed class SingleThreadExecutor : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public SingleThreadExecutor()
        {
            var started = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new QueueSynchronizationContext(_queue));
                started.Set();
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    work();
                }
            })
            {
                IsBackground = true,
                Name = "Scry.Tests.SingleThreadExecutor"
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
                    // deliberately not awaited here: the inner task's continuations are posted back
                    // to this thread's SynchronizationContext, so the submission resumes on it
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

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
        }
    }

    private sealed class QueueSynchronizationContext(BlockingCollection<Action> queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (!queue.IsAddingCompleted)
            {
                queue.Add(() => callback(state));
            }
        }

        public override void Send(SendOrPostCallback callback, object? state) => callback(state);
    }
}
