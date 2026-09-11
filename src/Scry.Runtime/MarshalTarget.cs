using Scry.Contracts;

namespace Scry.Runtime;

/// <summary>
/// Resolves the optional <c>marshal</c> field shared by execution and the structured operations.
/// Kept in one place so both paths accept exactly the same targets and produce the same errors:
/// a client should not be able to discover that <c>evaluate</c> and <c>get</c> disagree about what
/// <c>"marshal": "ui"</c> means.
/// </summary>
internal static class MarshalTarget
{
    /// <summary>
    /// Returns true when the caller asked to run on the host-nominated thread. Rejects an
    /// unrecognised target, and rejects the UI target on a host with no marshaller - a non-UI
    /// process, or a UI process that never registered an adapter - rather than letting the call
    /// fail later with a cross-thread exception that says nothing about the cause.
    /// </summary>
    public static bool Resolve(string? marshal, ExecutionMarshaller? marshaller)
    {
        if (string.IsNullOrWhiteSpace(marshal))
        {
            return false;
        }

        if (!string.Equals(marshal, ExecutionMarshalTargets.UiThread, StringComparison.OrdinalIgnoreCase))
        {
            throw new ScryOperationException(
                "marshal_target_not_supported",
                $"marshal '{marshal}' is not supported. The only supported target is " +
                $"'{ExecutionMarshalTargets.UiThread}'.");
        }

        if (marshaller is null)
        {
            throw new ScryOperationException(
                "marshal_target_unavailable",
                $"marshal '{ExecutionMarshalTargets.UiThread}' requires a host-registered execution " +
                "marshaller. Register the WPF or Windows Forms adapter, or attach with " +
                "--adapters, to enable it.");
        }

        return true;
    }
}
