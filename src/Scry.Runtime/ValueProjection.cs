using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Scry.Contracts;

namespace Scry.Runtime;

internal static class ValueProjection
{
    private const int MaximumDepth = 4;
    private const int MaximumMembers = 64;
    private const int MaximumStringLength = 1024;

    public static bool IsScalar(Type type) =>
        type.IsPrimitive || type.IsEnum ||
        type == typeof(string) || type == typeof(decimal) ||
        type == typeof(Guid) || type == typeof(DateTime) ||
        type == typeof(DateTimeOffset) || type == typeof(TimeSpan) ||
        type == typeof(DateOnly) || type == typeof(TimeOnly) ||
        type == typeof(Half) || type == typeof(Int128) || type == typeof(UInt128) ||
        type == typeof(Uri);

    public static JsonElement Project(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.GetType().IsValueType)
        {
            throw new ArgumentException("Only value types can be projected.", nameof(value));
        }

        var state = new ProjectionState();
        return JsonSerializer.SerializeToElement(state.Project(value, 0), ScryJson.Options);
    }

    private sealed class ProjectionState
    {
        private int _remainingMembers = MaximumMembers;

        public object? Project(object? value, int depth)
        {
            if (value is null)
            {
                return null;
            }

            var type = value.GetType();
            if (IsScalar(type))
            {
                return value switch
                {
                    string text => ProjectText(text),
                    Uri uri => ProjectText(uri.OriginalString),
                    _ => value
                };
            }

            if (!type.IsValueType)
            {
                return new Dictionary<string, object?>
                {
                    ["$type"] = TypeName(type),
                    ["$reference"] = true
                };
            }

            if (depth >= MaximumDepth)
            {
                return Truncated(type, "depth");
            }

            var result = new Dictionary<string, object?>
            {
                ["$type"] = TypeName(type)
            };
            var members = GetMembers(type);
            foreach (var member in members)
            {
                if (_remainingMembers == 0)
                {
                    result["$truncated"] = "member-limit";
                    break;
                }

                _remainingMembers--;
                result[member.Name] = ProjectMember(member, value, depth + 1);
            }

            return result;
        }

        private static IReadOnlyList<MemberInfo> GetMembers(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            var properties = type.GetProperties(flags)
                .Where(property =>
                    property.GetMethod?.IsPublic == true &&
                    property.GetIndexParameters().Length == 0)
                .Cast<MemberInfo>();
            var fields = type.GetFields(flags)
                .Where(field => !field.IsStatic)
                .Cast<MemberInfo>();
            return properties
                .Concat(fields)
                .DistinctBy(member => member.Name, StringComparer.Ordinal)
                .OrderBy(member => member.Name, StringComparer.Ordinal)
                .ToArray();
        }

        private object? ProjectMember(MemberInfo member, object value, int depth)
        {
            if (member is PropertyInfo { PropertyType.IsByRefLike: true } byRefProperty)
            {
                return Error(
                    new NotSupportedException(
                        $"Byref-like member '{byRefProperty.Name}' cannot be projected."));
            }

            try
            {
                var memberValue = member switch
                {
                    PropertyInfo property => property.GetValue(value),
                    FieldInfo field => field.GetValue(value),
                    _ => throw new UnreachableException()
                };
                return Project(memberValue, depth);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                return Error(exception.InnerException);
            }
            catch (Exception exception) when (
                exception is NotSupportedException or MemberAccessException or ArgumentException)
            {
                return Error(exception);
            }
        }

        private static Dictionary<string, object?> Error(Exception exception) =>
            new()
            {
                ["$error"] = TypeName(exception.GetType()),
                ["$message"] = Truncate(exception.Message)
            };

        private static Dictionary<string, object?> Truncated(Type type, string reason) =>
            new()
            {
                ["$type"] = TypeName(type),
                ["$truncated"] = reason
            };

        private static string TypeName(Type type) => type.FullName ?? type.Name;

        private static string Truncate(string value) =>
            value.Length <= MaximumStringLength
                ? value
                : value[..MaximumStringLength];

        private static object ProjectText(string value) =>
            value.Length <= MaximumStringLength
                ? value
                : new Dictionary<string, object?>
                {
                    ["$value"] = Truncate(value),
                    ["$truncated"] = "string"
                };
    }
}
