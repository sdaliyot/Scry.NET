using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Scry.Runtime;

/// <summary>
/// The only code that touches Harmony. Every entry point is <see cref="MethodImplOptions.NoInlining"/>
/// and nothing outside this class names a Harmony type, so <c>0Harmony.dll</c> is first resolved when
/// the first hook is added - never at attach time, and never in a host that does not use hooks.
/// <para>
/// One static instance per AppDomain. Several hooks on the same method - from different sessions, or
/// from sibling endpoints that share the AppDomain - share a single patch: the first subscriber
/// applies it, the last one's removal unpatches it, and each call fans out to every subscriber.
/// </para>
/// <para>
/// The patches are record-only. They never change arguments, results or exceptions, never throw into
/// the target (everything is caught and counted) and guard against recursing into themselves, which
/// matters when the hooked method is one the capture code itself calls.
/// </para>
/// </summary>
internal static class HookPatcher
{
    private const string HarmonyId = "scry.net.hooks";

    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<IntPtr, MethodHookState> States = new();
    private static Harmony? s_harmony;

    [ThreadStatic]
    private static bool t_capturing;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Assembly LoadedAssembly() => typeof(Harmony).Assembly;

    /// <summary>The Harmony version this runtime was compiled against.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Version CompiledVersion() =>
        typeof(HookPatcher).Assembly.GetReferencedAssemblies()
            .FirstOrDefault(name => string.Equals(name.Name, "0Harmony", StringComparison.Ordinal))
            ?.Version ?? new Version(0, 0);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Subscribe(MethodBase method, HookSubscription subscription)
    {
        lock (Gate)
        {
            var key = method.MethodHandle.Value;
            var created = false;
            if (!States.TryGetValue(key, out var state))
            {
                state = new MethodHookState(method);
                States[key] = state;
                created = true;
            }

            state.Add(subscription);
            if (!created)
            {
                return;
            }

            try
            {
                Apply(state);
            }
            catch
            {
                state.Remove(subscription);
                States.TryRemove(key, out _);
                throw;
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Unsubscribe(MethodBase method, HookSubscription subscription)
    {
        lock (Gate)
        {
            var key = method.MethodHandle.Value;
            if (!States.TryGetValue(key, out var state) || !state.Remove(subscription))
            {
                return;
            }

            if (state.Subscribers.Length != 0)
            {
                return;
            }

            States.TryRemove(key, out _);
            // Removes only what this id applied; another Harmony user's patches on the same method
            // are rebuilt and left in place.
            s_harmony?.Unpatch(method, HarmonyPatchType.All, HarmonyId);
        }
    }

    /// <summary>Owners of patches on <paramref name="method"/> other than this class.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IReadOnlyList<string> OtherOwners(MethodBase method)
    {
        var info = Harmony.GetPatchInfo(method);
        return info is null
            ? Array.Empty<string>()
            : info.Owners.Where(owner => !string.Equals(owner, HarmonyId, StringComparison.Ordinal)).ToArray();
    }

    private static void Apply(MethodHookState state)
    {
        var shape = HookShape.Of(state.Method);
        var instanceCapable = !shape.IsStatic && !shape.DeclaringTypeIsValueType;
        var harmony = s_harmony ??= new Harmony(HarmonyId);
        harmony.Patch(
            state.Method,
            prefix: Method(instanceCapable ? nameof(PrefixInstance) : nameof(PrefixStatic)),
            postfix: Method(shape.HasReturnValue ? nameof(PostfixValue) : nameof(PostfixVoid)),
            finalizer: Method(nameof(Finalizer)));
    }

    // Last on every kind: the prefix sees the arguments the original will actually receive, and the
    // postfix the result other patches have already settled on.
    private static HarmonyMethod Method(string name) =>
        new(typeof(HookPatcher).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!)
        {
            priority = Priority.Last
        };

    public static void PrefixInstance(
        object __instance,
        object[] __args,
        MethodBase __originalMethod,
        out HookInvocation? __state) =>
        Begin(__originalMethod, __args, __instance, out __state);

    public static void PrefixStatic(
        object[] __args,
        MethodBase __originalMethod,
        out HookInvocation? __state) =>
        Begin(__originalMethod, __args, null, out __state);

    public static void PostfixValue(object __result, HookInvocation? __state) =>
        Complete(__state, threw: false, __result, null);

    public static void PostfixVoid(HookInvocation? __state) =>
        Complete(__state, threw: false, null, null);

    // Returns void, so the original exception continues to propagate unchanged. Also runs after a
    // normal return, where there is nothing left to record.
    public static void Finalizer(Exception? __exception, HookInvocation? __state)
    {
        if (__exception is not null)
        {
            Complete(__state, threw: true, null, __exception);
        }
    }

    private static void Begin(
        MethodBase original,
        object?[] arguments,
        object? instance,
        out HookInvocation? state)
    {
        state = null;
        if (t_capturing)
        {
            return;
        }

        t_capturing = true;
        try
        {
            if (States.TryGetValue(original.MethodHandle.Value, out var methodState))
            {
                var subscribers = methodState.Subscribers;
                if (subscribers.Length != 0)
                {
                    state = new HookInvocation(methodState, subscribers, arguments, instance);
                }
            }
        }
        catch
        {
            state = null;
        }
        finally
        {
            t_capturing = false;
        }
    }

    private static void Complete(
        HookInvocation? invocation,
        bool threw,
        object? returnValue,
        Exception? exception)
    {
        if (invocation is null || invocation.Recorded || t_capturing)
        {
            return;
        }

        if (!threw)
        {
            invocation.Recorded = true;
        }

        t_capturing = true;
        try
        {
            foreach (var subscription in invocation.Subscribers)
            {
                subscription.Record(invocation, threw, returnValue, exception);
            }
        }
        catch
        {
            foreach (var subscription in invocation.Subscribers)
            {
                subscription.Buffer.RecordFailure();
            }
        }
        finally
        {
            t_capturing = false;
        }
    }
}
