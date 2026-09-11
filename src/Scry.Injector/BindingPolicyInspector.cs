using System.Reflection;
using System.Xml.Linq;

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
    /// live-process lookup so it can be tested without a target to attach to.
    /// </summary>
    internal static string? FindConflict(string configurationPath, string payloadDirectory)
    {
        IReadOnlyList<BindingRedirect> redirects;
        try
        {
            redirects = ReadRedirects(configurationPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }

        if (redirects.Count == 0)
        {
            return null;
        }

        foreach (var dependency in ReadPayloadDependencies(payloadDirectory))
        {
            var redirect = redirects.FirstOrDefault(candidate => string.Equals(
                candidate.Name,
                dependency.Key,
                StringComparison.OrdinalIgnoreCase));
            if (redirect is null || redirect.NewVersion is null)
            {
                continue;
            }

            // Only a redirect that lands *below* what the payload carries is a problem, and only
            // when the payload's version is actually inside the redirected range.
            if (redirect.NewVersion < dependency.Value &&
                redirect.Covers(dependency.Value))
            {
                return
                    $"The target's configuration ('{configurationPath}') redirects " +
                    $"'{dependency.Key}' to version {redirect.NewVersion}, but the injected " +
                    $"payload requires {dependency.Value}. Injecting would load the older " +
                    "assembly into the payload and fail inside the target process. A binding " +
                    "redirect is applied before AssemblyResolve runs, so the payload cannot " +
                    "recover from this itself.";
            }
        }

        return null;
    }

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

    private static IReadOnlyList<BindingRedirect> ReadRedirects(string configurationPath)
    {
        var document = XDocument.Load(configurationPath);
        var redirects = new List<BindingRedirect>();
        foreach (var dependent in document.Descendants()
            .Where(element => element.Name.LocalName == "dependentAssembly"))
        {
            var identity = dependent.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "assemblyIdentity");
            var redirect = dependent.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "bindingRedirect");
            var name = identity?.Attribute("name")?.Value;
            if (name is null || redirect is null)
            {
                continue;
            }

            redirects.Add(new BindingRedirect(
                name,
                ParseRange(redirect.Attribute("oldVersion")?.Value),
                ParseVersion(redirect.Attribute("newVersion")?.Value)));
        }

        return redirects;
    }

    /// <summary>
    /// Reads the assembly versions actually staged in the payload directory. Uses
    /// <see cref="AssemblyName.GetAssemblyName"/> so nothing is loaded into this process.
    /// </summary>
    private static IReadOnlyDictionary<string, Version> ReadPayloadDependencies(string payloadDirectory)
    {
        var versions = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(payloadDirectory))
        {
            return versions;
        }

        foreach (var file in Directory.EnumerateFiles(payloadDirectory, "*.dll"))
        {
            try
            {
                var name = AssemblyName.GetAssemblyName(file);
                if (name.Name is { Length: > 0 } simpleName && name.Version is { } version)
                {
                    versions[simpleName] = version;
                }
            }
            catch (Exception exception) when (
                exception is BadImageFormatException or FileLoadException or IOException)
            {
                // Native or unreadable file next to the payload; nothing to compare.
            }
        }

        return versions;
    }

    private static Version? ParseVersion(string? value) =>
        Version.TryParse(value, out var parsed) ? parsed : null;

    private static (Version? Low, Version? High) ParseRange(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var parts = value!.Split('-');
        return parts.Length == 2
            ? (ParseVersion(parts[0]), ParseVersion(parts[1]))
            : (ParseVersion(parts[0]), ParseVersion(parts[0]));
    }

    private sealed record BindingRedirect(
        string Name,
        (Version? Low, Version? High) OldVersion,
        Version? NewVersion)
    {
        /// <summary>
        /// True when the redirect's oldVersion range would capture <paramref name="version"/>. An
        /// unparseable or absent range is treated as covering everything, which is the usual
        /// intent of a redirect and keeps the check from missing a real conflict.
        /// </summary>
        public bool Covers(Version version) =>
            (OldVersion.Low is null || version >= OldVersion.Low) &&
            (OldVersion.High is null || version <= OldVersion.High);
    }
}
