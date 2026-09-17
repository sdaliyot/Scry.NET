namespace Scry.MultiDomainAttachTarget.Plugin;

/// <summary>
/// Exists only to be findable. <c>Scry.MultiDomainAttachTarget</c> loads this assembly into a
/// second AppDomain and never references it from its own (default-domain) code, so this type is
/// the thing an AppDomain-targeted <c>find-types</c> is supposed to see - and an ordinary,
/// default-domain attach is supposed not to.
/// </summary>
public sealed class PluginMarker
{
    public string Greeting => "Hello from the plugin AppDomain.";
}
