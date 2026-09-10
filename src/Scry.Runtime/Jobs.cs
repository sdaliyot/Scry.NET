using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Scry.Contracts;

namespace Scry.Runtime;

internal sealed class JobManager : IDisposable
{
    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new(StringComparer.Ordinal);
    private readonly OperationDispatcher _dispatcher;
    private readonly string _targetId;
    private readonly TimeSpan _retention;
    private readonly int _maximumJobs;
    private readonly int _maximumLogEntries;
    private readonly int _maximumLogMessageLength;
    private readonly object _admissionGate = new();
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public JobManager(
        OperationDispatcher dispatcher,
        string targetId,
        TimeSpan retention,
        int maximumJobs,
        int maximumLogEntries,
        int maximumLogMessageLength)
    {
        _dispatcher = dispatcher;
        _targetId = targetId;
        _retention = retention;
        _maximumJobs = maximumJobs;
        _maximumLogEntries = maximumLogEntries;
        _maximumLogMessageLength = maximumLogMessageLength;
        _cleanupTimer = new Timer(
            static state => ((JobManager)state!).RemoveExpired(),
            this,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
    }

    public async ValueTask<object> DispatchAsync(
        string operation,
        JsonElement payload,
        SessionState session,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return operation switch
        {
            "job.start" => Start(Parse<JobStartRequest>(payload), session, correlationId),
            "job.status" => Status(Parse<JobQueryRequest>(payload), session),
            "job.wait" => await WaitAsync(Parse<JobWaitRequest>(payload), session, cancellationToken)
                .ConfigureAwait(false),
            "job.cancel" => Cancel(Parse<JobQueryRequest>(payload), session),
            "job.logs" => Logs(Parse<JobLogsRequest>(payload), session),
            _ => throw new ScryOperationException(
                "operation_not_supported",
                $"Operation '{operation}' is not supported.")
        };
    }

    private JobSnapshot Start(
        JobStartRequest request,
        SessionState session,
        string requestCorrelationId)
    {
        if (string.IsNullOrWhiteSpace(request.Operation) ||
            request.Operation is "handshake" ||
            request.Operation.StartsWith("job.", StringComparison.Ordinal))
        {
            throw new ScryOperationException(
                "invalid_request",
                "A job operation must name a non-job protocol operation.");
        }

        if (request.Payload.ValueKind != JsonValueKind.Object)
        {
            throw new ScryOperationException("invalid_request", "A job payload must be an object.");
        }

        JobEntry entry;
        lock (_admissionGate)
        {
            RemoveExpired();
            RemoveOldestCompletedUntilBelowLimit();
            if (_jobs.Count >= _maximumJobs)
            {
                throw new ScryOperationException(
                    "job_limit_reached",
                    $"The target's limit of {_maximumJobs} retained or active jobs has been reached.");
            }

            var jobId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                ? requestCorrelationId
                : request.CorrelationId;
            var lease = session.EnterOperation(renewLeaseOnExit: false);
            entry = new(
                new(_targetId, session.Id, jobId),
                request.Operation,
                correlationId,
                request.Payload.Clone(),
                lease,
                _maximumLogEntries,
                _maximumLogMessageLength);
            if (!_jobs.TryAdd(Key(entry.Handle), entry))
            {
                lease.Dispose();
                throw new ScryOperationException("operation_failed", "Could not allocate a unique job ID.");
            }
        }

        entry.Start((job, token) => ExecuteAsync(job, session, token));
        return entry.Snapshot();
    }

    private JobSnapshot Status(JobQueryRequest request, SessionState session) =>
        Find(request.Job, session).Snapshot();

    private async ValueTask<JobWaitResult> WaitAsync(
        JobWaitRequest request,
        SessionState session,
        CancellationToken cancellationToken)
    {
        if (request.TimeoutMilliseconds is < 0 or > 300000)
        {
            throw new ScryOperationException(
                "invalid_request",
                "timeoutMilliseconds must be between 0 and 300000.");
        }

        var entry = Find(request.Job, session);
        var timedOut = false;
        if (!entry.IsCompleted)
        {
            try
            {
                await entry.Completion.WaitAsync(
                    TimeSpan.FromMilliseconds(request.TimeoutMilliseconds),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                timedOut = true;
            }
        }

        return new(entry.Snapshot(), timedOut);
    }

    private JobSnapshot Cancel(JobQueryRequest request, SessionState session)
    {
        var entry = Find(request.Job, session);
        entry.Cancel();
        return entry.Snapshot();
    }

    private JobLogResult Logs(JobLogsRequest request, SessionState session)
    {
        if (request.Cursor < 0 || request.Limit is < 1 or > 1000)
        {
            throw new ScryOperationException(
                "invalid_request",
                "cursor must be non-negative and limit must be between 1 and 1000.");
        }

        return Find(request.Job, session).ReadLogs(request.Cursor, request.Limit);
    }

    private async Task ExecuteAsync(
        JobEntry entry,
        SessionState session,
        CancellationToken cancellationToken)
    {
        entry.MarkRunning();
        try
        {
            var context = new OperationExecutionContext(
                cancellationToken,
                entry.Handle.JobId,
                entry.CorrelationId,
                entry.Log);
            var result = await _dispatcher.DispatchAsync(
                entry.Operation,
                entry.Payload,
                session,
                context).ConfigureAwait(false);
            entry.Succeed(JsonSerializer.SerializeToElement(result, ScryJson.Options));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            entry.MarkCanceled();
        }
        catch (Exception exception)
        {
            var actual = exception is TargetInvocationException { InnerException: { } inner }
                ? inner
                : exception;
            var code = actual is ScryOperationException operationException
                ? operationException.Code
                : "operation_failed";
            entry.Fail(new(code, actual.Message, ExceptionDetail.FromException(actual)));
        }
    }

    private JobEntry Find(JobHandle? handle, SessionState session)
    {
        RemoveExpired();
        if (handle is null)
        {
            throw new ScryOperationException("invalid_request", "A job handle is required.");
        }

        if (!string.Equals(handle.TargetId, _targetId, StringComparison.Ordinal) ||
            !string.Equals(handle.SessionId, session.Id, StringComparison.Ordinal))
        {
            throw new ScryOperationException(
                "job_scope_mismatch",
                "The job belongs to a different target or session.");
        }

        if (!_jobs.TryGetValue(Key(handle), out var entry))
        {
            throw new ScryOperationException(
                "job_not_found",
                "The job does not exist or its retention period expired.");
        }

        return entry;
    }

    private void RemoveExpired()
    {
        var threshold = DateTimeOffset.UtcNow - _retention;
        foreach (var pair in _jobs)
        {
            if (pair.Value.CompletedAt is { } completedAt &&
                completedAt <= threshold &&
                _jobs.TryRemove(pair.Key, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    private void RemoveOldestCompletedUntilBelowLimit()
    {
        foreach (var entry in _jobs.Values
            .Where(job => job.IsCompleted)
            .OrderBy(job => job.CompletedAt)
            .ThenBy(job => job.Handle.JobId, StringComparer.Ordinal))
        {
            if (_jobs.Count < _maximumJobs)
            {
                break;
            }

            if (_jobs.TryRemove(Key(entry.Handle), out var removed))
            {
                removed.Dispose();
            }
        }
    }

    private static T Parse<T>(JsonElement payload)
    {
        try
        {
            return payload.Deserialize<T>(ScryJson.Options)
                ?? throw new ScryOperationException("invalid_request", "The job request is missing.");
        }
        catch (JsonException exception)
        {
            throw new ScryOperationException("invalid_request", exception.Message);
        }
    }

    private static string Key(JobHandle handle) => $"{handle.SessionId}:{handle.JobId}";

    public void Dispose()
    {
        lock (_admissionGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cleanupTimer.Dispose();
            foreach (var entry in _jobs.Values)
            {
                entry.Cancel();
            }

            try
            {
                Task.WhenAll(_jobs.Values.Select(entry => entry.Completion))
                    .Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Job execution captures operation failures; shutdown only waits for cooperative cancellation.
            }

            foreach (var entry in _jobs.Values)
            {
                if (entry.IsCompleted)
                {
                    // The job finished within the shutdown grace period; safe to dispose now.
                    entry.Dispose();
                }
                else
                {
                    // The job did not observe cancellation within the grace period and its
                    // background task may still be executing. Disposing its CancellationTokenSource
                    // or session lease now could throw ObjectDisposedException from the still-running
                    // operation, or release the session lease while the operation is still using that
                    // session. Defer disposal until the job's own execution actually completes.
                    entry.DisposeWhenCompleted();
                }
            }

            _jobs.Clear();
        }
    }
}

internal sealed class JobEntry : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<JobLogEntry> _logs = new();
    private readonly IDisposable _sessionLease;
    private readonly int _maximumLogEntries;
    private readonly int _maximumLogMessageLength;
    private long _nextCursor;
    private string _state = JobStates.Queued;
    private bool _cancellationRequested;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _completedAt;
    private JsonElement? _result;
    private ProtocolError? _error;
    private int _disposed;

    public JobEntry(
        JobHandle handle,
        string operation,
        string correlationId,
        JsonElement payload,
        IDisposable sessionLease,
        int maximumLogEntries,
        int maximumLogMessageLength)
    {
        Handle = handle;
        Operation = operation;
        CorrelationId = correlationId;
        Payload = payload;
        _sessionLease = sessionLease;
        _maximumLogEntries = maximumLogEntries;
        _maximumLogMessageLength = maximumLogMessageLength;
        CreatedAt = DateTimeOffset.UtcNow;
        Log("information", "Job queued.");
    }

    public JobHandle Handle { get; }

    public string Operation { get; }

    public string CorrelationId { get; }

    public JsonElement Payload { get; }

    public DateTimeOffset CreatedAt { get; }

    public Task Completion => _completion.Task;

    public bool IsCompleted => _completion.Task.IsCompleted;

    public DateTimeOffset? CompletedAt
    {
        get
        {
            lock (_gate)
            {
                return _completedAt;
            }
        }
    }

    public void Start(Func<JobEntry, CancellationToken, Task> execute) =>
        _ = Task.Run(() => execute(this, _cancellation.Token));

    public void MarkRunning()
    {
        lock (_gate)
        {
            _state = JobStates.Running;
            _startedAt = DateTimeOffset.UtcNow;
            AddLogCore("information", "Job started.");
        }
    }

    public void Succeed(JsonElement result) =>
        Complete(JobStates.Succeeded, result, null, "Job succeeded.");

    public void Fail(ProtocolError error) =>
        Complete(JobStates.Failed, null, error, $"Job failed: {error.Message}");

    public void MarkCanceled() =>
        Complete(JobStates.Canceled, null, null, "Job canceled.");

    public void Cancel()
    {
        var cancel = false;
        lock (_gate)
        {
            if (_completion.Task.IsCompleted || _cancellationRequested)
            {
                return;
            }

            _cancellationRequested = true;
            AddLogCore("information", "Cancellation requested.");
            cancel = true;
        }

        if (cancel)
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (AggregateException exception)
            {
                Log("warning", $"A cancellation callback failed: {exception.GetBaseException().Message}");
            }
        }
    }

    public void Log(string level, string message)
    {
        lock (_gate)
        {
            AddLogCore(level, message);
        }
    }

    public JobSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new(
                Handle,
                Operation,
                Handle.JobId,
                CorrelationId,
                _state,
                _cancellationRequested,
                CreatedAt,
                _startedAt,
                _completedAt,
                _result,
                _error);
        }
    }

    public JobLogResult ReadLogs(long cursor, int limit)
    {
        lock (_gate)
        {
            var oldestCursor = _logs.Count == 0 ? _nextCursor : _logs.Peek().Cursor;
            var effectiveCursor = Math.Max(cursor, oldestCursor);
            var entries = new List<JobLogEntry>(Math.Min(limit, _logs.Count));
            var byteBudget = ProtocolConstants.MaximumFrameBytes - (64 * 1024);
            var usedBytes = 0;
            foreach (var entry in _logs.Where(entry => entry.Cursor >= effectiveCursor))
            {
                if (entries.Count == limit)
                {
                    break;
                }

                var entryBytes = JsonSerializer.SerializeToUtf8Bytes(entry, ScryJson.Options).Length;
                if (usedBytes + entryBytes > byteBudget)
                {
                    break;
                }

                entries.Add(entry);
                usedBytes += entryBytes;
            }

            var nextCursor = entries.Count == 0 ? effectiveCursor : entries[^1].Cursor + 1;
            return new(
                Handle,
                cursor,
                oldestCursor,
                nextCursor,
                cursor < oldestCursor,
                _logs.Any(entry => entry.Cursor >= nextCursor),
                entries);
        }
    }

    private void Complete(
        string state,
        JsonElement? result,
        ProtocolError? error,
        string logMessage)
    {
        lock (_gate)
        {
            if (_completion.Task.IsCompleted)
            {
                return;
            }

            _state = state;
            _result = result;
            _error = error;
            _completedAt = DateTimeOffset.UtcNow;
            AddLogCore(state == JobStates.Failed ? "error" : "information", logMessage);
            _completion.TrySetResult();
        }
    }

    private void AddLogCore(string level, string message)
    {
        var boundedLevel = level.Length <= 64 ? level : level[..64];
        var bounded = message.Length <= _maximumLogMessageLength
            ? message
            : message[.._maximumLogMessageLength];
        _logs.Enqueue(new(_nextCursor++, DateTimeOffset.UtcNow, boundedLevel, bounded));
        while (_logs.Count > _maximumLogEntries)
        {
            _logs.Dequeue();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _cancellation.Cancel();
        }
        catch (AggregateException)
        {
        }

        _sessionLease.Dispose();
        _cancellation.Dispose();
    }

    /// <summary>
    /// Defers disposal until the job's background task actually finishes. Used when a manager
    /// shutdown's bounded wait elapses before this job observes cancellation: forcing disposal
    /// while the task is still running could dispose the <see cref="CancellationTokenSource"/> or
    /// session lease out from under it, producing an <see cref="ObjectDisposedException"/> or
    /// releasing the session while it is still in use.
    /// </summary>
    public void DisposeWhenCompleted()
    {
        _ = Completion.ContinueWith(
            static (_, state) => ((JobEntry)state!).Dispose(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
