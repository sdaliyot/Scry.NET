using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Scry;

/// <summary>
/// Reference identity comparer, standing in for <c>ReferenceEqualityComparer</c> (.NET 5+) which the
/// desktop CLR does not provide.
/// <para>
/// Generic, unlike the runtime's non-generic version, because the callers need
/// <c>IEqualityComparer&lt;DependencyObject&gt;</c> and <c>IEqualityComparer&lt;Control&gt;</c>.
/// Compiled into every target so callers need no conditional compilation.
/// </para>
/// </summary>
internal sealed class ReferenceComparer<T> : IEqualityComparer<T>
    where T : class
{
    public static ReferenceComparer<T> Instance { get; } = new();

    public bool Equals(T? left, T? right) => ReferenceEquals(left, right);

    public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
}
