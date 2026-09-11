using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Scry.Runtime;

internal static class RuntimeCompatibility
{
    public static byte[] GetRandomBytes(int count)
    {
        var bytes = new byte[count];
        using var generator = RandomNumberGenerator.Create();
        generator.GetBytes(bytes);
        return bytes;
    }

    public static string CreateRandomHex(int byteCount)
    {
        var bytes = GetRandomBytes(byteCount);
        var characters = new char[bytes.Length * 2];
        const string hex = "0123456789abcdef";
        for (var index = 0; index < bytes.Length; index++)
        {
            characters[index * 2] = hex[bytes[index] >> 4];
            characters[(index * 2) + 1] = hex[bytes[index] & 0x0f];
        }

        return new string(characters);
    }

    public static async Task AwaitWithCancellationAsync(Task task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled || task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var cancellation = Task.Delay(Timeout.Infinite, cancellationToken);
        if (await Task.WhenAny(task, cancellation).ConfigureAwait(false) != task)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        await task.ConfigureAwait(false);
    }

    public static async Task AwaitWithTimeoutAsync(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) != task)
        {
            throw new TimeoutException();
        }

        await task.ConfigureAwait(false);
    }

    public static async Task AwaitWithTimeoutAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout, linkedCancellation.Token);
        if (await Task.WhenAny(task, delay).ConfigureAwait(false) == task)
        {
            linkedCancellation.Cancel();
            await task.ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException();
    }

    public static async Task<T> AwaitWithTimeoutAsync<T>(Task<T> task, TimeSpan timeout)
    {
        if (task.IsCompleted)
        {
            return await task.ConfigureAwait(false);
        }

        if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) != task)
        {
            throw new TimeoutException();
        }

        return await task.ConfigureAwait(false);
    }

    public static IEqualityComparer<object> ReferenceComparer { get; } =
        new ObjectReferenceComparer();

    public static bool IsByRefLikeCompatible(this Type type)
    {
#if NETFRAMEWORK
        return type.GetCustomAttributesData()
            .Any(data => data.AttributeType.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute");
#else
        return type.IsByRefLike;
#endif
    }

    private sealed class ObjectReferenceComparer : IEqualityComparer<object>
    {
        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);

        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }
}
