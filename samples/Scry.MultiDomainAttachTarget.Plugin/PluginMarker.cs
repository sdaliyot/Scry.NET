namespace Scry.MultiDomainAttachTarget.Plugin;

/// <summary>
/// Exists to be findable. <c>Scry.MultiDomainAttachTarget</c> loads this assembly into a
/// second AppDomain and never references it from its own (default-domain) code, so this type is
/// the thing an AppDomain-targeted <c>find-types</c> is supposed to see - and an ordinary,
/// default-domain attach is supposed not to. Constructing it also starts a
/// <see cref="PluginWorker"/>, which is what gives method hooks calls to record in that domain.
/// </summary>
public sealed class PluginMarker
{
    public PluginMarker()
    {
        new PluginWorker().Start();
    }

    public string Greeting => "Hello from the plugin AppDomain.";
}
