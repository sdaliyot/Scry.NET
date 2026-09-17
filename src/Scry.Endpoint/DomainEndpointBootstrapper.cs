#if NETFRAMEWORK
namespace Scry.Endpoint;

/// <summary>
/// Marshalled into a chosen, non-default AppDomain via
/// <see cref="AppDomain.CreateInstanceFromAndUnwrap(string, string)"/> so an endpoint can be started
/// there instead of in the process's default domain - see the AppDomain-targeting design
/// (<c>Scry.Runtime.ClrAppDomains</c> and <c>Scry.Runtime.AppDomainSelector</c>) for the mechanism
/// that picks the domain and drives this class from the default domain.
/// <para>
/// <b>Its own type surface must stay minimal</b> - no field, property, or method parameter/return
/// type naming anything from another Scry assembly, and nothing that touches Roslyn. Activating this
/// object across the domain boundary is itself an assembly load into the *new* domain, resolved by
/// that domain's own probing rules, which know nothing about the payload directory. Every later
/// reference this type makes to <c>Scry.Runtime</c>, <c>Scry.Contracts</c>, or Roslyn types resolves
/// the same way - so <see cref="InstallResolver"/> must run, and must succeed, before
/// <see cref="StartEndpoint"/> ever touches <see cref="EndpointHost"/>. Only <see cref="string"/>
/// and nullable primitives cross this type's public surface, for exactly that reason.
/// </para>
/// </summary>
public sealed class DomainEndpointBootstrapper : MarshalByRefObject
{
    private static EndpointHost? _host;
    private string? _payloadDirectory;

    /// <summary>
    /// Installs an <see cref="AppDomain.AssemblyResolve"/> handler resolving from
    /// <paramref name="payloadDirectory"/>, mirroring the default-domain path's own resolver.
    /// References nothing beyond <see cref="System.Reflection.Assembly"/> and
    /// <see cref="System.IO.Path"/>, so calling this is safe before anything else is staged. Must be
    /// the first call made against a given instance.
    /// </summary>
    public void InstallResolver(string payloadDirectory)
    {
        _payloadDirectory = payloadDirectory;
        AppDomain.CurrentDomain.AssemblyResolve += ResolvePayloadAssembly;
    }

    /// <summary>
    /// Starts the endpoint in this domain and returns
    /// <c>[targetId, alias, descriptorPath]</c> - a plain string array rather than a record, so the
    /// cross-domain return value needs no shared type identity beyond <see cref="string"/> itself.
    /// Throws <see cref="InvalidOperationException"/> if <see cref="InstallResolver"/> was not
    /// already called, and whatever <see cref="EndpointHost.Start"/> itself throws otherwise.
    /// </summary>
    /// <remarks>
    /// Desktop adapters are deliberately not supported here: wiring one needs
    /// <c>Scry.Injector.Payload</c>'s reflection-based adapter loader, and that assembly cannot be a
    /// dependency of <c>Scry.Endpoint</c> without creating a project cycle. A UI adapter inside a
    /// non-default AppDomain (an ASP.NET application domain, most commonly) is also a rare enough
    /// combination that the restriction costs little.
    /// </remarks>
    public string[] StartEndpoint(string? alias, int? tcpPort, string? targetsDirectory)
    {
        if (_payloadDirectory is null)
        {
            throw new InvalidOperationException(
                $"{nameof(InstallResolver)} must be called before {nameof(StartEndpoint)}.");
        }

        var options = alias is null
            ? new EndpointOptions { TcpPort = tcpPort, TargetsDirectory = targetsDirectory }
            : new EndpointOptions
            {
                Alias = alias,
                TcpPort = tcpPort,
                TargetsDirectory = targetsDirectory
            };
        _host = EndpointHost.Start(options: options);
        return
        [
            _host.Metadata.TargetId,
            _host.Metadata.Alias,
            _host.DescriptorPath
        ];
    }

    private System.Reflection.Assembly? ResolvePayloadAssembly(
        object? sender, ResolveEventArgs arguments)
    {
        var name = new System.Reflection.AssemblyName(arguments.Name).Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var path = System.IO.Path.Combine(_payloadDirectory!, name + ".dll");
        return System.IO.File.Exists(path) ? System.Reflection.Assembly.LoadFrom(path) : null;
    }
}
#endif
