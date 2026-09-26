using System.Reflection;
using Scry.Endpoint;

namespace Scry.Injector.Payload;

/// <summary>
/// Wires an optional desktop adapter inside an injected target, selected by the attach command's
/// <c>--adapters</c> flag.
/// <para>
/// Without an adapter an injected endpoint only has the framework-neutral surface: no
/// <c>wpf.*</c>/<c>winforms.*</c> operations, and no execution marshaller, so
/// <c>evaluate</c>/<c>execute</c> cannot use <c>"marshal": "ui"</c> and therefore cannot touch a
/// <c>DependencyObject</c> or a <c>Control</c> at all. Embedded hosts call <c>UseWpf</c>
/// themselves; an injected target by definition cannot, which is what this replaces.
/// </para>
/// <para>
/// The adapter is loaded reflectively from the staged payload directory rather than referenced.
/// Two reasons: this payload targets net8.0 while the modern adapters target net8.0-windows, and a
/// hard reference would make every attach - including into a non-UI process - depend on the
/// WindowsDesktop shared framework being present in the target. Loading on demand keeps non-UI
/// attaches free of any desktop dependency.
/// </para>
/// </summary>
internal static class DesktopAdapterWiring
{
    public const string None = "none";
    public const string Wpf = "wpf";
    public const string WinForms = "winforms";

    public static void Apply(EndpointBuilder builder, string? adapters)
    {
        if (string.IsNullOrWhiteSpace(adapters) ||
            adapters!.Equals(None, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (adapters.Equals(Wpf, StringComparison.OrdinalIgnoreCase))
        {
            ApplyWpf(builder);
            return;
        }

        if (adapters.Equals(WinForms, StringComparison.OrdinalIgnoreCase))
        {
            ApplyWinForms(builder);
            return;
        }

        throw new ArgumentException(
            "Unknown adapter selection '" + adapters + "'. Expected '" + Wpf + "', '" + WinForms +
            "', or '" + None + "'.",
            nameof(adapters));
    }

    private static void ApplyWpf(EndpointBuilder builder)
    {
        // Application.Current is the documented root for UseWpf(Application): the adapter
        // enumerates Application.Windows when no explicit roots are registered, so the target
        // application needs to cooperate in no way at all.
        var applicationType = RequireLoadedType("PresentationFramework", "System.Windows.Application");
        var current = applicationType
            .GetProperty("Current", BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null);
        if (current is null)
        {
            throw new InvalidOperationException(
                "The target process has WPF loaded but System.Windows.Application.Current is null, " +
                "so there is no application-level dispatcher to attach to. Some WPF hosts create " +
                "windows without an Application; attach without --adapters and use evaluate " +
                "against the target's own types instead.");
        }

        InvokeRegistration(
            "Scry.Wpf",
            "Scry.Wpf.WpfEndpointBuilderExtensions",
            "UseWpf",
            applicationType,
            builder,
            current);
    }

    private static void ApplyWinForms(EndpointBuilder builder)
    {
        // UseWinForms needs a Control to marshal through. Application.OpenForms is ordered by
        // creation, not by which form is the application's "main" one - a hidden utility/listener
        // form (observed in practice: a third-party UI library's own internal theme-change
        // listener window, created before the application's real main form and left permanently
        // invisible) can end up first and silently become the marshal owner. Every UI-marshalled
        // call then targets that hidden form's handle instead of a form anyone is actually
        // driving, and since nothing ever shows, activates, or otherwise pumps it any differently
        // from before, calls marshalled through it can hang rather than fail fast. Prefer the
        // first *visible* open form; only fall back to the first form of any visibility when none
        // are visible yet (e.g. attaching before the main form has shown), which keeps the
        // original behavior for that edge case.
        var applicationType = RequireLoadedType("System.Windows.Forms", "System.Windows.Forms.Application");
        var openForms = applicationType
            .GetProperty("OpenForms", BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null);
        var forms = (openForms as System.Collections.IEnumerable)?.Cast<object>().ToArray()
            ?? Array.Empty<object>();
        var controlType = RequireLoadedType("System.Windows.Forms", "System.Windows.Forms.Control");
        var visibleProperty = controlType.GetProperty("Visible", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "System.Windows.Forms.Control.Visible was not found by reflection.");
        var owner = forms.FirstOrDefault(form => (bool)visibleProperty.GetValue(form)!)
            ?? forms.FirstOrDefault();
        if (owner is null)
        {
            throw new InvalidOperationException(
                "The target process has Windows Forms loaded but Application.OpenForms is empty, " +
                "so there is no control to marshal through. Retry once a form is open, or attach " +
                "without --adapters.");
        }

        InvokeRegistration(
            "Scry.WinForms",
            "Scry.WinForms.WinFormsEndpointBuilderExtensions",
            "UseWinForms",
            controlType,
            builder,
            owner);
    }

    /// <summary>
    /// Resolves a type from a framework assembly that must already be loaded in the target. A
    /// missing assembly means the caller asked for an adapter this target cannot support, which is
    /// worth saying plainly rather than surfacing a raw reflection failure.
    /// </summary>
    private static Type RequireLoadedType(string assemblyName, string typeName)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(
                candidate.GetName().Name,
                assemblyName,
                StringComparison.OrdinalIgnoreCase));
        if (assembly is null)
        {
            throw new InvalidOperationException(
                "The target process does not have " + assemblyName + " loaded, so the requested " +
                "adapter does not apply to it. Attach without --adapters for a non-UI target.");
        }

        return assembly.GetType(typeName, throwOnError: true)!;
    }

    private static void InvokeRegistration(
        string adapterAssemblyName,
        string extensionsTypeName,
        string methodName,
        Type ownerParameterType,
        EndpointBuilder builder,
        object owner)
    {
        var adapter = LoadAdapterAssembly(adapterAssemblyName);
        var extensions = adapter.GetType(extensionsTypeName, throwOnError: true)!;

        // UseWpf and UseWinForms each take (builder, owner, configure = null, options = null), and
        // WPF has a second overload taking a Dispatcher rather than an Application. Match on the
        // owner parameter type so the intended overload is chosen rather than the first found.
        var method = extensions.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(candidate =>
                candidate.Name == methodName &&
                candidate.GetParameters() is { Length: 4 } parameters &&
                parameters[1].ParameterType == ownerParameterType)
            ?? throw new InvalidOperationException(
                extensionsTypeName + "." + methodName + " does not have the expected " +
                "(EndpointBuilder, " + ownerParameterType.Name + ", configure, options) overload.");

        method.Invoke(null, new object?[] { builder, owner, null, null });
    }

    /// <summary>
    /// Loads a staged adapter from this payload's own directory into this payload's own load
    /// context, so the EndpointBuilder type it binds against is the one this assembly is using. On
    /// .NET a plain Assembly.LoadFrom would land in the default context and produce two distinct
    /// EndpointBuilder types.
    /// </summary>
    private static Assembly LoadAdapterAssembly(string assemblyName)
    {
        var directory = Path.GetDirectoryName(typeof(DesktopAdapterWiring).Assembly.Location);
        var path = Path.Combine(directory!, assemblyName + ".dll");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                "The " + assemblyName + " adapter was not staged beside the injection payload at '" +
                path + "'. Rebuild the injector so its payload directories are repopulated.");
        }

#if NETFRAMEWORK
        return Assembly.LoadFrom(path);
#else
        var context =
            System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(
                typeof(DesktopAdapterWiring).Assembly)
            ?? System.Runtime.Loader.AssemblyLoadContext.Default;
        return context.LoadFromAssemblyPath(path);
#endif
    }
}
