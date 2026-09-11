namespace Scry.Contracts;

/// <summary>
/// Comparison applied to the value a condition's expression produced.
/// </summary>
public static class ConditionOperators
{
    public const string IsTrue = "isTrue";
    // Named EqualTo/NotEqualTo rather than Equals/NotEquals so they do not hide object.Equals;
    // the wire values are unchanged.
    public const string EqualTo = "equals";
    public const string NotEqualTo = "notEquals";
    public const string Contains = "contains";
    public const string IsNull = "isNull";
    public const string IsNotNull = "isNotNull";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
        new[] { IsTrue, EqualTo, NotEqualTo, Contains, IsNull, IsNotNull });
}

/// <summary>
/// A framework-neutral validation condition: a C# expression plus a comparison against its result.
/// <para>
/// Deliberately expression-based rather than member-path based. It is the only shape that works
/// identically in a console, service or worker process - the plan's first-class cases, which have
/// no UI tree to query - while still being able to assert on view-model state in a desktop process,
/// which the <c>wpf.*</c> and <c>winforms.*</c> conditions cannot do because they only see the
/// bounded UI-tree projection.
/// </para>
/// </summary>
/// <param name="Source">C# expression evaluated against the target.</param>
/// <param name="Operator">
/// One of <see cref="ConditionOperators"/>. Defaults to <see cref="ConditionOperators.IsTrue"/>,
/// so a boolean expression needs nothing else.
/// </param>
/// <param name="Expected">
/// Comparison operand for <c>equals</c>, <c>notEquals</c> and <c>contains</c>.
/// </param>
/// <param name="TimeoutMilliseconds">Total budget for <c>wait</c>; ignored by <c>assert</c>.</param>
/// <param name="PollIntervalMilliseconds">Delay between <c>wait</c> attempts.</param>
/// <param name="Imports">Extra namespaces for the expression, as for evaluate.</param>
/// <param name="References">Extra assembly references for the expression, as for evaluate.</param>
/// <param name="Marshal">
/// Optional thread target for each evaluation, as for evaluate. Note this marshals the individual
/// evaluations, not the wait loop, so polling never occupies the UI thread between attempts.
/// </param>
public sealed record ConditionRequest(
    string Source,
    string? Operator = null,
    System.Text.Json.JsonElement? Expected = null,
    int? TimeoutMilliseconds = null,
    int? PollIntervalMilliseconds = null,
    IReadOnlyList<string>? Imports = null,
    IReadOnlyList<string>? References = null,
    string? Marshal = null);

/// <summary>
/// Outcome of a <c>wait</c> or <c>assert</c>.
/// </summary>
/// <param name="Satisfied">Whether the comparison held.</param>
/// <param name="Attempts">Number of evaluations performed; always 1 for <c>assert</c>.</param>
/// <param name="ElapsedMilliseconds">Wall-clock time spent.</param>
/// <param name="Value">The projected value of the last evaluation.</param>
/// <param name="Description">Human-readable account of what was compared, for failure messages.</param>
public sealed record ConditionResult(
    bool Satisfied,
    int Attempts,
    long ElapsedMilliseconds,
    RemoteValue? Value,
    string Description);
