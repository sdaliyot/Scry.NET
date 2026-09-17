using System.Collections.Concurrent;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Scry.Contracts;

namespace Scry.Runtime;

/// <summary>
/// Delivers an <see cref="AuditRecord"/> to an embedding host's own logging. Called on a
/// background thread, never on the request path, so a slow or throwing handler cannot delay or
/// fail the operation being recorded.
/// </summary>
public delegate void AuditRecordHandler(AuditRecord record);

/// <summary>
/// Records what happened to a target: connections, handshake attempts, operations, and their
/// outcomes. Two destinations, both best-effort - a rolling file (so attach mode, where nothing
/// can configure a sink because the target does not cooperate, is still auditable out of the
/// box) and an optional caller-supplied callback (so an embedding host can route records into its
/// own logging instead of, or as well as, the file).
///
/// <para>
/// Every write is enqueue-only and non-blocking. This matters because some hooks fire from inside
/// the connection-handling path while a request is being served, including on a thread that may
/// currently be the target's own UI thread (a marshalled operation's continuation is not
/// guaranteed to leave that thread just because it used <c>ConfigureAwait(false)</c>) - a
/// synchronous file write there would stall the application for the duration of the write, which
/// is exactly what attach mode is supposed not to do. A single background task drains the queue.
/// </para>
/// </summary>
internal sealed class AuditLog : IAsyncDisposable
{
    /// <summary>Keeps one file from growing without bound; a fresh file starts once this is hit.</summary>
    private const long DefaultMaximumFileBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Bounds the client-controlled strings that reach a record. Without this, an authenticated
    /// client could send an operation name or correlation ID sized against the protocol's own
    /// 8 MB frame limit and burn through the file's rotation budget in a handful of requests.
    /// </summary>
    private const int MaximumFieldLength = 256;

    private readonly ConcurrentQueue<AuditRecord> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _drainTask;
    private readonly AuditRecordHandler? _callback;
    private readonly string? _directory;
    private readonly int _processId;
    private readonly string _instanceId = Guid.NewGuid().ToString("N").Substring(0, 8);

    private readonly long _maximumFileBytes;

    private FileStream? _file;
    private long _fileBytesWritten;
    private int _fileSequence;
    private bool _fileDisabled;

    public AuditLog(
        bool enabled,
        string? directory,
        AuditRecordHandler? callback,
        int processId,
        long maximumFileBytes = DefaultMaximumFileBytes)
    {
        _callback = callback;
        _processId = processId;
        _directory = enabled ? (directory ?? AuditPaths.DirectoryPath) : null;
        _maximumFileBytes = maximumFileBytes;
        _drainTask = enabled || callback is not null
            ? Task.Run(DrainAsync)
            : Task.CompletedTask;
    }

    /// <summary>
    /// Enqueues a record. Never throws and never blocks the caller: the queue has no bound of its
    /// own (rotation bounds the file, not the queue), and a record is simply dropped if the log has
    /// already been permanently disabled by an unwritable path. This is called from inside request
    /// handling, including from within existing <c>catch</c> blocks - a throwing implementation
    /// here would replace the exception the caller is already handling.
    /// </summary>
    public void Write(AuditRecord record)
    {
        if (_drainTask.IsCompleted)
        {
            // Nothing is draining (audit is fully off, or already disposed) - drop rather than
            // grow the queue forever.
            return;
        }

        _queue.Enqueue(record);
        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Bounds a client-controlled string before it reaches a record.</summary>
    public static string? Bound(string? value) =>
        value is null
            ? null
            : value.Length <= MaximumFieldLength ? value : value.Substring(0, MaximumFieldLength);

    /// <summary>
    /// The host's Windows user name, captured once. Over the named pipe this is meaningful as an
    /// authenticated fact about the caller: the pipe accepts only current-user connections
    /// (<c>PipeOptions.CurrentUserOnly</c> on .NET 9, an equivalent DACL on .NET Framework - see
    /// <c>RuntimeHost.CreatePipe</c>), so every pipe connection is guaranteed to be this user, not
    /// merely likely to be. Over a TCP connection there is no such guarantee - a loopback socket has
    /// no DACL equivalent, so this value still identifies the <em>host process's</em> user but says
    /// nothing about who connected; only the capability token does that. See
    /// <see cref="AuditRecord.Transport"/> to tell which applies to a given record.
    /// </summary>
    public static string? CurrentHostUser()
    {
        try
        {
            // Scry.NET is Windows-only end to end (named pipes, PipeSecurity/DACLs elsewhere in
            // this class), but targets plain net8.0 rather than net8.0-windows, so the platform
            // analyzer sees this as reachable from an unconstrained TFM. It is not.
#pragma warning disable CA1416
            return WindowsIdentity.GetCurrent().Name;
#pragma warning restore CA1416
        }
        catch (Exception exception) when (
            exception is SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns a request payload into the safe, bounded subset of it worth recording, per operation.
    /// Deliberately an allowlist rather than a denylist: a denylist would silently start leaking the
    /// next field added to the protocol, where an allowlist simply omits it until someone decides it
    /// belongs. The one rule that matters most: a <c>set</c> value and <c>invoke</c> arguments are
    /// never recorded, even in summary - only their shape (kind, length, count). This project's own
    /// README uses <c>set-password.json</c> as its worked example, so the value is the actual secret
    /// here, not the submitted C# source.
    /// </summary>
    public static string? DescribePayload(string operation, JsonElement payload)
    {
        try
        {
            switch (operation)
            {
                case "evaluate" or "execute" or "wait" or "assert":
                    {
                        if (!payload.TryGetProperty("source", out var source) ||
                            source.ValueKind != JsonValueKind.String)
                        {
                            return null;
                        }

                        var text = source.GetString() ?? string.Empty;
                        string hash;
                        using (var sha256 = SHA256.Create())
                        {
                            hash = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(text)))
                                .Replace("-", string.Empty)
                                .ToLowerInvariant();
                        }

                        var marshal = payload.TryGetProperty("marshal", out var marshalValue) &&
                            marshalValue.ValueKind == JsonValueKind.String
                            ? marshalValue.GetString()
                            : null;
                        return marshal is null
                            ? $"sourceLength={text.Length};sourceSha256={hash}"
                            : $"sourceLength={text.Length};sourceSha256={hash};marshal={marshal}";
                    }

                case "get" or "set" or "invoke" or "inspect" or "enumerate":
                    var subject = payload.TryGetProperty("root", out var root) &&
                        root.ValueKind == JsonValueKind.String
                        ? $"root={root.GetString()}"
                        : payload.TryGetProperty("reference", out _)
                            ? "reference=<leased>"
                            : null;
                    var member = payload.TryGetProperty("member", out var memberValue) &&
                        memberValue.ValueKind == JsonValueKind.String
                        ? $"member={memberValue.GetString()}"
                        : payload.TryGetProperty("registeredOperation", out var registered) &&
                            registered.ValueKind == JsonValueKind.String
                            ? $"registeredOperation={registered.GetString()}"
                            : null;
                    var shape = operation == "set" && payload.TryGetProperty("value", out var value)
                        ? $"valueKind={value.ValueKind}"
                        : operation == "invoke" && payload.TryGetProperty("arguments", out var arguments)
                            ? arguments.ValueKind == JsonValueKind.Array
                                ? $"argumentCount={arguments.GetArrayLength()}"
                                : $"argumentsKind={arguments.ValueKind}"
                            : null;
                    return string.Join(";", new[] { subject, member, shape }.Where(part => part is not null));

                case "load-assembly":
                    return payload.TryGetProperty("path", out var path) &&
                        path.ValueKind == JsonValueKind.String
                        ? $"path={Bound(path.GetString())}"
                        : null;

                default:
                    return null;
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or FormatException)
        {
            // Never let a summarisation quirk (an unexpected payload shape) take down the request
            // it was only meant to describe.
            return null;
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            try
            {
                await _signal.WaitAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!_queue.TryDequeue(out var record))
            {
                continue;
            }

            TryInvokeCallback(record);
            TryWriteToFile(record);
        }

        // Drain whatever is left after cancellation, so a burst right before shutdown is not
        // silently lost.
        while (_queue.TryDequeue(out var remaining))
        {
            TryInvokeCallback(remaining);
            TryWriteToFile(remaining);
        }

        _file?.Dispose();
    }

    private void TryInvokeCallback(AuditRecord record)
    {
        if (_callback is null)
        {
            return;
        }

        try
        {
            _callback(record);
        }
        catch (Exception)
        {
            // The callback is the embedding host's own code; a throw there is their bug, not
            // something this log can act on beyond not letting it interrupt the drain loop.
        }
    }

    private void TryWriteToFile(AuditRecord record)
    {
        if (_directory is null || _fileDisabled)
        {
            return;
        }

        try
        {
            var line = JsonSerializer.SerializeToUtf8Bytes(record, AuditJson.Options);
            EnsureFileFor(line.Length);
            _file!.Write(line, 0, line.Length);
            _file.WriteByte((byte)'\n');
            _fileBytesWritten += line.Length + 1;
            _file.Flush();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
            SecurityException)
        {
            // An unwritable path does not heal itself (permissions, a missing drive); stop trying
            // rather than pay this cost on every subsequent record for the life of the target.
            _fileDisabled = true;
            _file?.Dispose();
            _file = null;
        }
    }

    private void EnsureFileFor(int incomingBytes)
    {
        if (_file is not null && _fileBytesWritten + incomingBytes <= _maximumFileBytes)
        {
            return;
        }

        _file?.Dispose();
        Directory.CreateDirectory(_directory!);
        var path = AuditPaths.GetLogPath(_directory!, _processId, _instanceId, _fileSequence++);
        _file = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 4096,
            FileOptions.None);
        _fileBytesWritten = 0;
    }

    public async ValueTask DisposeAsync()
    {
#if NETFRAMEWORK
        _stopping.Cancel();
#else
        await _stopping.CancelAsync().ConfigureAwait(false);
#endif
        try
        {
            await RuntimeCompatibility.AwaitWithTimeoutAsync(_drainTask, TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or TimeoutException)
        {
        }

        _stopping.Dispose();
        _signal.Dispose();
    }
}
