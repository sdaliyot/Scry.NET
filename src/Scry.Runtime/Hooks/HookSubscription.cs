using System.Reflection;

namespace Scry.Runtime;

/// <summary>What a patch needs to know about the hooked method's shape; chooses the patch variants.</summary>
internal readonly struct HookShape(bool isStatic, bool hasReturnValue, bool declaringTypeIsValueType)
{
    public bool IsStatic { get; } = isStatic;

    public bool HasReturnValue { get; } = hasReturnValue;

    /// <summary>
    /// An instance method on a struct cannot be given <c>__instance</c> as an <c>object</c>, so it
    /// is patched like a static method and the instance is not captured.
    /// </summary>
    public bool DeclaringTypeIsValueType { get; } = declaringTypeIsValueType;

    public static HookShape Of(MethodBase method) => new(
        method.IsStatic,
        method is MethodInfo { ReturnType: var returnType } && returnType != typeof(void),
        method.DeclaringType?.IsValueType == true);
}

/// <summary>State a prefix hands to the postfix and finalizer of the same call.</summary>
internal sealed class HookInvocation(
    MethodHookState state,
    HookSubscription[] subscribers,
    object?[]? arguments,
    object? instance)
{
    public MethodHookState State { get; } = state;

    public HookSubscription[] Subscribers { get; } = subscribers;

    public object?[]? Arguments { get; } = arguments;

    public object? Instance { get; } = instance;

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public int ThreadId { get; } = Environment.CurrentManagedThreadId;

    /// <summary>
    /// Set once a postfix has recorded a normal return, so the finalizer (which also runs when the
    /// method returned normally) does not record the same call twice.
    /// </summary>
    public bool Recorded { get; set; }
}

/// <summary>One hook's interest in one method: which values to keep, and where to put them.</summary>
internal sealed class HookSubscription(
    HookBuffer buffer,
    bool captureArguments,
    bool captureReturnValue,
    bool captureException,
    bool captureInstance)
{
    public HookBuffer Buffer { get; } = buffer;

    public bool CaptureArguments { get; } = captureArguments;

    public bool CaptureReturnValue { get; } = captureReturnValue;

    public bool CaptureException { get; } = captureException;

    public bool CaptureInstance { get; } = captureInstance;

    /// <summary>Never throws: a failure to record is counted, not propagated into the target.</summary>
    public void Record(HookInvocation invocation, bool threw, object? returnValue, Exception? exception)
    {
        try
        {
            Buffer.Add(new HookRecord
            {
                StartedUtc = invocation.StartedUtc,
                CompletedUtc = DateTime.UtcNow,
                ThreadId = invocation.ThreadId,
                Threw = threw,
                Arguments = CaptureArguments ? invocation.Arguments : null,
                Instance = CaptureInstance ? invocation.Instance : null,
                ReturnValue = !threw && CaptureReturnValue ? returnValue : null,
                Exception = threw && CaptureException ? exception : null
            });
        }
        catch
        {
            Buffer.RecordFailure();
        }
    }
}

/// <summary>
/// The subscribers of one patched method. Several hooks - from different sessions or sibling
/// endpoints in the same AppDomain - share a single Harmony patch and fan out from here.
/// </summary>
internal sealed class MethodHookState(MethodBase method)
{
    private HookSubscription[] _subscribers = Array.Empty<HookSubscription>();

    public MethodBase Method { get; } = method;

    /// <summary>Copy-on-write, so the hot path reads it without a lock.</summary>
    public HookSubscription[] Subscribers => Volatile.Read(ref _subscribers);

    public void Add(HookSubscription subscription)
    {
        var current = _subscribers;
        var next = new HookSubscription[current.Length + 1];
        Array.Copy(current, next, current.Length);
        next[current.Length] = subscription;
        Volatile.Write(ref _subscribers, next);
    }

    public bool Remove(HookSubscription subscription)
    {
        var current = _subscribers;
        var index = Array.IndexOf(current, subscription);
        if (index < 0)
        {
            return false;
        }

        var next = new HookSubscription[current.Length - 1];
        Array.Copy(current, 0, next, 0, index);
        Array.Copy(current, index + 1, next, index, current.Length - index - 1);
        Volatile.Write(ref _subscribers, next);
        return true;
    }
}
