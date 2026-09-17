#if NETFRAMEWORK
namespace Scry.Runtime;

/// <summary>
/// Resolves an <c>--appdomain</c> / <c>appdomain.start</c> selector - <c>&lt;id&gt;</c>,
/// <c>&lt;name&gt;</c>, or <c>auto</c> - against the domains actually loaded in this process.
/// <para>
/// A domain's <see cref="AppDomain.FriendlyName"/> is not reliably stable: an ASP.NET application
/// domain's name carries a volatile trailing sequence number that changes on every recycle (for
/// example <c>/LM/W3SVC/2/ROOT-1-134341132053660838</c>), while the leading
/// <c>/LM/W3SVC/2/ROOT</c> - the actual application identity - does not. So beyond an exact name
/// match, a selector that is a <em>prefix</em> of a domain's name also matches, which is what lets
/// an ASP.NET application be targeted by its stable id across recycles without this class knowing
/// anything about ASP.NET.
/// </para>
/// </summary>
internal static class AppDomainSelector
{
    /// <summary>
    /// Resolves <paramref name="selector"/> against <paramref name="domains"/>. Returns the matches
    /// found - zero, one, or several - and leaves it to the caller to decide what zero or several
    /// means: an attach-time hop and a live <c>appdomain.start</c> disagree on that (see both call
    /// sites), which is why this does not throw.
    /// </summary>
    public static IReadOnlyList<AppDomain> Resolve(
        string? selector,
        IReadOnlyList<AppDomain> domains)
    {
        if (string.IsNullOrWhiteSpace(selector) ||
            // Non-null past the guard above, but .NET Framework's reference assemblies are not
            // annotated, so the compiler cannot infer that from string.IsNullOrWhiteSpace.
            selector!.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            // "auto": the single non-default domain when there is exactly one, else the default -
            // deliberately never a guess among several non-default domains, which would silently
            // place the endpoint somewhere the caller did not ask for.
            var nonDefault = domains.Where(domain => !domain.IsDefaultAppDomain()).ToArray();
            return nonDefault.Length == 1
                ? nonDefault
                : domains.Where(domain => domain.IsDefaultAppDomain()).ToArray();
        }

        if (int.TryParse(selector, out var id))
        {
            return domains.Where(domain => domain.Id == id).ToArray();
        }

        var exact = domains.Where(domain =>
            string.Equals(domain.FriendlyName, selector, StringComparison.Ordinal)).ToArray();
        if (exact.Length > 0)
        {
            return exact;
        }

        return domains.Where(domain =>
            domain.FriendlyName.StartsWith(selector, StringComparison.Ordinal)).ToArray();
    }
}
#endif
