using System.Collections.Concurrent;
using Microsoft.CodeAnalysis.Scripting;
using Scry.Contracts;

namespace Scry.Runtime;

/// <summary>
/// Caches compiled Roslyn scripts so repeating a submission does not recompile it.
/// <para>
/// The motivating case is <c>wait</c>, which re-evaluates one expression until it holds. Against a
/// real application each attempt was paying a full compile, so a poll loop cost seconds per attempt
/// rather than milliseconds. Ordinary repeated <c>evaluate</c> calls benefit the same way.
/// </para>
/// <para>
/// Only successful compilations are cached, which is what keeps the cache honest about a changing
/// process. A submission that failed to compile because its type was in an assembly not yet loaded
/// is recompiled next time and can then succeed; a submission that already compiled stays valid,
/// because the assemblies it bound to cannot be unloaded from the default AppDomain and its
/// resolved references do not need to see anything newer.
/// </para>
/// </summary>
internal sealed class ScriptCache(int maximumEntries)
{
    private readonly ConcurrentDictionary<ScriptCacheKey, CacheEntry> _entries = new();
    private readonly int _maximumEntries = maximumEntries < 1 ? 1 : maximumEntries;
    private long _sequence;

    public int Count => _entries.Count;

    public bool TryGet(
        ScriptCacheKey key,
        out Script<object?> script,
        out IReadOnlyList<CompilationDiagnostic> diagnostics)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            // Touch, so eviction sheds genuinely cold entries rather than merely old ones.
            entry.LastUsed = Interlocked.Increment(ref _sequence);
            script = entry.Script;
            diagnostics = entry.Diagnostics;
            return true;
        }

        script = null!;
        diagnostics = Array.Empty<CompilationDiagnostic>();
        return false;
    }

    public void Add(
        ScriptCacheKey key,
        Script<object?> script,
        IReadOnlyList<CompilationDiagnostic> diagnostics)
    {
        _entries[key] = new(script, diagnostics) { LastUsed = Interlocked.Increment(ref _sequence) };
        Evict();
    }

    /// <summary>
    /// Keeps the cache bounded so a client sending many distinct submissions cannot grow it without
    /// limit. Approximate least-recently-used: an entry can be touched concurrently with this, which
    /// at worst evicts a slightly warmer entry than intended and costs one recompile.
    /// </summary>
    private void Evict()
    {
        if (_entries.Count <= _maximumEntries)
        {
            return;
        }

        foreach (var candidate in _entries
            .OrderBy(pair => pair.Value.LastUsed)
            .Take(_entries.Count - _maximumEntries)
            .ToArray())
        {
            _entries.TryRemove(candidate.Key, out _);
        }
    }

    private sealed class CacheEntry(
        Script<object?> script,
        IReadOnlyList<CompilationDiagnostic> diagnostics)
    {
        public Script<object?> Script { get; } = script;

        public IReadOnlyList<CompilationDiagnostic> Diagnostics { get; } = diagnostics;

        public long LastUsed { get; set; }
    }
}

/// <summary>
/// Everything that affects how a submission compiles. The globals type is fixed, and the ambient
/// loaded-assembly set is deliberately not part of the key - see <see cref="ScriptCache"/> for why
/// a successful compilation stays valid as the process loads more assemblies.
/// </summary>
internal readonly struct ScriptCacheKey : IEquatable<ScriptCacheKey>
{
    /// <summary>
    /// Separates joined list entries. A control character rather than punctuation, so that
    /// {"AB","C"} and {"A","BC"} cannot collide into one key via a separator that could plausibly
    /// occur inside a namespace or an assembly name.
    /// </summary>
    private static readonly string Separator = ((char)0x1F).ToString();

    private readonly string _source;
    private readonly string _imports;
    private readonly string _references;

    public ScriptCacheKey(
        string source,
        IEnumerable<string> imports,
        IReadOnlyList<string>? references)
    {
        // The already-wrapped source, so an expression and a statement body cannot share a key.
        _source = source;

        // Joined rather than compared element-wise, so the key stays a cheap comparable struct.
        _imports = string.Join(Separator, imports);
        _references = references is null ? string.Empty : string.Join(Separator, references);
    }

    public bool Equals(ScriptCacheKey other) =>
        string.Equals(_source, other._source, StringComparison.Ordinal) &&
        string.Equals(_imports, other._imports, StringComparison.Ordinal) &&
        string.Equals(_references, other._references, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ScriptCacheKey other && Equals(other);

    public override int GetHashCode()
    {
        // Hand-rolled rather than HashCode.Combine, which does not exist on .NET Framework.
        unchecked
        {
            var hash = 17;
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(_source);
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(_imports);
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(_references);
            return hash;
        }
    }
}
