using System.Diagnostics;
using Scry.Runtime;
using Scry.Endpoint;
using Scry.Client;

namespace Scry.Tests;

/// <summary>
/// Regression tests for the shutdown-ordering bug where <c>JobManager.Dispose()</c> forcibly
/// disposed every retained <c>JobEntry</c> (and <c>SessionManager.Dispose()</c> forcibly disposed
/// every session) after a bounded grace period, even when a job's background task was still
/// executing. That could throw <see cref="ObjectDisposedException"/> from the still-running
/// operation and could corrupt a session another in-flight operation was still using.
/// </summary>
[Collection("Scry integration")]
public sealed class JobDisposalTests
{
    [Fact]
    public async Task Host_dispose_does_not_throw_when_a_job_outlives_the_shutdown_grace_period()
    {
        var signal = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = EndpointHost.Start(builder => builder.RegisterJobOperation(
            "outlives-shutdown",
            async (_, context) =>
            {
                Exception? captured = null;
                try
                {
                    // Simulate work that does not observe cooperative cancellation immediately,
                    // so it keeps running past the JobManager shutdown grace period (5 seconds).
                    await Task.Delay(TimeSpan.FromSeconds(6), CancellationToken.None).ConfigureAwait(false);

                    // Observe cancellation through the token's wait handle after the grace period
                    // has elapsed. This models a job that blocks on a handle - legacy or interop
                    // work that cannot await - and is the operation that actually exercises the bug.
                    //
                    // WaitHandle is deliberate, not incidental: it is the only CancellationToken
                    // member that throws ObjectDisposedException once its source is disposed.
                    // Register and Task.Delay(_, token) both short-circuit on an already-cancelled
                    // token without touching the source, and JobManager.Dispose() cancels before it
                    // disposes - which is why a Task.Delay here passed even against the pre-fix code.
                    //
                    // Pre-fix, JobManager.Dispose() disposed this job's CancellationTokenSource as
                    // soon as the grace period elapsed, so this threw. Post-fix, disposal is
                    // deferred until the job's own execution completes, so the handle is still
                    // valid here - and already signalled, because cancellation was requested.
                    var signalled = context.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                    Assert.True(signalled, "the job's cancellation handle should already be signalled");
                }
                catch (OperationCanceledException)
                {
                    // Expected once cooperative cancellation is observed; not a failure.
                }
                catch (Exception exception)
                {
                    captured = exception;
                }

                signal.TrySetResult(captured);
                return new { done = true };
            }));

        await using (var client = await ScryClient.ConnectAsync(host.DescriptorPath))
        {
            var start = await client.StartJobAsync(
                "invoke",
                new { registeredOperation = "outlives-shutdown" });
            Assert.True(start.Success, start.Error?.Message);
        }

        var stopwatch = Stopwatch.StartNew();
        await host.DisposeAsync();
        stopwatch.Stop();

        // Disposal must return in bounded time rather than blocking for the still-running job's
        // full duration.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(12), $"Dispose took {stopwatch.Elapsed}.");

        var captured = await RuntimeCompatibility.AwaitWithTimeoutAsync(signal.Task, TimeSpan.FromSeconds(15));
        Assert.Null(captured);
    }

    [Fact]
    public void SessionManager_dispose_does_not_corrupt_a_session_with_an_active_operation()
    {
        var sessions = new SessionManager(
            targetId: "test-target",
            handleLease: TimeSpan.FromMinutes(5),
            sessionLease: TimeSpan.FromMinutes(5),
            maximumPreviewLength: 256,
            maximumHandlesPerSession: 16,
            maximumSessions: 8);

        var session = sessions.Create();

        // Simulate an operation (e.g. a job) that is still in flight when shutdown begins.
        using var operationLease = session.EnterOperation(renewLeaseOnExit: false);
        var reference = session.Lease(new object(), out _);

        sessions.Dispose();

        // The session was in use during shutdown, so it must remain fully functional instead of
        // being cleared/disposed out from under the in-flight operation.
        var resolved = session.Resolve(reference);
        Assert.NotNull(resolved);

        // Simulates a job whose background task finishes encoding its result (leasing a fresh
        // handle for a reference-typed value) after the shutdown grace period has already
        // elapsed. Before the fix this raced with SessionManager.Dispose() clearing/disposing the
        // session, turning a job that should complete cleanly into a spurious "session expired"
        // failure.
        var lateReference = session.Lease(new object(), out _);
        Assert.NotNull(session.Resolve(lateReference));
    }

    [Fact]
    public void SessionManager_dispose_still_disposes_idle_sessions()
    {
        var sessions = new SessionManager(
            targetId: "test-target",
            handleLease: TimeSpan.FromMinutes(5),
            sessionLease: TimeSpan.FromMinutes(5),
            maximumPreviewLength: 256,
            maximumHandlesPerSession: 16,
            maximumSessions: 8);

        var session = sessions.Create();
        var reference = session.Lease(new object(), out _);

        sessions.Dispose();

        var exception = Assert.Throws<ScryOperationException>(() => session.Resolve(reference));
        Assert.Equal("session_expired", exception.Code);
    }
}
