using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Scry.Contracts;

namespace Scry.Runtime;

internal sealed class OperationDispatcher(
    TargetMetadata target,
    AgentConfiguration configuration,
    AssemblyCatalog assemblies,
    ExecutionEngine execution)
{
    private const int MaximumEnumerationPayloadBytes =
        ProtocolConstants.MaximumFrameBytes - (1024 * 1024);
    private static readonly JsonSerializerOptions ArgumentJsonOptions = new(ScryJson.Options)
    {
        IncludeFields = true
    };

    public async ValueTask<object?> DispatchAsync(
        string operation,
        JsonElement payload,
        SessionState session,
        CancellationToken cancellationToken) =>
        operation switch
        {
            "capabilities" => Capabilities(),
            "roots" => Roots(session),
            "inspect" => Inspect(payload, session),
            "get" => Get(payload, session),
            "set" => Set(payload, session),
            "invoke" => await InvokeAsync(payload, session, cancellationToken).ConfigureAwait(false),
            "enumerate" => Enumerate(payload, session),
            "release" => Release(payload, session),
            "evaluate" => await EvaluateAsync(payload, session, cancellationToken).ConfigureAwait(false),
            "execute" => await ExecuteAsync(payload, session, cancellationToken).ConfigureAwait(false),
            "load-assembly" => LoadAssembly(payload),
            "list-assemblies" => ListAssemblies(),
            "find-types" => FindTypes(payload),
            "describe-type" => DescribeType(payload),
            _ => throw new ScryOperationException("operation_not_supported", $"Operation '{operation}' is not supported.")
        };

    private object Capabilities() => new
    {
        target,
        protocolVersion = ProtocolConstants.Version,
        operations = ProtocolConstants.CoreCapabilities,
        features = ProtocolConstants.FeatureCapabilities,
        registeredOperations = configuration.Operations.Values
            .Select(item => new { item.Name, item.Description })
            .OrderBy(item => item.Name, StringComparer.Ordinal)
    };

    private object Roots(SessionState session) => new
    {
        roots = configuration.Roots.Values
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => new
            {
                item.Name,
                item.Description,
                value = Encode(item.ValueFactory(), session)
            })
            .ToArray()
    };

    private object Inspect(JsonElement payload, SessionState session)
    {
        var value = ResolveSubject(payload, session);
        var includeNonPublic = GetOptionalBoolean(payload, "includeNonPublic");
        var type = value.GetType();

        var properties = TypeHierarchy(type)
            .SelectMany(level => level.GetProperties(DeclaredFlags(includeNonPublic)))
            .Where(property => property.GetIndexParameters().Length == 0)
            .Where(property =>
                IsAccessorVisible(property.GetMethod, includeNonPublic) ||
                IsAccessorVisible(property.SetMethod, includeNonPublic))
            .DistinctBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => new MemberDescription(
                property.Name,
                "property",
                TypeName(property.PropertyType),
                IsAccessorVisible(property.GetMethod, includeNonPublic),
                IsAccessorVisible(property.SetMethod, includeNonPublic),
                property.GetMethod?.IsPublic == true || property.SetMethod?.IsPublic == true))
            .Cast<MemberDescription>();
        var fields = TypeHierarchy(type)
            .SelectMany(level => level.GetFields(DeclaredFlags(includeNonPublic)))
            .DistinctBy(field => field.Name, StringComparer.Ordinal)
            .Select(field => new MemberDescription(
                field.Name,
                "field",
                TypeName(field.FieldType),
                true,
                !field.IsInitOnly,
                field.IsPublic))
            .Cast<MemberDescription>();
        var methods = TypeHierarchy(type)
            .SelectMany(level => level.GetMethods(DeclaredFlags(includeNonPublic)))
            .Where(method => !method.IsSpecialName)
            .DistinctBy(
                method => $"{method.Name}({string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.FullName))})",
                StringComparer.Ordinal)
            .Select(method => new MemberDescription(
                method.Name,
                "method",
                TypeName(method.ReturnType),
                false,
                false,
                method.IsPublic,
                method.GetParameters().Select(parameter => TypeName(parameter.ParameterType)).ToArray()))
            .Cast<MemberDescription>();

        return new
        {
            type = TypeName(type),
            preview = session.Preview(value),
            members = properties.Concat(fields).Concat(methods)
                .OrderBy(member => member.Name, StringComparer.Ordinal)
                .ThenBy(member => member.Kind, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private object Get(JsonElement payload, SessionState session)
    {
        var subject = ResolveSubject(payload, session);
        var name = RequiredString(payload, "member");
        var member = FindReadableMember(subject.GetType(), name, GetOptionalBoolean(payload, "includeNonPublic"));
        var value = member switch
        {
            PropertyInfo property => property.GetValue(subject),
            FieldInfo field => field.GetValue(subject),
            _ => throw new UnreachableException()
        };
        return new
        {
            value = Encode(
                value,
                session,
                GetOptionalBoolean(payload, "asReference"))
        };
    }

    private object Set(JsonElement payload, SessionState session)
    {
        var subject = ResolveSubject(payload, session);
        var name = RequiredString(payload, "member");
        var valueElement = RequiredProperty(payload, "value");
        var member = FindWritableMember(subject.GetType(), name, GetOptionalBoolean(payload, "includeNonPublic"));
        object? converted;
        switch (member)
        {
            case PropertyInfo property:
                converted = ConvertArgument(valueElement, property.PropertyType, session);
                property.SetValue(subject, converted);
                break;
            case FieldInfo field:
                converted = ConvertArgument(valueElement, field.FieldType, session);
                field.SetValue(subject, converted);
                break;
            default:
                throw new UnreachableException();
        }

        return new
        {
            value = Encode(
                converted,
                session,
                GetOptionalBoolean(payload, "asReference"))
        };
    }

    private async ValueTask<object?> InvokeAsync(
        JsonElement payload,
        SessionState session,
        CancellationToken cancellationToken)
    {
        if (payload.TryGetProperty("registeredOperation", out var operationElement))
        {
            var operationName = operationElement.GetString()
                ?? throw new ScryOperationException("invalid_request", "registeredOperation must be a string.");
            if (!configuration.Operations.TryGetValue(operationName, out var operation))
            {
                throw new ScryOperationException(
                    "operation_not_found",
                    $"Registered operation '{operationName}' does not exist.");
            }

            var arguments = payload.TryGetProperty("arguments", out var args)
                ? args
                : JsonSerializer.SerializeToElement(new { }, ScryJson.Options);
            return new
            {
                value = Encode(
                    await operation.Handler(arguments, cancellationToken).ConfigureAwait(false),
                    session,
                    GetOptionalBoolean(payload, "asReference"))
            };
        }

        var subject = ResolveSubject(payload, session);
        var name = RequiredString(payload, "member");
        var argumentElements = payload.TryGetProperty("arguments", out var array)
            ? array.EnumerateArray().ToArray()
            : [];
        var includeNonPublic = GetOptionalBoolean(payload, "includeNonPublic");
        var candidates = TypeHierarchy(subject.GetType())
            .SelectMany(level => level.GetMethods(DeclaredFlags(includeNonPublic)))
            .Where(method => method.Name == name && !method.ContainsGenericParameters)
            .Where(method => method.GetParameters().Length == argumentElements.Length)
            .DistinctBy(
                method => $"{method.Name}({string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.FullName))})",
                StringComparer.Ordinal)
            .ToArray();

        var matches = new List<(MethodInfo Method, object?[] Arguments)>();
        foreach (var method in candidates)
        {
            if (TryConvertArguments(
                argumentElements,
                method.GetParameters(),
                session,
                out var converted))
            {
                matches.Add((method, converted));
            }
        }

        if (matches.Count == 0)
        {
            throw new ScryOperationException(
                "member_not_found",
                $"No compatible method '{name}' with {argumentElements.Length} argument(s) was found.");
        }

        if (matches.Count > 1)
        {
            throw new ScryOperationException(
                "ambiguous_member",
                $"Method '{name}' has {matches.Count} compatible overloads; the invocation is ambiguous.");
        }

        var selected = matches[0];
        var result = selected.Method.Invoke(subject, selected.Arguments);
        if (result is Task task)
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            result = task.GetType().IsGenericType
                ? task.GetType().GetProperty("Result")!.GetValue(task)
                : null;
        }
        else if (result is ValueTask valueTask)
        {
            await valueTask.ConfigureAwait(false);
            result = null;
        }
        else if (result is not null &&
            result.GetType().IsGenericType &&
            result.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var valueTaskAsTask = (Task)result.GetType().GetMethod("AsTask")!.Invoke(result, null)!;
            await valueTaskAsTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            result = valueTaskAsTask.GetType().GetProperty("Result")!.GetValue(valueTaskAsTask);
        }

        return new
        {
            value = Encode(
                result,
                session,
                GetOptionalBoolean(payload, "asReference"))
        };
    }

    private object Enumerate(JsonElement payload, SessionState session)
    {
        var subject = ResolveSubject(payload, session);
        if (subject is not IEnumerable enumerable)
        {
            throw new ScryOperationException("not_enumerable", "The referenced value is not enumerable.");
        }

        var offset = OptionalInt32(payload, "offset", 0);
        var limit = OptionalInt32(payload, "limit", 100);
        if (offset < 0 || limit is < 1 or > 1000)
        {
            throw new ScryOperationException(
                "invalid_request",
                "offset must be non-negative and limit must be between 1 and 1000.");
        }

        var items = new List<RemoteValue>();
        var encodedBytes = 0;
        var index = 0;
        var hasMore = false;
        var asReferences = GetOptionalBoolean(payload, "asReferences");
        var enumerator = enumerable.GetEnumerator();
        try
        {
            while (enumerator.MoveNext())
            {
                if (index++ < offset)
                {
                    continue;
                }

                if (items.Count == limit)
                {
                    hasMore = true;
                    break;
                }

                var item = Encode(
                    enumerator.Current,
                    session,
                    asReferences,
                    out var handleCreated);
                var itemBytes = JsonSerializer.SerializeToUtf8Bytes(item, ScryJson.Options).Length;
                if (encodedBytes + itemBytes > MaximumEnumerationPayloadBytes)
                {
                    if (handleCreated && item.Reference is not null)
                    {
                        session.Release(item.Reference.HandleId);
                    }

                    if (items.Count == 0)
                    {
                        throw new ScryOperationException(
                            "value_too_large",
                            "A collection item is too large for a protocol response.");
                    }

                    hasMore = true;
                    break;
                }

                items.Add(item);
                encodedBytes += itemBytes;
            }
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }

        return new { items, offset, returned = items.Count, hasMore };
    }

    private static object Release(JsonElement payload, SessionState session)
    {
        var ids = payload.TryGetProperty("handleIds", out var handles)
            ? handles.EnumerateArray().Select(element =>
                element.GetString() ?? throw new ScryOperationException("invalid_request", "handleIds must contain strings."))
            : [RequiredString(payload, "handleId")];
        var released = ids.Count(session.Release);
        return new { released };
    }

    private async ValueTask<object> EvaluateAsync(
        JsonElement payload,
        SessionState session,
        CancellationToken cancellationToken)
    {
        var request = Deserialize<ExecutionRequest>(payload);
        var result = await execution.EvaluateAsync(request, session, cancellationToken).ConfigureAwait(false);
        return new ExecutionResult(
            Encode(result.Value, session),
            result.Logs,
            result.DroppedLogEntries,
            result.Diagnostics,
            result.ElapsedMilliseconds);
    }

    private async ValueTask<object> ExecuteAsync(
        JsonElement payload,
        SessionState session,
        CancellationToken cancellationToken)
    {
        var request = Deserialize<ExecutionRequest>(payload);
        var result = await execution.ExecuteAsync(request, session, cancellationToken).ConfigureAwait(false);
        return new ExecutionResult(
            Encode(result.Value, session),
            result.Logs,
            result.DroppedLogEntries,
            result.Diagnostics,
            result.ElapsedMilliseconds);
    }

    private object LoadAssembly(JsonElement payload) =>
        new { assembly = assemblies.Load(Deserialize<LoadAssemblyRequest>(payload)) };

    private object ListAssemblies() => new { assemblies = assemblies.List() };

    private object FindTypes(JsonElement payload) =>
        new { types = assemblies.FindTypes(Deserialize<FindTypesRequest>(payload)) };

    private object DescribeType(JsonElement payload) =>
        assemblies.DescribeType(Deserialize<DescribeTypeRequest>(payload));

    private object ResolveSubject(JsonElement payload, SessionState session)
    {
        if (payload.TryGetProperty("root", out var rootElement))
        {
            var rootName = rootElement.GetString()
                ?? throw new ScryOperationException("invalid_request", "root must be a string.");
            if (!configuration.Roots.TryGetValue(rootName, out var root))
            {
                throw new ScryOperationException("root_not_found", $"Root '{rootName}' does not exist.");
            }

            return root.ValueFactory()
                ?? throw new ScryOperationException("null_subject", $"Root '{rootName}' is null.");
        }

        var referenceElement = payload.TryGetProperty("reference", out var directReference)
            ? directReference
            : throw new ScryOperationException("invalid_request", "A root or reference is required.");
        var reference = referenceElement.Deserialize<ExternalReference>(ScryJson.Options)
            ?? throw new ScryOperationException("invalid_request", "The reference is invalid.");
        return session.Resolve(reference);
    }

    private static MemberInfo FindReadableMember(Type type, string name, bool includeNonPublic)
    {
        foreach (var level in TypeHierarchy(type))
        {
            var property = level.GetProperties(DeclaredFlags(includeNonPublic))
                .FirstOrDefault(candidate =>
                    candidate.Name == name &&
                    candidate.GetIndexParameters().Length == 0 &&
                    IsAccessorVisible(candidate.GetMethod, includeNonPublic));
            if (property is not null)
            {
                return property;
            }

            var field = level.GetFields(DeclaredFlags(includeNonPublic))
                .FirstOrDefault(candidate => candidate.Name == name);
            if (field is not null)
            {
                return field;
            }
        }

        throw new ScryOperationException("member_not_found", $"Readable member '{name}' was not found.");
    }

    private static MemberInfo FindWritableMember(Type type, string name, bool includeNonPublic)
    {
        foreach (var level in TypeHierarchy(type))
        {
            var property = level.GetProperties(DeclaredFlags(includeNonPublic))
                .FirstOrDefault(candidate =>
                    candidate.Name == name &&
                    candidate.GetIndexParameters().Length == 0 &&
                    IsAccessorVisible(candidate.SetMethod, includeNonPublic));
            if (property is not null)
            {
                return property;
            }

            var field = level.GetFields(DeclaredFlags(includeNonPublic))
                .FirstOrDefault(candidate => candidate.Name == name && !candidate.IsInitOnly);
            if (field is not null)
            {
                return field;
            }
        }

        throw new ScryOperationException("member_not_found", $"Writable member '{name}' was not found.");
    }

    private RemoteValue Encode(
        object? value,
        SessionState session,
        bool leaseValueType = false)
    {
        return Encode(value, session, leaseValueType, out _);
    }

    private RemoteValue Encode(
        object? value,
        SessionState session,
        bool leaseValueType,
        out bool handleCreated)
    {
        handleCreated = false;
        if (value is null)
        {
            return new("null", "null", "null", JsonSerializer.SerializeToElement<object?>(null, ScryJson.Options));
        }

        var type = value.GetType();
        if (ValueProjection.IsScalar(type))
        {
            return new(
                "scalar",
                TypeName(type),
                session.Preview(value),
                JsonSerializer.SerializeToElement(value, type, ScryJson.Options));
        }

        if (type.IsValueType && !leaseValueType)
        {
            return new(
                "value",
                TypeName(type),
                session.Preview(value),
                ValueProjection.Project(value));
        }

        var reference = session.Lease(value, out handleCreated);
        return new("reference", reference.Type, reference.Preview, null, reference);
    }

    private static object? ConvertArgument(JsonElement element, Type targetType, SessionState session)
    {
        if (TryGetReferenceElement(element, out var referenceElement))
        {
            var reference = referenceElement.Deserialize<ExternalReference>(ScryJson.Options)
                ?? throw new ScryOperationException("invalid_request", "The reference argument is invalid.");
            var resolved = session.Resolve(reference);
            if (!targetType.IsInstanceOfType(resolved))
            {
                throw new ScryOperationException(
                    "argument_type_mismatch",
                    $"Referenced {TypeName(resolved.GetType())} cannot be assigned to {TypeName(targetType)}.");
            }

            return resolved;
        }

        if (ContainsIncompleteProjection(element))
        {
            throw new ScryOperationException(
                "incomplete_value_projection",
                "A truncated, faulted, or reference-bearing value projection cannot be used as an argument.");
        }

        return element.Deserialize(targetType, ArgumentJsonOptions);
    }

    private static bool ContainsIncompleteProjection(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "$reference" or "$truncated" or "$error" ||
                    ContainsIncompleteProjection(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsIncompleteProjection(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryGetReferenceElement(JsonElement element, out JsonElement referenceElement)
    {
        referenceElement = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var candidate = element.TryGetProperty("reference", out var nested) &&
            nested.ValueKind == JsonValueKind.Object
            ? nested
            : element;
        if (!candidate.TryGetProperty("targetId", out var targetId) ||
            targetId.ValueKind != JsonValueKind.String ||
            !candidate.TryGetProperty("sessionId", out var sessionId) ||
            sessionId.ValueKind != JsonValueKind.String ||
            !candidate.TryGetProperty("handleId", out var handleId) ||
            handleId.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        referenceElement = candidate;
        return true;
    }

    private static bool TryConvertArguments(
        JsonElement[] elements,
        ParameterInfo[] parameters,
        SessionState session,
        out object?[] converted)
    {
        converted = new object?[elements.Length];
        try
        {
            for (var index = 0; index < elements.Length; index++)
            {
                converted[index] = ConvertArgument(elements[index], parameters[index].ParameterType, session);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or InvalidCastException ||
            exception is ScryOperationException { Code: "argument_type_mismatch" })
        {
            converted = [];
            return false;
        }
    }

    private static string TypeName(Type type) => type.FullName ?? type.Name;

    private static BindingFlags DeclaredFlags(bool includeNonPublic) =>
        BindingFlags.Instance | BindingFlags.DeclaredOnly | BindingFlags.Public |
        (includeNonPublic ? BindingFlags.NonPublic : 0);

    private static bool IsAccessorVisible(MethodInfo? accessor, bool includeNonPublic) =>
        accessor is not null && (includeNonPublic || accessor.IsPublic);

    private static IEnumerable<Type> TypeHierarchy(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }

    private static JsonElement RequiredProperty(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
            ? value
            : throw new ScryOperationException("invalid_request", $"Required property '{name}' is missing.");

    private static string RequiredString(JsonElement payload, string name) =>
        RequiredProperty(payload, name).GetString()
        ?? throw new ScryOperationException("invalid_request", $"Property '{name}' must be a string.");

    private static bool GetOptionalBoolean(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.GetBoolean();

    private static int OptionalInt32(JsonElement payload, string name, int fallback) =>
        payload.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;

    private static T Deserialize<T>(JsonElement payload) =>
        payload.Deserialize<T>(ScryJson.Options)
        ?? throw new ScryOperationException("invalid_request", $"Request payload must be a {typeof(T).Name} object.");
}
