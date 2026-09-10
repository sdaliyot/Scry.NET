namespace Scry.Contracts;

public sealed record LoadAssemblyRequest(
    string Path,
    string LoadPolicy = "default");

public sealed record AssemblyDescription(
    string Name,
    string FullName,
    string? Version,
    string? Location,
    bool IsDynamic,
    string LoadContext,
    bool IsDefaultLoadContext,
    bool IsCollectible);

public sealed record LoadAssemblyResult(AssemblyDescription Assembly);

public sealed record ListAssembliesResult(IReadOnlyList<AssemblyDescription> Assemblies);

public sealed record FindTypesRequest(
    string? Query = null,
    string? Assembly = null,
    string? LoadContext = null,
    string? Namespace = null,
    bool IncludeNonPublic = false,
    int Limit = 100);

public sealed record TypeSummary(
    string Name,
    string FullName,
    string? Namespace,
    string Assembly,
    string LoadContext,
    bool IsPublic,
    bool IsAbstract,
    bool IsInterface,
    bool IsEnum,
    bool IsValueType);

public sealed record FindTypesResult(IReadOnlyList<TypeSummary> Types);

public sealed record DescribeTypeRequest(
    string Type,
    string? Assembly = null,
    string? LoadContext = null,
    bool IncludeNonPublic = false);

public sealed record TypeDescription(
    TypeSummary Type,
    string? BaseType,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<string> GenericArguments,
    IReadOnlyList<MemberDescription> Members);
