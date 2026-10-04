using Scry.Contracts;

namespace Scry.Client;

/// <summary>
/// A hook on one method inside the target, created by <see cref="ScryClient.AddHookAsync"/>.
/// Records are captured in the target; read them with <see cref="ReadAsync"/> or wait for a matching
/// one with <see cref="WaitForCallAsync"/>. Disposing removes the hook and restores the original
/// method.
/// <para>
/// A hook belongs to the session of the client that created it, so it must be used through that
/// same connection or a resumed session. If the session ends - the lease expires, or the endpoint
/// shuts down - the target unpatches the method itself.
/// </para>
/// </summary>
public sealed class ScryHook : IAsyncDisposable
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumWait = TimeSpan.FromMinutes(5);

    private readonly ScryClient _client;
    private int _disposed;

    internal ScryHook(ScryClient client, HookAddResult info)
    {
        _client = client;
        Info = info;
    }

    public HookHandle Handle => Info.Hook;

    /// <summary>What <c>hook.add</c> reported: signature, inlining risk, Harmony state, warnings.</summary>
    public HookAddResult Info { get; }

    /// <summary>Advisories raised when the hook was added; for example a high inlining risk.</summary>
    public IReadOnlyList<string> Warnings => Info.Warnings;

    public HookInliningRisk Inlining => Info.Inlining;

    public Task<HookReadResult> ReadAsync(
        long cursor = 0,
        int limit = 100,
        bool includeReferences = false,
        CancellationToken cancellationToken = default) =>
        _client.ReadHookAsync(new HookReadRequest(Handle, cursor, limit, includeReferences), cancellationToken);

    /// <summary>Like <see cref="ReadAsync"/>, then discards the calls it returned.</summary>
    public Task<HookReadResult> DrainAsync(
        long cursor = 0,
        int limit = 100,
        bool includeReferences = false,
        CancellationToken cancellationToken = default) =>
        _client.DrainHookAsync(new HookReadRequest(Handle, cursor, limit, includeReferences), cancellationToken);

    /// <summary>
    /// Waits for a recorded call that satisfies <paramref name="predicate"/>, evaluated in the target
    /// against the real captured objects with <c>Args</c>, <c>ReturnValue</c>, <c>Exception</c>,
    /// <c>Instance</c> and <c>Call</c> in scope. Reports a timeout in the result instead of throwing.
    /// Pass the previous result's <see cref="HookWaitResult.NextCursor"/> as <paramref name="cursor"/>
    /// to look only at calls recorded since.
    /// </summary>
    public Task<HookWaitResult> TryWaitForCallAsync(
        string? predicate = null,
        TimeSpan? timeout = null,
        long cursor = 0,
        string? marshal = null,
        bool includeReferences = false,
        IReadOnlyList<string>? imports = null,
        IReadOnlyList<string>? references = null,
        CancellationToken cancellationToken = default)
    {
        var wait = timeout ?? DefaultWait;
        if (wait < TimeSpan.Zero || wait > MaximumWait)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                $"A hook wait must be between zero and {MaximumWait.TotalMinutes} minutes.");
        }

        return _client.WaitForHookCallAsync(
            new HookWaitRequest(
                Handle,
                predicate,
                cursor,
                checked((int)Math.Ceiling(wait.TotalMilliseconds)),
                imports,
                references,
                marshal,
                includeReferences),
            cancellationToken);
    }

    /// <summary>
    /// Like <see cref="TryWaitForCallAsync"/> but returns the matching call, and throws
    /// <see cref="TimeoutException"/> - carrying the target's diagnostics, such as an inlining
    /// warning when nothing was ever recorded - if none arrives in time.
    /// </summary>
    public async Task<HookCall> WaitForCallAsync(
        string? predicate = null,
        TimeSpan? timeout = null,
        long cursor = 0,
        string? marshal = null,
        bool includeReferences = false,
        IReadOnlyList<string>? imports = null,
        IReadOnlyList<string>? references = null,
        CancellationToken cancellationToken = default)
    {
        var result = await TryWaitForCallAsync(
                predicate, timeout, cursor, marshal, includeReferences, imports, references, cancellationToken)
            .ConfigureAwait(false);
        if (result.Call is { } call)
        {
            return call;
        }

        throw new TimeoutException(
            $"No call to {Info.Signature} satisfied the predicate within {(timeout ?? DefaultWait).TotalSeconds:0.##} " +
            $"seconds ({result.Evaluated} evaluated, {result.TotalCalls} recorded, {result.DroppedCalls} dropped). " +
            string.Join(" ", result.Diagnostics));
    }

    public Task<HookRemoveResult> RemoveAsync(CancellationToken cancellationToken = default) =>
        _client.RemoveHookAsync(Handle, cancellationToken);

    /// <summary>
    /// Removes the hook. Does not throw if it is already gone or the connection has closed: the
    /// target unpatches on its own when the session ends, so there is nothing left to undo.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await RemoveAsync().ConfigureAwait(false);
        }
        catch (ScryRemoteException exception) when (
            exception.Response.Error?.Code is "hook_not_found" or "hook_scope_mismatch")
        {
        }
        catch (Exception exception) when (
            exception is ObjectDisposedException or System.IO.IOException or ProtocolException or TimeoutException)
        {
        }
    }
}
