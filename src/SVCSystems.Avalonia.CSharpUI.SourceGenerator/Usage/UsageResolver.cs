using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.InternalGenerators;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Usage;

/// <summary>
/// Maps simple names from source to control types and closes the result over base types.
/// Base-type closure is required because fluent setters are emitted on the type that declares
/// the Avalonia property: Button().Margin(...) needs an extension on Layoutable, which is a
/// base type of Button.
/// </summary>
internal static class UsageResolver
{
    private readonly struct CacheKey : IEquatable<CacheKey>
    {
        internal CacheKey(ulong references, ulong names, ulong locals)
        {
            References = references;
            Names = names;
            Locals = locals;
        }

        internal ulong References { get; }
        internal ulong Names { get; }
        internal ulong Locals { get; }

        public bool Equals(CacheKey other) =>
            References == other.References && Names == other.Names && Locals == other.Locals;

        public override bool Equals(object? obj) => obj is CacheKey other && Equals(other);

        public override int GetHashCode() => unchecked((int)(References ^ Names ^ Locals));
    }

    // A catalog is rebuilt whenever source can affect usage. Scoping the cache to the catalog
    // instance guarantees that no inheritance, local-control or common-type change is missed.
    private static readonly ConditionalWeakTable<ControlCatalog, ConcurrentDictionary<CacheKey, UsageResult>> Cache = new();

    internal static UsageResult Resolve(
        ControlCatalog catalog,
        ImmutableArray<string> names,
        CancellationToken cancellationToken = default,
        ulong referenceHash = 0)
    {
        var cache = Cache.GetValue(catalog, static _ => new ConcurrentDictionary<CacheKey, UsageResult>());
        var key = new CacheKey(
            referenceHash,
            HashNames(names),
            HashLocalControls(catalog));
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = ResolveCore(catalog, names, cancellationToken);
        return cache.GetOrAdd(key, result);
    }

    private static ulong HashNames(ImmutableArray<string> names)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var name in names)
        {
            foreach (var ch in name)
            {
                hash = (hash ^ ch) * prime;
            }

            hash = (hash ^ 0xFFFF) * prime;
        }

        return hash;
    }

    private static ulong HashLocalControls(ControlCatalog catalog)
    {
        const ulong prime = 1099511628211UL;
        var hash = 14695981039346656037UL;
        foreach (var entry in catalog.LocalEntries)
        {
            foreach (var ch in entry.Key.AssemblyName)
            {
                hash = (hash ^ ch) * prime;
            }

            foreach (var ch in entry.Key.TypeName)
            {
                hash = (hash ^ ch) * prime;
            }

            hash = (hash ^ 0xFFFF) * prime;
        }

        return hash;
    }

    private static UsageResult ResolveCore(
        ControlCatalog catalog,
        ImmutableArray<string> names,
        CancellationToken cancellationToken)
    {
        var keys = new HashSet<TypeKey>();
        var ambiguous = new SortedSet<string>(StringComparer.Ordinal);

        // Common Avalonia controls are always available, even when usage detection cannot see
        // them (compiled XAML, reflection, runtime-composed views).
        foreach (var type in catalog.CommonTypes())
        {
            AddWithClosure(type, keys);
        }

        // Local controls belong to the application itself, so their declarations are roots.
        foreach (var entry in catalog.LocalEntries)
        {
            AddWithClosure(entry.Symbol, keys);
        }

        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // An attached-property extension name (Host_Property) must root its host even when the
            // host type is never mentioned, because the generated setter applies to any control.
            if (catalog.TryGetAttachedPropertyHost(name, out var attachedHost))
            {
                if (attachedHost.IsOrInheritsFrom("Avalonia.AvaloniaObject"))
                {
                    AddWithClosure(attachedHost, keys);
                }
                else
                {
                    // Attached properties can live on a plain/static helper class. Such a host has
                    // no Avalonia base chain, but its model still emits the attached setters.
                    keys.Add(TypeKey.Create(attachedHost));
                }
            }

            var entries = catalog.Lookup(name);
            if (entries.Count == 0)
            {
                continue;
            }

            if (entries.Count == 1)
            {
                AddWithClosure(entries[0].Symbol, keys);
                continue;
            }

            // Include every candidate: preferring a local or Avalonia.Controls match would drop
            // controls that source names explicitly (e.g. Vendor.Gauge with a local Other.Gauge).
            // Never emit two extension classes with the same generated name (same namespace and
            // type name in different assemblies).
            var emittedClassNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var extensionClassName = SymbolUtilities.BuildExtensionClassName(entry.Symbol);
                if (emittedClassNames.Add(extensionClassName))
                {
                    AddWithClosure(entry.Symbol, keys);
                }
            }

            ambiguous.Add(name);
        }

        var ordered = keys
            .OrderBy(static key => key.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static key => key.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();

        return new UsageResult(ordered, ambiguous.ToImmutableArray());
    }

    private static void AddWithClosure(INamedTypeSymbol type, HashSet<TypeKey> keys)
    {
        // Inheritance is transitive: if the root derives from AvaloniaObject, every ancestor in
        // this chain does too. One check replaces the old O(depth^2) check-per-ancestor.
        var isAvaloniaObject = type.IsOrInheritsFrom("Avalonia.AvaloniaObject");
        if (!isAvaloniaObject)
        {
            return;
        }

        for (INamedTypeSymbol? current = type;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            if (current.IsAccessibleFromGeneratedCode())
            {
                keys.Add(TypeKey.Create(current));
            }

            // Bases above AvaloniaObject do not themselves inherit from it; the old loop stopped
            // adding them. Stop here to preserve exactly the same key set.
            if (current.Name == "AvaloniaObject" &&
                current.ContainingNamespace?.ToDisplayString() == "Avalonia")
            {
                break;
            }
        }
    }
}
