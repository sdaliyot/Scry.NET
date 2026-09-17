#if NETFRAMEWORK
using System.Text.Json;
using Scry.Contracts;
using Scry.Endpoint;
using Scry.Runtime;

namespace Scry.Injector.Payload;

/// <summary>
/// Registers <c>appdomain.list</c> and <c>appdomain.start</c> on an injected endpoint running in
/// the default AppDomain, so an operator who attached without <c>--appdomain</c> can still discover
/// and reach the process's other AppDomains afterward - the one native injection this process gets
/// is not spent finding out what domains exist.
/// <para>
/// Unlike <see cref="AppDomainHop"/> (the attach-time path), <c>appdomain.start</c> spends nothing
/// on a bad selector - the endpoint it would act on already exists - so it fails cleanly on zero or
/// several matches instead of falling back to anywhere.
/// </para>
/// </summary>
internal static class AppDomainOperationWiring
{
    public static void Apply(EndpointBuilder builder, Func<TargetMetadata> metadataAccessor, Func<string> descriptorPathAccessor)
    {
        builder.RegisterOperation(
            "appdomain.list",
            (_, cancellationToken) => ListAsync(descriptorPathAccessor(), cancellationToken),
            "Lists the AppDomains loaded in this process, and which already host a Scry endpoint.",
            new OperationPolicy { IsReadOnly = true });
        builder.RegisterOperation(
            "appdomain.start",
            (arguments, cancellationToken) =>
                StartAsync(arguments, metadataAccessor().Alias, descriptorPathAccessor(), cancellationToken),
            "Starts a sibling Scry endpoint inside a chosen AppDomain of this same process.",
            new OperationPolicy { RequiresConfirmation = true });
    }

    private static async ValueTask<object?> ListAsync(
        string descriptorPath, CancellationToken cancellationToken)
    {
        var domains = ClrAppDomains.EnumerateDomains();
        var targetsDirectory = Path.GetDirectoryName(descriptorPath)!;
        var processId = System.Diagnostics.Process.GetCurrentProcess().Id;
        var liveDomainKeys = (await TargetDiscovery.FindAsync(targetsDirectory, cancellationToken)
            .ConfigureAwait(false))
            .Where(item => item.Descriptor.Target.ProcessId == processId)
            .Select(item => item.Descriptor.Target.AppDomainId)
            .ToHashSet();

        var result = new
        {
            domains = domains.Select(domain =>
            {
                var isDefault = domain.IsDefaultAppDomain();
                return new
                {
                    id = domain.Id,
                    friendlyName = domain.FriendlyName,
                    isDefault,
                    hasEndpoint = liveDomainKeys.Contains(isDefault ? (int?)null : domain.Id),
                    applicationBase = domain.SetupInformation.ApplicationBase,
                    configurationFile = domain.SetupInformation.ConfigurationFile,
                    shadowCopyFiles = domain.SetupInformation.ShadowCopyFiles
                };
            }).ToArray()
        };
        return JsonSerializer.SerializeToElement(result, ScryJson.Options);
    }

    private static ValueTask<object?> StartAsync(
        JsonElement arguments, string currentAlias, string descriptorPath, CancellationToken cancellationToken)
    {
        var selector = arguments.TryGetProperty("selector", out var selectorElement)
            ? selectorElement.GetString()
            : throw new ScryOperationException("invalid_request", "'selector' is required.");
        var requestedAlias = arguments.TryGetProperty("alias", out var aliasElement)
            ? aliasElement.GetString()
            : null;
        var tcpPort = arguments.TryGetProperty("tcpPort", out var tcpPortElement) &&
            tcpPortElement.ValueKind != JsonValueKind.Null
            ? tcpPortElement.GetInt32()
            : (int?)null;

        var domains = ClrAppDomains.EnumerateDomains();
        var matches = AppDomainSelector.Resolve(selector, domains);
        if (matches.Count == 0)
        {
            throw new ScryOperationException(
                "appdomain_not_found", $"No AppDomain in this process matches '{selector}'.");
        }

        if (matches.Count > 1)
        {
            throw new ScryOperationException(
                "appdomain_ambiguous",
                $"'{selector}' matches {matches.Count} AppDomains " +
                $"({string.Join(", ", matches.Select(domain => domain.FriendlyName))}); " +
                "name the one you want more specifically.");
        }

        var domain = matches[0];
        var targetsDirectory = Path.GetDirectoryName(descriptorPath)!;
        var payloadDirectory = Path.GetDirectoryName(
            typeof(AppDomainOperationWiring).Assembly.Location)!;

        if (domain.IsDefaultAppDomain())
        {
            throw new ScryOperationException(
                "appdomain_already_hosts_this_endpoint",
                "The selector resolved to the default AppDomain, which this endpoint is already " +
                "running in.");
        }

        var configurationPath = domain.SetupInformation.ConfigurationFile;
        if (!string.IsNullOrWhiteSpace(configurationPath) && File.Exists(configurationPath))
        {
            var conflict = BindingRedirectComparison.FindConflict(configurationPath, payloadDirectory);
            if (conflict is not null)
            {
                throw new ScryOperationException("binding_conflict", conflict);
            }
        }

        // A sibling needs its own alias - TargetDiscovery.ResolveAsync throws on an ambiguous
        // alias, and the current endpoint's own alias is definitely already in use.
        var alias = requestedAlias ?? $"{currentAlias}-appdomain-{domain.Id}";

        var bootstrapper = (DomainEndpointBootstrapper)domain.CreateInstanceFromAndUnwrap(
            typeof(DomainEndpointBootstrapper).Assembly.Location,
            typeof(DomainEndpointBootstrapper).FullName!)!;
        bootstrapper.InstallResolver(payloadDirectory);
        var started = bootstrapper.StartEndpoint(alias, tcpPort, targetsDirectory);

        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<object?>(JsonSerializer.SerializeToElement(
            new
            {
                targetId = started[0],
                alias = started[1],
                descriptorPath = started[2],
                appDomainId = domain.Id,
                appDomainFriendlyName = domain.FriendlyName
            },
            ScryJson.Options));
    }
}
#endif
