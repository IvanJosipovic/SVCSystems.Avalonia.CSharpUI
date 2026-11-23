using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Usage;

/// <summary>
/// A compilation-independent identity for a type. Incremental generator values must not hold
/// Roslyn symbols, so usage analysis is stored as stable string keys and re-resolved against
/// the current compilation inside the source-output stage.
/// </summary>
internal readonly struct TypeKey : IEquatable<TypeKey>
{
    private sealed class KeyBox
    {
        internal KeyBox(TypeKey key) => Key = key;

        internal TypeKey Key { get; }
    }

    private static readonly ConditionalWeakTable<INamedTypeSymbol, KeyBox> Cache = new();

    private readonly string _assemblyName;
    private readonly string _typeName;

    internal TypeKey(string assemblyName, string typeName)
    {
        _assemblyName = assemblyName ?? string.Empty;
        _typeName = typeName ?? string.Empty;
    }

    internal string AssemblyName => _assemblyName;

    internal string TypeName => _typeName;

    internal static TypeKey Create(INamedTypeSymbol type) =>
        Cache.GetValue(type, static symbol =>
        {
            var typeName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (typeName.StartsWith("global::", StringComparison.Ordinal))
            {
                typeName = typeName.Substring("global::".Length);
            }

            return new KeyBox(new TypeKey(
                symbol.ContainingAssembly?.Identity.Name ?? string.Empty,
                typeName));
        }).Key;

    public bool Equals(TypeKey other) =>
        StringComparer.Ordinal.Equals(_assemblyName, other._assemblyName) &&
        StringComparer.Ordinal.Equals(_typeName, other._typeName);

    public override bool Equals(object? obj) => obj is TypeKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return (StringComparer.Ordinal.GetHashCode(_assemblyName) * 397) ^
                   StringComparer.Ordinal.GetHashCode(_typeName);
        }
    }

    public override string ToString() => $"{_assemblyName}|{_typeName}";
}

/// <summary>
/// The resolved set of control types to generate for, plus names that could not be
/// disambiguated. Equality is value-based so Roslyn can cache incremental results.
/// </summary>
internal sealed class UsageResult : IEquatable<UsageResult>
{
    private readonly int _hash;

    internal static readonly UsageResult Empty = new(
        ImmutableArray<TypeKey>.Empty,
        ImmutableArray<string>.Empty);

    internal UsageResult(ImmutableArray<TypeKey> keys, ImmutableArray<string> ambiguousNames)
    {
        Keys = keys;
        AmbiguousNames = ambiguousNames;

        unchecked
        {
            var hash = 17;
            foreach (var key in keys)
            {
                hash = (hash * 31) + key.GetHashCode();
            }

            foreach (var name in ambiguousNames)
            {
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(name);
            }

            _hash = hash;
        }
    }

    internal ImmutableArray<TypeKey> Keys { get; }

    internal ImmutableArray<string> AmbiguousNames { get; }

    public bool Equals(UsageResult? other) =>
        other is not null &&
        Keys.SequenceEqual(other.Keys) &&
        AmbiguousNames.SequenceEqual(other.AmbiguousNames);

    public override bool Equals(object? obj) => obj is UsageResult other && Equals(other);

    public override int GetHashCode() => _hash;
}
