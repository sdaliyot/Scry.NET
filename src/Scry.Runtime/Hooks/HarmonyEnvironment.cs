using System.Reflection;
using Scry.Contracts;

namespace Scry.Runtime;

/// <summary>
/// Reports on Harmony as loaded in this AppDomain, so a hook never fails - or silently misbehaves -
/// because the target already carries its own copy.
/// <para>
/// <c>0Harmony</c> is not strong-named, so the CLR binds it by simple name alone: if the target
/// loaded its own, a reference from this runtime resolves to that copy and the payload's staged
/// DLL is never consulted. Nothing in here names a Harmony type; the one thing that must (the
/// assembly this runtime actually resolved) comes through <see cref="HookPatcher"/>.
/// </para>
/// </summary>
internal static class HarmonyEnvironment
{
    private const string HarmonyAssemblyName = "0Harmony";

    public static HookHarmonyInfo Describe()
    {
        var ours = HookPatcher.LoadedAssembly();
        var foreign = new List<HookForeignHarmony>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            // Harmony and MonoMod emit dynamic assemblies of their own - named like MonoMod
            // namespaces - to hold generated patch code. Those are ours, not a second copy.
            if (assembly.IsDynamic)
            {
                continue;
            }

            var name = assembly.GetName();
            var location = Location(assembly);
            var isHarmony = string.Equals(name.Name, HarmonyAssemblyName, StringComparison.Ordinal);

            // A MonoMod assembly only counts when it came from a file: MonoMod also generates helper
            // assemblies in memory (loaded from a stream, so no location and version 0.0.0.0) while
            // patching, and those are this runtime's own doing.
            var isMonoMod = !string.IsNullOrEmpty(location) &&
                name.Name is { } simple &&
                simple.StartsWith("MonoMod.", StringComparison.Ordinal);
            if ((isHarmony && !ReferenceEquals(assembly, ours)) || isMonoMod)
            {
                foreign.Add(new(name.Name ?? string.Empty, name.Version?.ToString() ?? "unknown", location));
            }
        }

        return new(ours.GetName().Version?.ToString() ?? "unknown", Location(ours), foreign);
    }

    /// <summary>
    /// Throws <c>hooks_unavailable</c> when the Harmony this runtime resolved cannot be relied on:
    /// one older than the version it was compiled against may lack the APIs the patches use.
    /// </summary>
    public static void ThrowIfUnusable(HookHarmonyInfo info)
    {
        var compiled = HookPatcher.CompiledVersion();
        if (Version.TryParse(info.Version, out var loaded) && loaded < compiled)
        {
            throw new ScryOperationException(
                "hooks_unavailable",
                $"This process had already loaded Harmony {loaded} from '{info.Location ?? "an unknown location"}', " +
                $"older than the {compiled} that Scry's hooks need. The CLR binds 0Harmony by simple name, so " +
                "the copy staged with the Scry payload was never used. Hooks cannot be applied in this process.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["loadedVersion"] = loaded.ToString(),
                    ["requiredVersion"] = compiled.ToString(),
                    ["location"] = info.Location ?? string.Empty
                });
        }
    }

    /// <summary>
    /// Owners of patches on <paramref name="method"/> made through a Harmony copy other than ours.
    /// Two independent copies each keep their own patch state, so stacking a second detour on a
    /// method the first already detoured cannot be undone safely. Read by reflection because the
    /// foreign copy's types are not ours.
    /// </summary>
    public static IReadOnlyList<string> ForeignOwners(MethodBase method)
    {
        var owners = new List<string>();
        var ours = HookPatcher.LoadedAssembly();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic ||
                ReferenceEquals(assembly, ours) ||
                !string.Equals(assembly.GetName().Name, HarmonyAssemblyName, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var patchInfo = assembly.GetType("HarmonyLib.Harmony")
                    ?.GetMethod("GetPatchInfo", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(MethodBase) }, null)
                    ?.Invoke(null, new object[] { method });
                if (patchInfo?.GetType().GetProperty("Owners")?.GetValue(patchInfo) is System.Collections.IEnumerable found)
                {
                    owners.AddRange(found.Cast<object>().Select(owner => $"{owner} (Harmony {assembly.GetName().Version})"));
                }
            }
            catch (Exception exception) when (
                exception is TargetInvocationException or MissingMemberException or AmbiguousMatchException)
            {
                // A foreign copy we cannot interrogate is reported by Describe, not treated as a hit.
            }
        }

        return owners;
    }

    private static string? Location(Assembly assembly)
    {
        try
        {
            return assembly.IsDynamic ? null : assembly.Location;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
