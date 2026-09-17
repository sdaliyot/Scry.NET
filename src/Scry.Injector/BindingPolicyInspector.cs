namespace Scry.Injector;

/// <summary>
/// Pre-flight check for .NET Framework targets, run before anything is injected.
/// <para>
/// The .NET Framework bootstrap path loads the payload into the target's <em>default AppDomain</em>,
/// which means the payload binds under the target application's own assembly binding policy. If the
/// target's configuration file redirects one of the payload's dependencies down to an older version
/// than the payload was built against, the payload loads that older assembly and then fails deep
/// inside itself with a <see cref="MissingMethodException"/> or <see cref="TypeLoadException"/> -
/// inside somebody else's running application.
/// </para>
/// <para>
/// The payload's own <c>AssemblyResolve</c> hook cannot save this case: a binding redirect is
/// applied <em>before</em> <c>AssemblyResolve</c> fires, and that event only runs when a bind
/// fails, not when it succeeds against the wrong version. So the only safe options are to refuse,
/// or to ship a payload whose dependency identities the host cannot name. This refuses.
/// </para>
/// <para>
/// The check is driven off the staged payload's actual dependency versions rather than a hardcoded
/// assembly list, so it stays correct as the payload's dependencies change.
/// </para>
/// </summary>
internal static class BindingPolicyInspector
{
    /// <summary>
    /// Returns a description of the first binding conflict found, or null when the target's
    /// configuration cannot force a downgrade of anything the payload needs. Deliberately
    /// conservative: anything unreadable or unparseable is treated as "no known conflict", because
    /// refusing to attach on the basis of a file we could not understand would be worse than
    /// letting the attach proceed and fail loudly.
    /// </summary>
    public static string? FindConflict(ProcessInspectionResult target, string payloadDirectory)
    {
        if (target.RuntimeFamily != TargetRuntimeFamily.NetFramework)
        {
            // Modern .NET loads the payload into its own component load context, so the target's
            // binding policy does not apply to it.
            return null;
        }

        var configurationPath = TryFindConfigurationPath(target);
        return configurationPath is null
            ? null
            : FindConflict(configurationPath, payloadDirectory);
    }

    /// <summary>
    /// The comparison itself, over a configuration file and a payload directory. Split out from the
    /// live-process lookup so it can be tested without a target to attach to. Forwards to
    /// <see cref="BindingRedirectComparison"/>, which is shared (via a linked source file, not a
    /// project reference) with <c>Scry.Runtime</c>'s AppDomain-hop path - see that type's doc
    /// comment for why.
    /// </summary>
    internal static string? FindConflict(string configurationPath, string payloadDirectory) =>
        BindingRedirectComparison.FindConflict(configurationPath, payloadDirectory);

    private static string? TryFindConfigurationPath(ProcessInspectionResult target)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(target.ProcessId);
            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return null;
            }

            var path = executablePath + ".config";
            return File.Exists(path) ? path : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The process went away, or its main module is not readable from here. Either way
            // there is no configuration to inspect, so let the attach proceed.
            return null;
        }
    }
}
