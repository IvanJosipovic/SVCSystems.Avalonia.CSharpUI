using Microsoft.CodeAnalysis;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.Analysis;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Usage;

/// <summary>
/// A cheap index of candidate control types in the current compilation. Building the catalog
/// only walks type symbols and reads one attribute; no <see cref="ControlModel"/> is built here.
/// </summary>
internal sealed class ControlCatalog
{
    private readonly Dictionary<string, List<CatalogEntry>> _byFactoryName;
    private readonly Dictionary<TypeKey, INamedTypeSymbol> _byKey;
    private readonly List<CatalogEntry> _localEntries;
    private readonly HashSet<string> _commonTypeNames;

    private ControlCatalog(
        Dictionary<string, List<CatalogEntry>> byFactoryName,
        Dictionary<TypeKey, INamedTypeSymbol> byKey,
        List<CatalogEntry> localEntries,
        HashSet<string> commonTypeNames)
    {
        _byFactoryName = byFactoryName;
        _byKey = byKey;
        _localEntries = localEntries;
        _commonTypeNames = commonTypeNames;
    }

    internal IReadOnlyList<CatalogEntry> LocalEntries => _localEntries;

    /// <summary>
    /// Curated common controls that exist in this compilation (see <see cref="CommonControls"/>).
    /// </summary>
    internal IEnumerable<INamedTypeSymbol> CommonTypes()
    {
        foreach (var pair in _byKey)
        {
            if (_commonTypeNames.Contains(pair.Key.TypeName))
            {
                yield return pair.Value;
            }
        }
    }

    internal static ControlCatalog Build(Compilation compilation)
    {
        var byFactoryName = new Dictionary<string, List<CatalogEntry>>(StringComparer.Ordinal);
        var byKey = new Dictionary<TypeKey, INamedTypeSymbol>();
        var localEntries = new List<CatalogEntry>();
        var localAssembly = compilation.Assembly;

        foreach (var type in EnumerateClasses(localAssembly.GlobalNamespace, publicOnly: false, skipGeneric: false))
        {
            Add(type, isLocal: true, byFactoryName, byKey, localEntries);
        }

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly ||
                SymbolEqualityComparer.Default.Equals(assembly, localAssembly) ||
                !ReferencesAvalonia(assembly))
            {
                continue;
            }

            foreach (var type in EnumerateClasses(assembly.GlobalNamespace, publicOnly: true, skipGeneric: true))
            {
                Add(type, isLocal: false, byFactoryName, byKey, localEntries);
            }
        }

        return new ControlCatalog(byFactoryName, byKey, localEntries, CommonControls.GetTypeNames(compilation));
    }

    internal IReadOnlyList<CatalogEntry> Lookup(string factoryName) =>
        _byFactoryName.TryGetValue(factoryName, out var entries) ? entries : Array.Empty<CatalogEntry>();

    internal bool TryGetByKey(TypeKey key, out INamedTypeSymbol symbol) => _byKey.TryGetValue(key, out symbol!);

    private static void Add(
        INamedTypeSymbol type,
        bool isLocal,
        Dictionary<string, List<CatalogEntry>> byFactoryName,
        Dictionary<TypeKey, INamedTypeSymbol> byKey,
        List<CatalogEntry> localEntries)
    {
        if (!type.IsOrInheritsFrom("Avalonia.AvaloniaObject") ||
            !type.IsAccessibleFromGeneratedCode())
        {
            return;
        }

        var key = TypeKey.Create(type);
        if (byKey.ContainsKey(key))
        {
            return;
        }

        byKey.Add(key, type);

        var factoryName = ControlModelBuilder.GetMetadata(type).Alias ?? type.Name;
        factoryName = SymbolUtilities.CleanIdentifier(factoryName) ?? type.Name;

        var entry = new CatalogEntry(key, type, isLocal);
        if (!byFactoryName.TryGetValue(factoryName, out var entries))
        {
            entries = new List<CatalogEntry>();
            byFactoryName.Add(factoryName, entries);
        }

        entries.Add(entry);
        if (isLocal)
        {
            localEntries.Add(entry);
        }
    }

    private static bool ReferencesAvalonia(IAssemblySymbol assembly)
    {
        if (assembly.Name.StartsWith("Avalonia", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var module in assembly.Modules)
        {
            foreach (var reference in module.ReferencedAssemblies)
            {
                if (reference.Name is "Avalonia.Base" or "Avalonia.Controls" or "Avalonia.Markup" or "Avalonia.Themes.Fluent")
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateClasses(
        INamespaceSymbol ns,
        bool publicOnly,
        bool skipGeneric)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            if (type.TypeKind != TypeKind.Class ||
                (skipGeneric && type.IsGenericType) ||
                (publicOnly && type.DeclaredAccessibility != Accessibility.Public))
            {
                continue;
            }

            yield return type;
        }

        foreach (var namespaceMember in ns.GetNamespaceMembers())
        {
            foreach (var type in EnumerateClasses(namespaceMember, publicOnly, skipGeneric))
            {
                yield return type;
            }
        }
    }

    internal readonly struct CatalogEntry(TypeKey key, INamedTypeSymbol symbol, bool isLocal)
    {
        internal TypeKey Key { get; } = key;

        internal INamedTypeSymbol Symbol { get; } = symbol;

        internal bool IsLocal { get; } = isLocal;
    }
}
