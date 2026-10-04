using Microsoft.CodeAnalysis.Scripting;

namespace Scry.Runtime;

/// <summary>Metadata about the call a <c>hook.wait</c> predicate is being evaluated against.</summary>
public sealed class HookCallInfo
{
    internal HookCallInfo(long sequence, int threadId, DateTime startedUtc, DateTime completedUtc, bool threw)
    {
        Sequence = sequence;
        ThreadId = threadId;
        StartedUtc = startedUtc;
        CompletedUtc = completedUtc;
        Threw = threw;
    }

    public long Sequence { get; }

    public int ThreadId { get; }

    public DateTime StartedUtc { get; }

    public DateTime CompletedUtc { get; }

    public bool Threw { get; }
}

/// <summary>
/// What a <c>hook.wait</c> predicate can see, by bare name: <c>Args</c>, <c>ReturnValue</c>,
/// <c>Exception</c>, <c>Instance</c> and <c>Call</c>. These are the real captured objects, not
/// projections. A value the hook was not asked to capture is null.
/// </summary>
public sealed class HookCallGlobals
{
    internal HookCallGlobals(
        object?[] args,
        object? returnValue,
        Exception? exception,
        object? instance,
        HookCallInfo call)
    {
        Args = args;
        ReturnValue = returnValue;
        Exception = exception;
        Instance = instance;
        Call = call;
    }

    public object?[] Args { get; }

    public object? ReturnValue { get; }

    public Exception? Exception { get; }

    public object? Instance { get; }

    public HookCallInfo Call { get; }
}

/// <summary>A predicate compiled once for a <c>hook.wait</c> and evaluated against many calls.</summary>
internal sealed class HookPredicate(Script<object?> script)
{
    public Script<object?> Script { get; } = script;
}
