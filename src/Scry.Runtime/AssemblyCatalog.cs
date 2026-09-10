using System.Reflection;
#if !NET48
using System.Runtime.Loader;
#endif
using Microsoft.CodeAnalysis;
using Scry.Contracts;

namespace Scry.Runtime;

internal sealed class AssemblyCatalog
{
    private readonly object _gate = new();
#if !NET48
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
        int maximumReferences)
    {
        if (selectors is { Count: > 0 } && selectors.Count > maximumReferences)
        {
            throw new ScryOperationException(
                "request_limit_exceeded",
                $"references exceeds the limit of {maximumReferences} entries.");
        }

        var assemblies = LoadedAssemblies()
            .Where(assembly =>
                IsExecutionCompatible(assembly) &&
                !assembly.IsDynamic &&
                TryGetLocation(assembly) is not null)
            .GroupBy(assembly => assembly.FullName, StringComparer.Ordinal)
            .Select(group => group.First())
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
                        $"No compatible loaded assembly matches reference '{selector}'.");
                }

                if (!assemblies.Any(assembly => AssemblyMatches(assembly, selector)))
                {
                    throw new ScryOperationException(
                        "assembly_not_compatible",
                        $"Assembly reference '{selector}' is not file-backed in the default load context and cannot preserve runtime type identity in C# execution.");
                }
            }
        }

        var references = new List<MetadataReference>();
        foreach (var path in assemblies
            .Select(TryGetLocation)
            .Where(path => path is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                references.Add(MetadataReference.CreateFromFile(path!));
            }
            catch (Exception exception) when (
                exception is BadImageFormatException or IOException or UnauthorizedAccessException)
            {
                // Native or unreadable modules are not compatible Roslyn metadata references.
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

    private Assembly LoadDefault(string path)
    {
        var existing = LoadedAssemblies().FirstOrDefault(assembly =>
            IsDefaultContext(assembly) &&
            string.Equals(TryGetLocation(assembly), path, StringComparison.OrdinalIgnoreCase));
#if NET48
        return existing ?? Assembly.LoadFrom(path);
#else
        return existing ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
#endif
    }

    private Assembly LoadIsolated(string path)
    {
#if NET48
        throw new ScryOperationException(
            "load_policy_not_supported",
            "The isolated load policy is unavailable on .NET Framework 4.8; only the default AppDomain is supported.");
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
#if NET48
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
#if NET48
        return "DefaultAppDomain";
#else
        var context = AssemblyLoadContext.GetLoadContext(assembly);
        return context?.Name ?? (context == AssemblyLoadContext.Default ? "Default" : "<unknown>");
#endif
    }

#if !NET48
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
#if NET48
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
#if NET48
        _ = assembly;
        return true;
#else
        return AssemblyLoadContext.GetLoadContext(assembly) == AssemblyLoadContext.Default;
#endif
    }

    private static bool IsExecutionCompatible(Assembly assembly) => IsDefaultContext(assembly);
}
