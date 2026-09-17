using System.Reflection;
#if !NETFRAMEWORK
using System.Runtime.Loader;
#endif
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using Scry.Contracts;

namespace Scry.Runtime;

internal sealed class AssemblyCatalog
{
    /// <summary>
    /// Metadata references keyed by assembly file path. MetadataReference.CreateFromFile reads and
    /// parses the file, and it was previously called for every compatible loaded assembly on every
    /// single execution - a few hundred file reads per evaluate in a real application, which
    /// dominated compile time. A loaded assembly's file cannot be swapped underneath the loaded
    /// image, so the parsed metadata stays valid for the life of the process. A null value records
    /// a path that is not usable metadata, so unreadable and native modules are not retried.
    /// Naturally bounded by the number of distinct assembly paths in the process.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MetadataReference?> _metadataReferences =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();
#if !NETFRAMEWORK
    private readonly List<AssemblyLoadContext> _retainedContexts = [];
#endif
    private readonly RuntimeHostOptions _options;

    public AssemblyCatalog(RuntimeHostOptions options)
    {
        _options = options;
    }

    public AssemblyDescription Load(LoadAssemblyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            throw new ScryOperationException("invalid_request", "path is required.");
        }

        var path = Path.GetFullPath(request.Path);
        if (!IsFullyQualifiedPath(request.Path))
        {
            throw new ScryOperationException(
                "invalid_request",
                "Assembly paths must be absolute so loading is independent of the target's working directory.");
        }

        if (!File.Exists(path))
        {
            throw new ScryOperationException("assembly_not_found", $"Assembly '{path}' does not exist.");
        }

        var fileLength = new FileInfo(path).Length;
        if (fileLength > _options.MaximumAssemblyBytes)
        {
            throw new ScryOperationException(
                "request_limit_exceeded",
                $"Assembly size {fileLength} exceeds the {_options.MaximumAssemblyBytes}-byte limit.");
        }

        try
        {
            _ = AssemblyName.GetAssemblyName(path);
            var policy = request.LoadPolicy.Trim().ToLowerInvariant();
            var assembly = policy switch
            {
                "default" => LoadDefault(path),
                "isolated" => LoadIsolated(path),
                _ => throw new ScryOperationException(
                    "invalid_load_policy",
                    "loadPolicy must be 'default' or 'isolated'.")
            };
            return Describe(assembly);
        }
        catch (ScryOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is BadImageFormatException or FileLoadException or FileNotFoundException or
            IOException or UnauthorizedAccessException)
        {
            throw new ScryOperationException(
                "assembly_load_failed",
                $"Could not load assembly '{path}': {exception.Message}");
        }
    }

    public IReadOnlyList<AssemblyDescription> List() =>
        LoadedAssemblies()
            .Select(Describe)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FullName, StringComparer.Ordinal)
            .ThenBy(item => item.LoadContext, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<TypeSummary> FindTypes(FindTypesRequest request)
    {
        if (request.Limit < 1 || request.Limit > _options.MaximumTypeResults)
        {
            throw new ScryOperationException(
                "invalid_request",
                $"limit must be between 1 and {_options.MaximumTypeResults}.");
        }

        return SelectAssemblies(request.Assembly, request.LoadContext)
            .SelectMany(GetLoadableTypes)
            .Where(type => request.IncludeNonPublic || IsPublic(type))
            .Where(type => request.Namespace is null ||
                string.Equals(type.Namespace, request.Namespace, StringComparison.Ordinal))
            .Where(type => string.IsNullOrWhiteSpace(request.Query) ||
                (type.FullName ?? type.Name).IndexOf(request.Query, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(type => type.FullName ?? type.Name, StringComparer.Ordinal)
            .Take(request.Limit)
            .Select(Summarize)
            .ToArray();
    }

    public TypeDescription DescribeType(DescribeTypeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Type))
        {
            throw new ScryOperationException("invalid_request", "type is required.");
        }

        var matches = SelectAssemblies(request.Assembly, request.LoadContext)
            .SelectMany(GetLoadableTypes)
            .Where(type =>
                string.Equals(type.FullName, request.Type, StringComparison.Ordinal) ||
                string.Equals(type.AssemblyQualifiedName, request.Type, StringComparison.Ordinal))
            .Where(type => request.IncludeNonPublic || IsPublic(type))
            .Distinct()
            .Take(2)
            .ToArray();
        if (matches.Length == 0)
        {
            throw new ScryOperationException("type_not_found", $"Type '{request.Type}' was not found.");
        }

        if (matches.Length > 1)
        {
            throw new ScryOperationException(
                "ambiguous_type",
                $"Type '{request.Type}' exists in multiple loaded assemblies; specify assembly.");
        }

        var type = matches[0];
        var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
            (request.IncludeNonPublic ? BindingFlags.NonPublic : 0);
        var members = type.GetMembers(flags)
            .Where(member => member.MemberType is
                MemberTypes.Constructor or MemberTypes.Method or MemberTypes.Property or
                MemberTypes.Field or MemberTypes.Event)
            .OrderBy(member => member.Name, StringComparer.Ordinal)
            .ThenBy(member => member.MemberType)
            .Take(_options.MaximumTypeMembers + 1)
            .Select(DescribeMember)
            .ToArray();
        if (members.Length > _options.MaximumTypeMembers)
        {
            throw new ScryOperationException(
                "result_limit_exceeded",
                $"Type '{request.Type}' has more than {_options.MaximumTypeMembers} visible members.");
        }

        return new(
            Summarize(type),
            TypeName(type.BaseType),
            type.GetInterfaces().Select(item => TypeName(item)!).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            type.GetGenericArguments().Select(item => TypeName(item)!).ToArray(),
            members);
    }

    public IReadOnlyList<MetadataReference> GetMetadataReferences(
        IReadOnlyList<string>? selectors,
        int maximumReferences,
        string? loadContext = null)
    {
        if (selectors is { Count: > 0 } && selectors.Count > maximumReferences)
        {
            throw new ScryOperationException(
                "request_limit_exceeded",
                $"references exceeds the limit of {maximumReferences} entries.");
        }

        if (!string.IsNullOrWhiteSpace(loadContext) &&
            !LoadedAssemblies().Any(assembly => string.Equals(ContextName(assembly), loadContext, StringComparison.Ordinal)))
        {
            throw new ScryOperationException(
                "load_context_not_found",
                $"No selected assembly is loaded in context '{loadContext}'.");
        }

        var assemblies = LoadedAssemblies()
            .Where(assembly =>
                IsExecutionCompatible(assembly, loadContext) &&
                !assembly.IsDynamic &&
                TryGetLocation(assembly) is not null)
            .GroupBy(assembly => assembly.FullName, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(assembly =>
                    loadContext is not null && string.Equals(ContextName(assembly), loadContext, StringComparison.Ordinal))
                .First())
            .ToArray();
        if (selectors is { Count: > 0 })
        {
            foreach (var selector in selectors)
            {
                if (string.IsNullOrWhiteSpace(selector))
                {
                    throw new ScryOperationException(
                        "invalid_request",
                        "references entries must be non-empty assembly names.");
                }

                var loadedMatches = LoadedAssemblies()
                    .Where(assembly => AssemblyMatches(assembly, selector))
                    .ToArray();
                if (loadedMatches.Length == 0)
                {
                    throw new ScryOperationException(
                        "assembly_not_found",
                        $"No loaded assembly matches reference '{selector}'.");
                }

                if (!assemblies.Any(assembly => AssemblyMatches(assembly, selector)))
                {
                    throw new ScryOperationException(
                        "assembly_not_compatible",
                        $"Assembly reference '{selector}' is not eligible for C# execution: it must be file-backed, not generated at run time, and loaded in the default load context, the runtime's own context, or the requested loadContext.");
                }
            }
        }

        var references = new List<MetadataReference>();
        foreach (var path in assemblies
            .Select(TryGetLocation)
            .Where(path => path is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var reference = _metadataReferences.GetOrAdd(path!, static candidate =>
            {
                try
                {
                    return MetadataReference.CreateFromFile(candidate);
                }
                catch (Exception exception) when (
                    exception is BadImageFormatException or IOException or UnauthorizedAccessException)
                {
                    // Native or unreadable modules are not compatible Roslyn metadata references.
                    return null;
                }
            });
            if (reference is not null)
            {
                references.Add(reference);
            }
        }

        if (references.Count > maximumReferences)
        {
            throw new ScryOperationException(
                "reference_limit_exceeded",
                $"The target has {references.Count} compatible loaded assembly references, exceeding the configured limit of {maximumReferences}.");
        }

        return references;
    }

    /// <summary>
    /// Builds the Roslyn loader used for evaluate/execute. Deduplicates by simple name, keeping the
    /// highest version: a long-lived host can have two versions of the same assembly loaded side by
    /// side (System.Text.Json 4.x alongside 9.x is the common case when attaching), and registering
    /// both leaves script type resolution ambiguous. GetMetadataReferences groups by FullName
    /// instead, because there distinct versions are legitimate compile references; here only one can
    /// win.
    /// </summary>
    public InteractiveAssemblyLoader CreateExecutionAssemblyLoader(string? loadContext = null)
    {
        var loader = new InteractiveAssemblyLoader();
        var candidates = LoadedAssemblies()
            .Where(assembly => IsExecutionCompatible(assembly, loadContext))
            .GroupBy(assembly => assembly.GetName().Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                // A same-simple-named copy in the caller-selected context wins the tie ahead of the
                // version compare, so the script binds the plugin's own Type rather than Default's -
                // that is the whole identity guarantee this feature depends on.
                .OrderByDescending(assembly =>
                    loadContext is not null && string.Equals(ContextName(assembly), loadContext, StringComparison.Ordinal))
                .ThenByDescending(assembly => assembly.GetName().Version ?? new Version(0, 0))
                .First());
        foreach (var assembly in candidates)
        {
            loader.RegisterDependency(assembly);
        }

        return loader;
    }

    private Assembly LoadDefault(string path)
    {
        var existing = LoadedAssemblies().FirstOrDefault(assembly =>
            IsDefaultContext(assembly) &&
            string.Equals(TryGetLocation(assembly), path, StringComparison.OrdinalIgnoreCase));
#if NETFRAMEWORK
        return existing ?? Assembly.LoadFrom(path);
#else
        return existing ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
#endif
    }

    private Assembly LoadIsolated(string path)
    {
#if NETFRAMEWORK
        throw new ScryOperationException(
            "load_policy_not_supported",
            "The isolated load policy is unavailable on .NET Framework; only the default AppDomain is supported.");
#else
        lock (_gate)
        {
            var existing = _retainedContexts
                .SelectMany(context => context.Assemblies)
                .FirstOrDefault(assembly =>
                    string.Equals(TryGetLocation(assembly), path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            var context = new IsolatedAssemblyLoadContext(path);
            var assembly = context.LoadFromAssemblyPath(path);
            _retainedContexts.Add(context);
            return assembly;
        }
#endif
    }

    private IEnumerable<Assembly> SelectAssemblies(string? selector, string? loadContext)
    {
        var assemblies = LoadedAssemblies();
        if (!string.IsNullOrWhiteSpace(selector))
        {
            var assemblySelector = selector!;
            assemblies = assemblies
                .Where(assembly => AssemblyMatches(assembly, assemblySelector))
                .ToArray();
            if (assemblies.Length == 0)
            {
                throw new ScryOperationException(
                    "assembly_not_found",
                    $"No loaded assembly matches '{selector}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(loadContext))
        {
            return assemblies;
        }

        var contextMatches = assemblies
            .Where(assembly =>
                string.Equals(ContextName(assembly), loadContext, StringComparison.Ordinal))
            .ToArray();
        if (contextMatches.Length == 0)
        {
            throw new ScryOperationException(
                "load_context_not_found",
                $"No selected assembly is loaded in context '{loadContext}'.");
        }

        return contextMatches;
    }

    private static Assembly[] LoadedAssemblies() =>
        AppDomain.CurrentDomain.GetAssemblies().Distinct().ToArray();

    private static bool AssemblyMatches(Assembly assembly, string selector) =>
        string.Equals(assembly.GetName().Name, selector, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(assembly.FullName, selector, StringComparison.OrdinalIgnoreCase);

    private static AssemblyDescription Describe(Assembly assembly)
    {
#if NETFRAMEWORK
        var name = assembly.GetName();
        return new(
            name.Name ?? assembly.FullName ?? "<unknown>",
            assembly.FullName ?? name.FullName,
            name.Version?.ToString(),
            TryGetLocation(assembly),
            assembly.IsDynamic,
            "DefaultAppDomain",
            true,
            false);
#else
        var context = AssemblyLoadContext.GetLoadContext(assembly);
        var name = assembly.GetName();
        return new(
            name.Name ?? assembly.FullName ?? "<unknown>",
            assembly.FullName ?? name.FullName,
            name.Version?.ToString(),
            TryGetLocation(assembly),
            assembly.IsDynamic,
            context?.Name ?? (context == AssemblyLoadContext.Default ? "Default" : "<unknown>"),
            context == AssemblyLoadContext.Default,
            context?.IsCollectible == true);
#endif
    }

    private static TypeSummary Summarize(Type type) =>
        new(
            type.Name,
            type.FullName ?? type.Name,
            type.Namespace,
            type.Assembly.GetName().Name ?? type.Assembly.FullName ?? "<unknown>",
            ContextName(type.Assembly),
            IsPublic(type),
            type.IsAbstract,
            type.IsInterface,
            type.IsEnum,
            type.IsValueType);

    private static MemberDescription DescribeMember(MemberInfo member) =>
        member switch
        {
            ConstructorInfo constructor => new(
                constructor.Name,
                "constructor",
                TypeName(constructor.DeclaringType)!,
                false,
                false,
                constructor.IsPublic,
                constructor.GetParameters().Select(parameter => TypeName(parameter.ParameterType)!).ToArray()),
            MethodInfo method => new(
                method.Name,
                "method",
                TypeName(method.ReturnType)!,
                false,
                false,
                method.IsPublic,
                method.GetParameters().Select(parameter => TypeName(parameter.ParameterType)!).ToArray()),
            PropertyInfo property => new(
                property.Name,
                "property",
                TypeName(property.PropertyType)!,
                property.GetMethod is not null,
                property.SetMethod is not null,
                property.GetMethod?.IsPublic == true || property.SetMethod?.IsPublic == true,
                property.GetIndexParameters().Select(parameter => TypeName(parameter.ParameterType)!).ToArray()),
            FieldInfo field => new(
                field.Name,
                "field",
                TypeName(field.FieldType)!,
                true,
                !field.IsInitOnly,
                field.IsPublic),
            EventInfo eventInfo => new(
                eventInfo.Name,
                "event",
                TypeName(eventInfo.EventHandlerType)!,
                eventInfo.AddMethod is not null,
                eventInfo.RemoveMethod is not null,
                eventInfo.AddMethod?.IsPublic == true || eventInfo.RemoveMethod?.IsPublic == true),
            _ => throw new InvalidOperationException($"Unsupported member type {member.MemberType}.")
        };

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null)!;
        }
        catch (Exception exception) when (
            exception is NotSupportedException or FileNotFoundException or FileLoadException)
        {
            return Array.Empty<Type>();
        }
    }

    private static bool IsPublic(Type type) => type.IsPublic || type.IsNestedPublic;

    private static string? TypeName(Type? type) => type?.FullName ?? type?.Name;

    private static string? TryGetLocation(Assembly assembly)
    {
        try
        {
            return string.IsNullOrEmpty(assembly.Location)
                ? null
                : Path.GetFullPath(assembly.Location);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string ContextName(Assembly assembly)
    {
#if NETFRAMEWORK
        return "DefaultAppDomain";
#else
        var context = AssemblyLoadContext.GetLoadContext(assembly);
        return context?.Name ?? (context == AssemblyLoadContext.Default ? "Default" : "<unknown>");
#endif
    }

#if !NETFRAMEWORK
    private sealed class IsolatedAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public IsolatedAssemblyLoadContext(string componentPath)
            : base($"Scry.Isolated.{Guid.NewGuid():N}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(componentPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }
#endif

    private static bool IsFullyQualifiedPath(string path)
    {
#if NETFRAMEWORK
        return path.StartsWith(@"\\", StringComparison.Ordinal) ||
            (path.Length >= 3 &&
             char.IsLetter(path[0]) &&
             path[1] == ':' &&
             (path[2] == Path.DirectorySeparatorChar || path[2] == Path.AltDirectorySeparatorChar));
#else
        return Path.IsPathFullyQualified(path);
#endif
    }

    private static bool IsDefaultContext(Assembly assembly)
    {
#if NETFRAMEWORK
        _ = assembly;
        return true;
#else
        return AssemblyLoadContext.GetLoadContext(assembly) == AssemblyLoadContext.Default;
#endif
    }

    private static bool IsExecutionCompatible(Assembly assembly, string? loadContext)
    {
#if NETFRAMEWORK
        // .NET Framework has a single default AppDomain and no load contexts, so there is no
        // context to compare against; loadContext is validated and rejected before this is ever
        // reached on this TFM (see ExecutionEngine's netfx guard), so it is intentionally unused
        // here. Accept any assembly Roslyn could actually reference: file-backed, not generated at
        // run time, and not metadata-only. This deliberately does not accept everything loaded - a
        // large host has hundreds of assemblies, and registering dynamic or locationless ones as
        // interactive dependencies is pointless and slow.
        _ = loadContext;
        return !assembly.IsDynamic
            && !assembly.ReflectionOnly
            && TryGetLocation(assembly) is not null;
#else
        // An injected payload is loaded into its own component context rather than Default, so
        // accept this runtime's own context too - otherwise execution would see a different
        // assembly set when attached than when embedded. A caller-selected loadContext widens this
        // further, e.g. to reach an isolated context created by load-assembly, without ever
        // narrowing it - Default and the runtime's own context stay eligible regardless.
        var context = AssemblyLoadContext.GetLoadContext(assembly);
        var runtimeContext = AssemblyLoadContext.GetLoadContext(typeof(AssemblyCatalog).Assembly);
        return context == AssemblyLoadContext.Default
            || context == runtimeContext
            || (loadContext is not null && string.Equals(ContextName(assembly), loadContext, StringComparison.Ordinal));
#endif
    }
}
