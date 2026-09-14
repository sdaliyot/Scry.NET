using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Scry.Contracts;

namespace Scry.Runtime;

internal sealed class OperationDispatcher(
    TargetMetadata target,
    EndpointConfiguration configuration,
    AssemblyCatalog assemblies,
    ExecutionEngine execution)
{
    private const int MaximumEnumerationPayloadBytes =
        ProtocolConstants.MaximumFrameBytes - (1024 * 1024);
    private static readonly JsonSerializerOptions ArgumentJsonOptions = new(ScryJson.Options)
    {
        IncludeFields = true
    };

    /// <summary>
    /// Operations that read or mutate live target objects, and so can hit UI thread affinity. They
    /// accept the same <c>marshal</c> field <c>evaluate</c>/<c>execute</c> take, applied centrally
    /// here rather than threaded through each one.
    /// <para>
    /// <c>evaluate</c>, <c>execute</c>, <c>wait</c> and <c>assert</c> are absent on purpose:
    /// execution marshals the whole submission itself, and wait/assert marshal each individual
    /// evaluation so that polling never occupies the UI thread between attempts.
    /// </para>
    /// </summary>
    private const int DefaultWaitTimeoutMilliseconds = 5000;
    private const int DefaultPollIntervalMilliseconds = 100;

    private static readonly HashSet<string> MarshallableOperations =
        new(StringComparer.Ordinal) { "inspect", "get", "set", "invoke", "enumerate" };

    public async ValueTask<object?> DispatchAsync(
        string operation,
        JsonElement payload,
        SessionState session,
        OperationExecutionContext context)
    {
        if (!MarshallableOperations.Contains(operation))
        {
            return await DispatchCoreAsync(operation, payload, session, context).ConfigureAwait(false);
        }

        var marshal = MarshalTarget.Resolve(
            OptionalString(payload, "marshal"),
            configuration.ExecutionMarshaller);
        if (!marshal)
        {
            return await DispatchCoreAsync(operation, payload, session, context).ConfigureAwait(false);
        }

        // Resolve() guarantees a marshaller here.
        var marshaller = configuration.ExecutionMarshaller!;
        return await marshaller(
            async () => await DispatchCoreAsync(operation, payload, session, context).ConfigureAwait(false),
            context.CancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<object?> DispatchCoreAsync(
        string operation,
        JsonElement payload,
        SessionState session,
        OperationExecutionContext context) =>
        operation switch
        {
            "capabilities" => Capabilities(),
            "roots" => Roots(session),
            "inspect" => Inspect(payload, session),
            "get" => Get(payload, session),
            "set" => Set(payload, session),
            "invoke" => await InvokeAsync(payload, session, context).ConfigureAwait(false),
            "enumerate" => Enumerate(payload, session),
            "release" => Release(payload, session),
            "evaluate" => await EvaluateAsync(payload, session, context.CancellationToken).ConfigureAwait(false),
            "execute" => await ExecuteAsync(payload, session, context.CancellationToken).ConfigureAwait(false),
            "wait" => await WaitAsync(payload, session, context.CancellationToken).ConfigureAwait(false),
            "assert" => await AssertAsync(payload, session, context.CancellationToken).ConfigureAwait(false),
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
        features = configuration.ExecutionMarshaller is null
            ? ProtocolConstants.FeatureCapabilities
            : ProtocolConstants.FeatureCapabilities
                .Concat(new[] { ProtocolConstants.UiThreadMarshallingFeature })
                .ToArray(),
        registeredOperations = configuration.DescribeOperations()
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToArray(),
        guidance = new
        {
            discovery = "Inspect roots and registered operation metadata before acting.",
            safety = "Prefer read-only operations. Obtain confirmation before invoking operations marked requiresConfirmation.",
            threading = "Operations marked ui-owner marshal through their UI adapter; worker-thread operations must not directly access UI-owned state."
        }
    };

    private object Roots(SessionState session) => new
    {
        roots = configuration.GetRoots()
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
            .DistinctByCompatible(property => property.Name, StringComparer.Ordinal)
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
            .DistinctByCompatible(field => field.Name, StringComparer.Ordinal)
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
            .DistinctByCompatible(
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
            _ => throw new InvalidOperationException("Unsupported readable member.")
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
                throw new InvalidOperationException("Unsupported writable member.");
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
        OperationExecutionContext context)
    {
        if (payload.TryGetProperty("registeredOperation", out var operationElement))
        {
            var operationName = operationElement.GetString()
                ?? throw new ScryOperationException("invalid_request", "registeredOperation must be a string.");
            if (!configuration.TryResolveOperation(
                operationName,
                out var operation,
                out var contextualOperation))
            {
                throw new ScryOperationException(
                    "operation_not_found",
                    $"Registered operation '{operationName}' does not exist.");
            }

            if (contextualOperation is not null)
            {
                var contextualArguments = payload.TryGetProperty("arguments", out var contextualArgs)
                    ? contextualArgs
                    : JsonSerializer.SerializeToElement(new { }, ScryJson.Options);
                return new
                {
                    value = Encode(
                        await contextualOperation.Handler(contextualArguments, context).ConfigureAwait(false),
                        session)
                };
            }

            var arguments = payload.TryGetProperty("arguments", out var args)
                ? args
                : JsonSerializer.SerializeToElement(new { }, ScryJson.Options);
            return new
            {
                value = Encode(
                    await operation!.Handler(arguments, context.CancellationToken).ConfigureAwait(false),
                    session,
                    GetOptionalBoolean(payload, "asReference"))
            };
        }

        var subject = ResolveSubject(payload, session);
        var name = RequiredString(payload, "member");
        var argumentElements = payload.TryGetProperty("arguments", out var array)
            ? array.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        var includeNonPublic = GetOptionalBoolean(payload, "includeNonPublic");
        var candidates = TypeHierarchy(subject.GetType())
            .SelectMany(level => level.GetMethods(DeclaredFlags(includeNonPublic)))
            .Where(method => method.Name == name && !method.ContainsGenericParameters)
            .Where(method => method.GetParameters().Length == argumentElements.Length)
            .DistinctByCompatible(
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
            await RuntimeCompatibility.AwaitWithCancellationAsync(task, context.CancellationToken).ConfigureAwait(false);
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
            await RuntimeCompatibility.AwaitWithCancellationAsync(valueTaskAsTask, context.CancellationToken)
                .ConfigureAwait(false);
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
            : new[] { RequiredString(payload, "handleId") };
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
            result.ElapsedMilliseconds,
            result.CompilationCached,
            result.Marshalled,
            result.ThreadId,
            result.CompileMilliseconds,
            result.RunMilliseconds);
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
            result.ElapsedMilliseconds,
            result.CompilationCached);
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
            if (!configuration.TryGetRoot(rootName, out var root))
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
            converted = Array.Empty<object?>();
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

    /// <summary>
    /// Polls a condition until it holds or the budget runs out. Returns an unsatisfied result
    /// rather than an error on timeout, matching the <c>wpf.wait</c>/<c>winforms.wait</c>
    /// convention; use <c>assert</c> when a miss should be a failure.
    /// </summary>
    private async ValueTask<object> WaitAsync(
        JsonElement payload,
        SessionState session,
        CancellationToken cancellationToken)
    {
        var request = Deserialize<ConditionRequest>(payload);
        var comparison = ConditionComparison.Parse(request);
        var timeout = TimeSpan.FromMilliseconds(
            request.TimeoutMilliseconds ?? DefaultWaitTimeoutMilliseconds);
        var pollInterval = TimeSpan.FromMilliseconds(
            request.PollIntervalMilliseconds ?? DefaultPollIntervalMilliseconds);
        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ScryOperationException(
                "invalid_request",
                "pollIntervalMilliseconds must be greater than zero.");
        }

        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        ConditionOutcome outcome;
        while (true)
        {
            attempts++;
            outcome = await EvaluateConditionAsync(request, comparison, session, cancellationToken)
                .ConfigureAwait(false);
            if (outcome.Satisfied || stopwatch.Elapsed >= timeout)
            {
                break;
            }

            // Don't overshoot the budget waiting to poll again.
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                remaining < pollInterval ? remaining : pollInterval,
                cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();
        return new ConditionResult(
            outcome.Satisfied,
            attempts,
            stopwatch.ElapsedMilliseconds,
            Encode(outcome.Value, session),
            comparison.Describe(outcome));
    }

    /// <summary>
    /// Evaluates a condition once and fails the operation when it does not hold, so a failed
    /// assertion is a failed request with a non-zero exit code rather than a success the caller has
    /// to inspect.
    /// </summary>
    private async ValueTask<object> AssertAsync(
        JsonElement payload,
        SessionState session,
        CancellationToken cancellationToken)
    {
        var request = Deserialize<ConditionRequest>(payload);
        var comparison = ConditionComparison.Parse(request);
        var stopwatch = Stopwatch.StartNew();
        var outcome = await EvaluateConditionAsync(request, comparison, session, cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();
        if (!outcome.Satisfied)
        {
            throw new ScryOperationException(
                "assertion_failed",
                comparison.Describe(outcome),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source"] = request.Source,
                    ["operator"] = comparison.Operator
                });
        }

        return new ConditionResult(
            true,
            1,
            stopwatch.ElapsedMilliseconds,
            Encode(outcome.Value, session),
            comparison.Describe(outcome));
    }

    private async ValueTask<ConditionOutcome> EvaluateConditionAsync(
        ConditionRequest request,
        ConditionComparison comparison,
        SessionState session,
        CancellationToken cancellationToken)
    {
        // Reuses evaluate wholesale, including its marshal handling, so a condition can read
        // UI-owned state with "marshal": "ui" without wait's polling loop ever running there.
        var evaluation = new ExecutionRequest(
            request.Source,
            request.Imports,
            request.References,
            Marshal: request.Marshal);
        var result = await execution
            .EvaluateAsync(evaluation, session, cancellationToken)
            .ConfigureAwait(false);
        return new ConditionOutcome(comparison.Matches(result.Value), result.Value);
    }

    private readonly record struct ConditionOutcome(bool Satisfied, object? Value);

    /// <summary>
    /// A parsed condition comparison. Comparison happens against the raw CLR value rather than its
    /// JSON projection, so a bounded preview never changes the verdict.
    /// </summary>
    private sealed class ConditionComparison
    {
        private ConditionComparison(string op, JsonElement? expected)
        {
            Operator = op;
            Expected = expected;
        }

        public string Operator { get; }

        private JsonElement? Expected { get; }

        public static ConditionComparison Parse(ConditionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Source))
            {
                throw new ScryOperationException("invalid_request", "source must be a non-empty expression.");
            }

            var op = string.IsNullOrWhiteSpace(request.Operator)
                ? ConditionOperators.IsTrue
                : request.Operator!;
            var match = ConditionOperators.All.FirstOrDefault(
                candidate => string.Equals(candidate, op, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new ScryOperationException(
                    "invalid_request",
                    $"operator '{op}' is not supported. Supported operators: " +
                    string.Join(", ", ConditionOperators.All) + ".");
            }

            var needsOperand =
                match is ConditionOperators.EqualTo or ConditionOperators.NotEqualTo or ConditionOperators.Contains;
            if (needsOperand && request.Expected is null)
            {
                throw new ScryOperationException(
                    "invalid_request",
                    $"operator '{match}' requires an 'expected' value.");
            }

            return new(match, request.Expected);
        }

        public bool Matches(object? value) => Operator switch
        {
            ConditionOperators.IsNull => value is null,
            ConditionOperators.IsNotNull => value is not null,
            ConditionOperators.IsTrue => value is true,
            ConditionOperators.EqualTo => ValuesEqual(value, Expected!.Value),
            ConditionOperators.NotEqualTo => !ValuesEqual(value, Expected!.Value),
            ConditionOperators.Contains => Contains(value, Expected!.Value),
            _ => false
        };

        public string Describe(ConditionOutcome outcome)
        {
            var actual = outcome.Value is null
                ? "null"
                : Convert.ToString(outcome.Value, CultureInfo.InvariantCulture) ?? outcome.Value.GetType().Name;
            var verb = outcome.Satisfied ? "holds" : "does not hold";
            return Expected is null
                ? $"Condition {Operator} {verb}; the expression produced '{actual}'."
                : $"Condition {Operator} {Expected.Value.ToString()} {verb}; the expression produced '{actual}'.";
        }

        /// <summary>
        /// Compares a CLR value with a JSON operand without going through serialization, so
        /// numeric widening (an int result against a JSON number) and string comparison behave the
        /// way a caller writing JSON expects.
        /// </summary>
        private static bool ValuesEqual(object? value, JsonElement expected)
        {
            switch (expected.ValueKind)
            {
                case JsonValueKind.Null:
                    return value is null;
                case JsonValueKind.True:
                    return value is true;
                case JsonValueKind.False:
                    return value is false;
                case JsonValueKind.String:
                    return value is not null &&
                        string.Equals(
                            Convert.ToString(value, CultureInfo.InvariantCulture),
                            expected.GetString(),
                            StringComparison.Ordinal);
                case JsonValueKind.Number:
                    if (value is null || value is bool || !expected.TryGetDouble(out var operand))
                    {
                        return false;
                    }

                    try
                    {
                        return Math.Abs(Convert.ToDouble(value, CultureInfo.InvariantCulture) - operand) < double.Epsilon;
                    }
                    catch (Exception exception) when (
                        exception is InvalidCastException or FormatException or OverflowException)
                    {
                        return false;
                    }

                default:
                    // Objects and arrays have no meaningful equality against a bounded projection;
                    // say so rather than silently comparing previews.
                    throw new ScryOperationException(
                        "invalid_request",
                        "expected must be a string, number, boolean, or null. Compare structured " +
                        "values inside the expression instead.");
            }
        }

        private static bool Contains(object? value, JsonElement expected)
        {
            if (expected.ValueKind != JsonValueKind.String)
            {
                throw new ScryOperationException(
                    "invalid_request",
                    $"operator '{ConditionOperators.Contains}' requires a string 'expected' value.");
            }

            var text = value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            // string.Contains(string, StringComparison) is .NET Core only.
            return text is not null &&
                text.IndexOf(expected.GetString() ?? string.Empty, StringComparison.Ordinal) >= 0;
        }
    }

    private static JsonElement RequiredProperty(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
            ? value
            : throw new ScryOperationException("invalid_request", $"Required property '{name}' is missing.");

    private static string RequiredString(JsonElement payload, string name) =>
        RequiredProperty(payload, name).GetString()
        ?? throw new ScryOperationException("invalid_request", $"Property '{name}' must be a string.");

    private static string? OptionalString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetOptionalBoolean(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.GetBoolean();

    private static int OptionalInt32(JsonElement payload, string name, int fallback) =>
        payload.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;

    private static T Deserialize<T>(JsonElement payload) =>
        payload.Deserialize<T>(ScryJson.Options)
        ?? throw new ScryOperationException("invalid_request", $"Request payload must be a {typeof(T).Name} object.");
}

internal static class LinqCompatibility
{
    internal static IEnumerable<TSource> DistinctByCompatible<TSource, TKey>(
        this IEnumerable<TSource> source,
        Func<TSource, TKey> keySelector,
        IEqualityComparer<TKey> comparer)
    {
        var keys = new HashSet<TKey>(comparer);
        foreach (var item in source)
        {
            if (keys.Add(keySelector(item)))
            {
                yield return item;
            }
        }
    }
}
