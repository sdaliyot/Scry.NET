using System.Collections.Concurrent;
using System.Globalization;
using Scry.Contracts;

namespace Scry.Runtime;

internal sealed class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly string _targetId;
    private readonly TimeSpan _handleLease;
    private readonly TimeSpan _sessionLease;
    private readonly int _maximumPreviewLength;
    private readonly int _maximumHandlesPerSession;
    private readonly int _maximumSessions;
    private readonly object _admissionGate = new();
    private readonly Timer _cleanupTimer;

    public SessionManager(
        string targetId,
        TimeSpan handleLease,
        TimeSpan sessionLease,
        int maximumPreviewLength,
        int maximumHandlesPerSession,
        int maximumSessions)
    {
        _targetId = targetId;
        _handleLease = handleLease;
        _sessionLease = sessionLease;
        _maximumPreviewLength = maximumPreviewLength;
        _maximumHandlesPerSession = maximumHandlesPerSession;
        _maximumSessions = maximumSessions;
        _cleanupTimer = new Timer(
            static state => ((SessionManager)state!).RemoveExpired(),
            this,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
    }

    public SessionState Create(string? clientName = null)
    {
        lock (_admissionGate)
        {
            RemoveExpired();
            if (_sessions.Count >= _maximumSessions)
            {
                throw new ScryOperationException(
                    "session_limit_reached",
                    $"The target's limit of {_maximumSessions} concurrent sessions has been reached.");
            }

            while (true)
            {
                var id = RuntimeCompatibility.CreateRandomHex(16);
                var session = new SessionState(
                    _targetId,
                    id,
                    _handleLease,
                    _sessionLease,
                    _maximumPreviewLength,
                    _maximumHandlesPerSession)
                {
                    ClientName = clientName
                };
                if (_sessions.TryAdd(id, session))
                {
                    return session;
                }

                session.Dispose();
            }
        }
    }

    public SessionState Resume(string id, string? clientName = null)
    {
        if (!_sessions.TryGetValue(id, out var session) || !session.TryTouch())
        {
            throw new ScryOperationException("session_not_found", "The requested session does not exist or has expired.");
        }

        // A resumed session keeps whichever name it already had unless the resuming client gave
        // one; this is what lets a background job (which has no connection of its own) still
        // report the name of whoever originally started the session it runs under.
        if (clientName is not null)
        {
            session.ClientName = clientName;
        }

        return session;
    }

    public void Remove(string id)
    {
        if (_sessions.TryGetValue(id, out var existing) &&
            existing.TryDisposeIfIdle() &&
            _sessions.TryRemove(id, out var session))
        {
            session.Dispose();
        }
    }

    private void RemoveExpired()
    {
        foreach (var pair in _sessions)
        {
            pair.Value.RemoveExpiredHandles();
            if (pair.Value.TryExpire() &&
                _sessions.TryRemove(pair.Key, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        foreach (var session in _sessions.Values)
        {
            if (!session.IsInUse)
            {
                session.Dispose();
            }

            // Sessions with an in-flight operation (e.g. a job whose background task outlived the
            // shutdown grace period) are intentionally left undisposed here. Forcibly disposing a
            // session while an operation is still reading/writing its handles would corrupt that
            // operation's state instead of letting it observe cooperative cancellation. Such
            // sessions become unreachable through this manager once cleared below and are
            // collected once the in-flight operation finishes and releases its lease.
        }

        _sessions.Clear();
    }
}

internal sealed class SessionState : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HandleEntry> _handles = new(StringComparer.Ordinal);
    private readonly Dictionary<object, string> _identities =
        new(RuntimeCompatibility.ReferenceComparer);
    private readonly string _targetId;
    private readonly TimeSpan _handleLease;
    private readonly TimeSpan _sessionLease;
    private readonly int _maximumPreviewLength;
    private readonly int _maximumHandles;
    private bool _disposed;
    private int _activeOperations;

    public SessionState(
        string targetId,
        string id,
        TimeSpan handleLease,
        TimeSpan sessionLease,
        int maximumPreviewLength,
        int maximumHandles)
    {
        _targetId = targetId;
        Id = id;
        _handleLease = handleLease;
        _sessionLease = sessionLease;
        _maximumPreviewLength = maximumPreviewLength;
        _maximumHandles = maximumHandles;
        ExpiresAt = DateTimeOffset.UtcNow.Add(sessionLease);
    }

    public string Id { get; }

    /// <summary>
    /// Self-asserted by the connecting client at handshake (<c>HandshakeRequest.ClientName</c>).
    /// A label for the audit log, not an authenticated identity - carried on the session, rather
    /// than only on the connection, so every operation record on this session names the same
    /// caller even across a resumed (non-ephemeral) session.
    /// </summary>
    public string? ClientName { get; set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public bool IsExpired => !IsInUse && ExpiresAt <= DateTimeOffset.UtcNow;

    public bool IsInUse => Volatile.Read(ref _activeOperations) > 0;

    public bool TryTouch()
    {
        lock (_gate)
        {
            if (_disposed || (_activeOperations == 0 && ExpiresAt <= DateTimeOffset.UtcNow))
            {
                return false;
            }

            ExpiresAt = DateTimeOffset.UtcNow.Add(_sessionLease);
            return true;
        }
    }

    public void Touch()
    {
        lock (_gate)
        {
            EnsureActive();
            ExpiresAt = DateTimeOffset.UtcNow.Add(_sessionLease);
        }
    }

    public bool TryExpire()
    {
        lock (_gate)
        {
            if (_disposed || _activeOperations > 0 || ExpiresAt > DateTimeOffset.UtcNow)
            {
                return false;
            }

            _disposed = true;
            return true;
        }
    }

    public bool TryDisposeIfIdle()
    {
        lock (_gate)
        {
            if (_disposed || _activeOperations > 0)
            {
                return false;
            }

            _disposed = true;
            return true;
        }
    }

    public IDisposable EnterOperation(bool renewLeaseOnExit = true)
    {
        lock (_gate)
        {
            EnsureActive();
            Interlocked.Increment(ref _activeOperations);
        }

        return new OperationLease(this, renewLeaseOnExit);
    }

    public ExternalReference Lease(object value, out bool created)
    {
        lock (_gate)
        {
            RemoveExpiredHandlesCore();
            if (_identities.TryGetValue(value, out var existingId) &&
                _handles.TryGetValue(existingId, out var existing))
            {
                existing.ExpiresAt = DateTimeOffset.UtcNow.Add(_handleLease);
                created = false;
                return CreateReference(existingId, existing);
            }

            if (_handles.Count >= _maximumHandles)
            {
                throw new ScryOperationException(
                    "handle_limit_reached",
                    $"The session's limit of {_maximumHandles} handles has been reached.");
            }

            var id = RuntimeCompatibility.CreateRandomHex(16);
            var entry = new HandleEntry(value, DateTimeOffset.UtcNow.Add(_handleLease));
            _handles.Add(id, entry);
            _identities.Add(value, id);
            created = true;
            return CreateReference(id, entry);
        }
    }

    public object Resolve(ExternalReference reference)
    {
        if (!string.Equals(reference.TargetId, _targetId, StringComparison.Ordinal) ||
            !string.Equals(reference.SessionId, Id, StringComparison.Ordinal))
        {
            throw new ScryOperationException(
                "reference_scope_mismatch",
                "The object reference belongs to a different target or session.");
        }

        lock (_gate)
        {
            EnsureActive();
            RemoveExpiredHandlesCore();
            if (!_handles.TryGetValue(reference.HandleId, out var entry))
            {
                throw new ScryOperationException(
                    "handle_not_found",
                    "The object handle does not exist, was released, or its lease expired.");
            }

            entry.ExpiresAt = DateTimeOffset.UtcNow.Add(_handleLease);
            return entry.Value;
        }
    }

    public bool Release(string handleId)
    {
        lock (_gate)
        {
            if (!_handles.TryGetValue(handleId, out var entry))
            {
                return false;
            }

            _handles.Remove(handleId);
            _identities.Remove(entry.Value);
            return true;
        }
    }

    public void RemoveExpiredHandles()
    {
        lock (_gate)
        {
            RemoveExpiredHandlesCore();
        }
    }

    public string Preview(object value)
    {
        var type = value.GetType();
        var preview = type.IsPrimitive || type.IsEnum ||
            type == typeof(string) || type == typeof(decimal) ||
            type == typeof(Guid) || type == typeof(DateTime) ||
            type == typeof(DateTimeOffset) || type == typeof(TimeSpan)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? type.Name
            : type.FullName ?? type.Name;
        return preview.Length <= _maximumPreviewLength
            ? preview
            : preview.Substring(0, _maximumPreviewLength);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _handles.Clear();
            _identities.Clear();
        }
    }

    private ExternalReference CreateReference(string handleId, HandleEntry entry) =>
        new(
            _targetId,
            Id,
            handleId,
            entry.Value.GetType().FullName ?? entry.Value.GetType().Name,
            Preview(entry.Value),
            entry.ExpiresAt);

    private void RemoveExpiredHandlesCore()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var id in _handles.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
        {
            var entry = _handles[id];
            _handles.Remove(id);
            _identities.Remove(entry.Value);
        }
    }

    private void EnsureActive()
    {
        if (_disposed || IsExpired)
        {
            throw new ScryOperationException("session_expired", "The session has expired.");
        }
    }

    private sealed class OperationLease(
        SessionState session,
        bool renewLeaseOnExit) : IDisposable
    {
        private SessionState? _session = session;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _session, null);
            if (owner is not null)
            {
                owner.ExitOperation(renewLeaseOnExit);
            }
        }
    }

    private void ExitOperation(bool renewLease)
    {
        lock (_gate)
        {
            if (Interlocked.Decrement(ref _activeOperations) == 0 && !_disposed && renewLease)
            {
                ExpiresAt = DateTimeOffset.UtcNow.Add(_sessionLease);
            }
        }
    }

    private sealed class HandleEntry(object value, DateTimeOffset expiresAt)
    {
        public object Value { get; } = value;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
    }
}
