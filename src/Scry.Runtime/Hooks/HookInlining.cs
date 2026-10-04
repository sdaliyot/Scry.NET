using System.Reflection;
using System.Runtime.CompilerServices;
using Scry.Contracts;

namespace Scry.Runtime;

/// <summary>
/// Estimates whether a patch on a method can be bypassed by JIT inlining.
/// <para>
/// The runtime exposes no way to ask whether a caller has already inlined a method: that decision
/// lives inside each caller's compiled code. Observing it live was considered and rejected - on
/// .NET the JIT's inlining events only cover methods compiled after a listener attaches, which
/// misses every caller compiled before an attach, and on .NET Framework they need an out-of-process
/// ETW session. So this reports a deterministic risk from the method's own metadata instead, and
/// callers can combine it with "a hook that never records a call" (see <c>hook.wait</c>) to turn
/// silence into an explained outcome.
/// </para>
/// </summary>
internal static class HookInlining
{
    /// <summary>IL sizes at which the JIT's inliner always considers a method (per its own heuristics).</summary>
    private const int AlwaysInlineIlBytes = 16;

    private const int LikelyInlineIlBytes = 100;

    public static HookInliningRisk Assess(MethodBase method)
    {
        var reasons = new List<string>();
        var implementation = method.GetMethodImplementationFlags();
        if ((implementation & MethodImplAttributes.NoInlining) != 0)
        {
            reasons.Add("The method is marked NoInlining, so no caller can inline it.");
            return new(HookInliningRisks.None, reasons);
        }

        if ((implementation & MethodImplAttributes.NoOptimization) != 0)
        {
            reasons.Add("The method is marked NoOptimization, so the JIT does not inline it.");
            return new(HookInliningRisks.None, reasons);
        }

        MethodBody? body;
        try
        {
            body = method.GetMethodBody();
        }
        catch (Exception exception) when (exception is InvalidOperationException or BadImageFormatException)
        {
            body = null;
        }

        var ilSize = body?.GetILAsByteArray()?.Length;
        var hasExceptionHandling = body is { ExceptionHandlingClauses.Count: > 0 };
        var isVirtual = method is MethodInfo { IsVirtual: true, IsFinal: false } &&
            method.DeclaringType is { IsSealed: false };

        if ((implementation & MethodImplAttributes.AggressiveInlining) != 0)
        {
            reasons.Add("The method is marked AggressiveInlining, which asks the JIT to inline it into callers.");
            return Finish(HookInliningRisks.High, reasons);
        }

        if (isVirtual)
        {
            reasons.Add(
                "The method is virtual and not sealed; calls through a base type or interface are " +
                "dispatched, not inlined, but a call the JIT can devirtualize can be.");
            return new(HookInliningRisks.Low, reasons);
        }

        if (hasExceptionHandling)
        {
            reasons.Add(
                "The method contains exception-handling regions, which the JIT does not inline on " +
                ".NET Framework or .NET 8.");
            return new(HookInliningRisks.Low, reasons);
        }

        if (ilSize is null)
        {
            reasons.Add("The method body could not be inspected, so its size is unknown.");
            return new(HookInliningRisks.Low, reasons);
        }

        if (ilSize <= AlwaysInlineIlBytes)
        {
            reasons.Add(
                $"The IL body is {ilSize} bytes, at or below the {AlwaysInlineIlBytes}-byte size the " +
                "JIT inlines almost unconditionally.");
            return Finish(HookInliningRisks.High, reasons);
        }

        if (ilSize <= LikelyInlineIlBytes)
        {
            reasons.Add(
                $"The IL body is {ilSize} bytes; the JIT inlines methods of this size when it judges " +
                "the call site profitable.");
            return Finish(HookInliningRisks.Medium, reasons);
        }

        reasons.Add($"The IL body is {ilSize} bytes, larger than the JIT usually inlines.");
        return new(HookInliningRisks.Low, reasons);
    }

    private static HookInliningRisk Finish(string risk, List<string> reasons)
    {
#if NETFRAMEWORK
        reasons.Add("Callers already JIT-compiled before the hook was added may have inlined the method.");
#else
        reasons.Add(
            "Callers compiled before the hook was added - including ReadyToRun-precompiled code - may " +
            "have inlined the method, and tiered compilation can inline it into a caller later.");
#endif
        return new(risk, reasons);
    }
}
