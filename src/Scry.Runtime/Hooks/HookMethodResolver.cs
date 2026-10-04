using System.Reflection;
using Scry.Contracts;

namespace Scry.Runtime;

/// <summary>Selects exactly one patchable method from a type, by name and optional parameter types.</summary>
internal static class HookMethodResolver
{
    private const BindingFlags DefaultFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.DeclaredOnly;

    private static readonly Dictionary<Type, string> KeywordAliases = new()
    {
        [typeof(bool)] = "bool",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(char)] = "char",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal",
        [typeof(string)] = "string",
        [typeof(object)] = "object"
    };

    public static MethodBase Resolve(Type type, HookAddRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Method))
        {
            throw new ScryOperationException("invalid_request", "method is required.");
        }

        if (type.ContainsGenericParameters)
        {
            throw NotPatchable(
                $"'{type.FullName}' is an open generic type; its methods have no single compiled body to patch.");
        }

        var flags = ParseFlags(request.BindingFlags);
        IEnumerable<MethodBase> candidates = request.Method is ".ctor"
            ? type.GetConstructors(flags & ~BindingFlags.Static)
            : type.GetMethods(flags).Where(method => string.Equals(method.Name, request.Method, StringComparison.Ordinal));
        var matches = candidates.ToList();
        if (matches.Count == 0)
        {
            throw new ScryOperationException(
                "member_not_found",
                $"Method '{request.Method}' was not found on '{type.FullName}' with binding flags {flags}.");
        }

        if (request.ParameterTypes is { } wanted)
        {
            matches = matches.Where(method => ParametersMatch(method.GetParameters(), wanted)).ToList();
            if (matches.Count == 0)
            {
                throw new ScryOperationException(
                    "member_not_found",
                    $"No overload of '{request.Method}' on '{type.FullName}' takes ({string.Join(", ", wanted)}).");
            }
        }

        if (matches.Count > 1)
        {
            throw new ScryOperationException(
                "ambiguous_member",
                $"'{request.Method}' on '{type.FullName}' has {matches.Count} overloads; specify parameterTypes. " +
                $"Candidates: {string.Join("; ", matches.Select(Signature))}.");
        }

        var method = matches[0];
        ValidatePatchable(method);
        return method;
    }

    public static string Signature(MethodBase method)
    {
        var parameters = string.Join(
            ", ",
            method.GetParameters().Select(parameter =>
                $"{(parameter.ParameterType.IsByRef ? (parameter.IsOut ? "out " : "ref ") : string.Empty)}" +
                $"{Display(parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType)} " +
                parameter.Name));
        var returnType = method is MethodInfo info ? Display(info.ReturnType) + " " : string.Empty;
        return $"{returnType}{method.DeclaringType?.FullName}.{method.Name}({parameters})";
    }

    /// <summary>C# keyword for a keyword type, applied through arrays (<c>string[]</c>); otherwise the full name.</summary>
    private static string Display(Type type)
    {
        if (type.IsArray)
        {
            return Display(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }

        return KeywordAliases.TryGetValue(type, out var alias) ? alias : type.FullName ?? type.Name;
    }

    private static BindingFlags ParseFlags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DefaultFlags;
        }

        if (!Enum.TryParse<BindingFlags>(text, ignoreCase: true, out var parsed) ||
            (parsed & (BindingFlags.Instance | BindingFlags.Static)) == 0)
        {
            throw new ScryOperationException(
                "invalid_request",
                "bindingFlags must be comma-separated System.Reflection.BindingFlags names including " +
                "Instance and/or Static.");
        }

        return parsed;
    }

    private static bool ParametersMatch(ParameterInfo[] parameters, IReadOnlyList<string> wanted)
    {
        if (parameters.Length != wanted.Count)
        {
            return false;
        }

        for (var index = 0; index < parameters.Length; index++)
        {
            var parameterType = parameters[index].ParameterType;
            if (parameterType.IsByRef)
            {
                parameterType = parameterType.GetElementType()!;
            }

            var requested = wanted[index].Trim().TrimEnd('&');
            var matches =
                string.Equals(requested, parameterType.FullName, StringComparison.Ordinal) ||
                string.Equals(requested, parameterType.Name, StringComparison.Ordinal) ||
                string.Equals(requested, Display(parameterType), StringComparison.Ordinal);
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidatePatchable(MethodBase method)
    {
        var signature = Signature(method);
        if (method.IsAbstract)
        {
            throw NotPatchable($"{signature} is abstract; hook the override that actually runs.");
        }

        if (method.ContainsGenericParameters)
        {
            throw NotPatchable($"{signature} is an open generic method.");
        }

        if ((method.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0 ||
            (method.Attributes & MethodAttributes.PinvokeImpl) != 0)
        {
            throw NotPatchable($"{signature} is implemented natively and has no managed body.");
        }

        if (method is MethodInfo { ReturnType: var returnType } &&
            (returnType.IsByRef || returnType.IsPointer || IsByRefLike(returnType)))
        {
            throw NotPatchable($"{signature} returns a by-ref, pointer or ref struct value.");
        }

        foreach (var parameter in method.GetParameters())
        {
            var parameterType = parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()!
                : parameter.ParameterType;
            if (parameterType.IsPointer || IsByRefLike(parameterType))
            {
                throw NotPatchable(
                    $"{signature} has a pointer or ref struct parameter ('{parameter.Name}'), which cannot be captured.");
            }
        }
    }

    // Type.IsByRefLike does not exist on .NET Framework.
    private static bool IsByRefLike(Type type) =>
        type.IsValueType &&
        type.GetCustomAttributesData().Any(data =>
            data.AttributeType.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute");

    private static ScryOperationException NotPatchable(string message) =>
        new("method_not_patchable", message);
}
