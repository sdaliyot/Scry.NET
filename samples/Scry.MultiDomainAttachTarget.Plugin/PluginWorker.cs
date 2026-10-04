using System.Runtime.CompilerServices;
using System.Threading;

namespace Scry.MultiDomainAttachTarget.Plugin;

/// <summary>
/// Gives method hooks something real to record inside the plugin's AppDomain, modelled on a plugin
/// that pushes data through private, non-virtual methods called on <c>this</c>. Started from
/// <see cref="PluginMarker"/>'s constructor, so it only ever runs inside the second AppDomain.
/// Every method is <c>NoInlining</c> so the hook, not the JIT, decides whether a call is seen.
/// </summary>
public sealed class PluginWorker
{
    private int _next;

    public void Start() =>
        new Thread(Loop) { IsBackground = true, Name = "PluginWorker" }.Start();

    private void Loop()
    {
        while (true)
        {
            var number = Interlocked.Increment(ref _next);
            PushAndAwait(new[] { "g" + number });
            PushAndAwait(number);
            Add(number, 1);
            try
            {
                if (number % 5 == 0)
                {
                    Fail("plugin " + number);
                }
            }
            catch (System.InvalidOperationException)
            {
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>True for an even <c>g&lt;n&gt;</c>, so a predicate can tell calls apart by result.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool PushAndAwait(string[] ids) => int.Parse(ids[0].Substring(1)) % 2 == 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool PushAndAwait(int count) => count % 2 == 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Add(int left, int right) => left + right;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Fail(string message) => throw new System.InvalidOperationException(message);
}
