using System.Reflection;
using System.Xml.Linq;

namespace Scry.Injector;

/// <summary>
/// The pure half of the binding-policy pre-flight: given a <c>.config</c> file and a directory of
/// staged dependency assemblies, says whether the configuration would force one of those
/// dependencies down to an older version than what is actually staged. No process, no target - so
/// it is testable on its own, and safe to run inside a target process against a domain's own
/// configuration file, not only from the injector against a target's main module's config.
/// <para>
/// Physically shared, not merely similar: this file is compiled into both <c>Scry.Injector</c>
/// (its original home, used by <c>BindingPolicyInspector</c> before a process is touched) and
/// <c>Scry.Runtime</c> (linked in via the project file, used again at AppDomain-hop time - see
/// <c>AppDomainSelector</c>'s doc comments and the AppDomain-targeting design). <c>Scry.Runtime</c>
/// cannot reference <c>Scry.Injector</c> without pulling the whole attach/injection machinery into
/// every embedding host, which is why this is a linked file rather than a project reference.
/// </para>
/// </summary>
internal static class BindingRedirectComparison
{
    /// <summary>
    /// Returns a description of the first binding conflict found, or null when
    /// <paramref name="configurationPath"/> cannot force a downgrade of anything staged in
    /// <paramref name="payloadDirectory"/>. Deliberately conservative: anything unreadable or
    /// unparseable is treated as "no known conflict" - refusing on the basis of a file we could not
    /// understand would be worse than proceeding and failing loudly.
    /// </summary>
    public static string? FindConflict(string configurationPath, string payloadDirectory)
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
                    $"The configuration ('{configurationPath}') redirects " +
                    $"'{dependency.Key}' to version {redirect.NewVersion}, but the payload " +
                    $"requires {dependency.Value}. Loading would bind the older assembly and fail " +
                    "deep inside the payload. A binding redirect is applied before AssemblyResolve " +
                    "runs, so the payload cannot recover from this itself.";
            }
        }

        return null;
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
    /// Reads the assembly versions actually staged in <paramref name="payloadDirectory"/>. Uses
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
