using System.Runtime.CompilerServices;

namespace Scry.AttachTarget;

/// <summary>
/// Gives method hooks something real to record in an unmodified target. A background thread keeps
/// calling private, non-virtual instance methods through <c>this</c> - the case a proxy cannot
/// intercept - plus an overload, a static method and one that throws. Every method is
/// <c>NoInlining</c> so the hook, not the JIT, decides whether a call is seen.
/// </summary>
public sealed class HookProbe
{
    private int _next;

    public void Start() =>
        new Thread(Loop) { IsBackground = true, Name = "HookProbe" }.Start();

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
                    Fail("probe " + number);
                }
            }
            catch (InvalidOperationException)
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
    private void Fail(string message) => throw new InvalidOperationException(message);
}
