namespace Scry.Runtime;

/// <summary>
/// One captured call. Holds strong references to the captured argument, return, exception and
/// instance objects, which is why a buffer is bounded: an entry keeps those objects alive only for
/// as long as it stays buffered.
/// </summary>
internal sealed class HookRecord
{
    public long Sequence { get; set; }

    public DateTime StartedUtc { get; init; }

    public DateTime CompletedUtc { get; init; }

    public int ThreadId { get; init; }

    public bool Threw { get; init; }

    public object?[]? Arguments { get; init; }

    public object? ReturnValue { get; init; }

    public Exception? Exception { get; init; }

    public object? Instance { get; init; }
}

internal readonly struct HookBufferPage(
    IReadOnlyList<HookRecord> records,
    long oldestSequence,
    long nextSequence,
    bool hasMore)
{
    public IReadOnlyList<HookRecord> Records { get; } = records;

    public long OldestSequence { get; } = oldestSequence;

    /// <summary>One past the last record in <see cref="Records"/>, or the effective cursor when empty.</summary>
    public long NextSequence { get; } = nextSequence;

    public bool HasMore { get; } = hasMore;
}

/// <summary>
/// Bounded, thread-safe ring of captured calls. Calls fire on arbitrary target threads, so adding is
/// a short lock and nothing else; when the ring is full the oldest entry is dropped and counted.
/// Sequence numbers start at 0 and never repeat, so a cursor stays meaningful after drops.
/// </summary>
internal sealed class HookBuffer
{
    private readonly object _gate = new();
    private readonly HookRecord?[] _ring;
    private long _oldest;
    private long _next;
    private long _dropped;
    private long _failures;
    private TaskCompletionSource<bool>? _signal;

    public HookBuffer(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _ring = new HookRecord?[capacity];
        Capacity = capacity;
    }

    public int Capacity { get; }

    public long TotalCalls
    {
        get
        {
            lock (_gate)
            {
                return _next;
            }
        }
    }

    public long BufferedCalls
    {
        get
        {
            lock (_gate)
            {
                return _next - _oldest;
            }
        }
    }

    public long DroppedCalls
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    public long CaptureFailures => Interlocked.Read(ref _failures);

    public void RecordFailure() => Interlocked.Increment(ref _failures);

    public void Add(HookRecord record)
    {
        TaskCompletionSource<bool>? waiters;
        lock (_gate)
        {
            record.Sequence = _next;
            _ring[(int)(_next % Capacity)] = record;
            _next++;
            if (_next - _oldest > Capacity)
            {
                _oldest++;
                _dropped++;
            }

            waiters = _signal;
            _signal = null;
        }

        // Outside the lock, and the continuations run asynchronously (see WaitForChange), so a
        // waiter never runs on - or holds up - the target thread that made the call.
        waiters?.TrySetResult(true);
    }

    /// <summary>Records at or after <paramref name="cursor"/>, up to <paramref name="limit"/>.</summary>
    public HookBufferPage Read(long cursor, int limit)
    {
        lock (_gate)
        {
            var effective = Math.Max(Math.Max(cursor, 0), _oldest);
            var records = new List<HookRecord>((int)Math.Min(limit, Math.Max(_next - effective, 0)));
            var sequence = effective;
            while (sequence < _next && records.Count < limit)
            {
                records.Add(_ring[(int)(sequence % Capacity)]!);
                sequence++;
            }

            return new(records, _oldest, sequence, sequence < _next);
        }
    }

    /// <summary>Discards every record up to and including <paramref name="sequence"/>.</summary>
    public void DiscardThrough(long sequence)
    {
        lock (_gate)
        {
            while (_oldest <= sequence && _oldest < _next)
            {
                _ring[(int)(_oldest % Capacity)] = null;
                _oldest++;
            }
        }
    }

    public long OldestSequence
    {
        get
        {
            lock (_gate)
            {
                return _oldest;
            }
        }
    }

    /// <summary>
    /// A task that completes the next time a record is added after <paramref name="knownNext"/>
    /// calls were seen. Already complete if one has been added since.
    /// </summary>
    public Task WaitForChange(long knownNext)
    {
        lock (_gate)
        {
            if (_next > knownNext)
            {
                return Task.CompletedTask;
            }

            _signal ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _signal.Task;
        }
    }
}
