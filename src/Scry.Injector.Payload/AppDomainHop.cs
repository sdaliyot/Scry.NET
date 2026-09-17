#if NETFRAMEWORK
using Scry.Endpoint;
using Scry.Injector;
using Scry.Runtime;

namespace Scry.Injector.Payload;

/// <summary>
/// Drives an attach-time <c>--appdomain</c> selector: resolve it against this process's actual
/// AppDomains, check the chosen domain's own binding policy, and marshal a
/// <see cref="DomainEndpointBootstrapper"/> into it. Used only from
/// <see cref="InjectedEndpointEntryPoint.Start"/> - the equivalent runtime operation,
/// <c>appdomain.start</c>, has different failure semantics (see <see cref="AppDomainOperationWiring"/>)
/// and does not share this code, deliberately: an attach-time failure here has already spent the
/// process's one native injection and must fall back rather than fail outright, while the runtime
/// operation spends nothing and should simply report the problem.
/// </summary>
internal static class AppDomainHop
{
    /// <summary>
    /// Attempts the hop. Returns true when an endpoint is now running inside the selected domain,
    /// in which case <paramref name="warning"/> is null and the caller has nothing further to do.
    /// Returns false - with <paramref name="warning"/> set only when the selector's outcome is
    /// worth reporting - whenever a caller should fall back to starting locally: the selector
    /// resolved to the default domain (nothing to hop to, and not a failure), matched zero or
    /// several AppDomains, the chosen domain's binding policy conflicted with the payload, or the
    /// hop itself threw.
    /// </summary>
    public static bool TryHop(
        string selector,
        string? alias,
        int? tcpPort,
        string? targetsDirectory,
        string payloadDirectory,
        out string? warning)
    {
        warning = null;

        IReadOnlyList<AppDomain> domains;
        try
        {
            domains = ClrAppDomains.EnumerateDomains();
        }
        catch (Exception exception)
        {
            warning = $"Could not enumerate AppDomains ({exception.Message}); " +
                "started in the default AppDomain instead.";
            return false;
        }

        var matches = AppDomainSelector.Resolve(selector, domains);
        if (matches.Count == 1 && matches[0].IsDefaultAppDomain())
        {
            // The selector legitimately resolved to the default domain - "auto" with no other
            // domain loaded yet, or an explicit id/name naming it. Not a failure: there is simply
            // nothing to hop to, so the ordinary local start below is exactly correct.
            return false;
        }

        if (matches.Count != 1)
        {
            warning = matches.Count == 0
                ? $"--appdomain '{selector}' matched no AppDomain in this process; " +
                  "started in the default AppDomain instead."
                : $"--appdomain '{selector}' matched {matches.Count} AppDomains; " +
                  "started in the default AppDomain instead.";
            return false;
        }

        var domain = matches[0];
        var configurationPath = TryGetConfigurationPath(domain);
        if (configurationPath is not null)
        {
            var conflict = BindingRedirectComparison.FindConflict(configurationPath, payloadDirectory);
            if (conflict is not null)
            {
                warning = $"AppDomain '{domain.FriendlyName}' binding policy conflict: {conflict} " +
                    "Started in the default AppDomain instead.";
                return false;
            }
        }

        try
        {
            var assemblyPath = typeof(DomainEndpointBootstrapper).Assembly.Location;
            var typeName = typeof(DomainEndpointBootstrapper).FullName!;
            var bootstrapper = (DomainEndpointBootstrapper)domain
                .CreateInstanceFromAndUnwrap(assemblyPath, typeName)!;
            bootstrapper.InstallResolver(payloadDirectory);
            bootstrapper.StartEndpoint(alias, tcpPort, targetsDirectory);
            return true;
        }
        catch (Exception exception)
        {
            warning = $"Failed to start the endpoint in AppDomain '{domain.FriendlyName}' " +
                $"({exception.Message}); started in the default AppDomain instead.";
            return false;
        }
    }

    private static string? TryGetConfigurationPath(AppDomain domain)
    {
        try
        {
            var path = domain.SetupInformation.ConfigurationFile;
            return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
#endif
