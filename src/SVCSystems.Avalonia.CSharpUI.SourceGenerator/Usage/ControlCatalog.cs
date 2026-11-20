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
    private readonly Dictionary<string, INamedTypeSymbol> _attachedHostsByName;
    private readonly List<CatalogEntry> _localEntries;
    private readonly HashSet<string> _commonTypeNames;

    private ControlCatalog(
        Dictionary<string, List<CatalogEntry>> byFactoryName,
        Dictionary<TypeKey, INamedTypeSymbol> byKey,
        Dictionary<string, INamedTypeSymbol> attachedHostsByName,
        List<CatalogEntry> localEntries,
        HashSet<string> commonTypeNames)
    {
        _byFactoryName = byFactoryName;
        _byKey = byKey;
        _attachedHostsByName = attachedHostsByName;
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
        var attachedHostsByName = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
        var localEntries = new List<CatalogEntry>();
        var localAssembly = compilation.Assembly;
        var assembliesByIdentity = new Dictionary<AssemblyIdentity, IAssemblySymbol>();
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol referencedAssembly)
            {
                assembliesByIdentity[referencedAssembly.Identity] = referencedAssembly;
            }
        }

        foreach (var type in EnumerateClasses(localAssembly.GlobalNamespace, publicOnly: false, skipGeneric: false))
        {
            AddAttachedHostIndex(type, byKey, attachedHostsByName);
            Add(type, isLocal: true, byFactoryName, byKey, localEntries);
        }

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly ||
                SymbolEqualityComparer.Default.Equals(assembly, localAssembly) ||
                !ReferencesAvalonia(assembly, assembliesByIdentity))
            {
                continue;
            }

            foreach (var type in EnumerateClasses(assembly.GlobalNamespace, publicOnly: true, skipGeneric: true))
            {
                AddAttachedHostIndex(type, byKey, attachedHostsByName);
                Add(type, isLocal: false, byFactoryName, byKey, localEntries);
            }
        }

        return new ControlCatalog(byFactoryName, byKey, attachedHostsByName, localEntries, CommonControls.GetTypeNames(compilation));
    }

    internal IReadOnlyList<CatalogEntry> Lookup(string factoryName) =>
        _byFactoryName.TryGetValue(factoryName, out var entries) ? entries : Array.Empty<CatalogEntry>();

    internal bool TryGetByKey(TypeKey key, out INamedTypeSymbol symbol) => _byKey.TryGetValue(key, out symbol!);

    /// <summary>
    /// Resolves a generated attached-property extension name (Host_Property) back to the type that
    /// hosts the Avalonia attached property. Attached setters apply to any control, so using one in
    /// source must bring its host into the generation set.
    /// </summary>
    internal bool TryGetAttachedPropertyHost(string extensionName, out INamedTypeSymbol host) =>
        _attachedHostsByName.TryGetValue(extensionName, out host!);

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

        // Base types are part of the usage closure. Keeping their symbols here (including
        // constructed generics such as GenericBase<int>) lets the generator resolve keys that
        // would otherwise be missing from a catalog of open definitions only.
        for (var baseType = type.BaseType;
             baseType is not null && baseType.SpecialType != SpecialType.System_Object;
             baseType = baseType.BaseType)
        {
            if (baseType.IsAccessibleFromGeneratedCode())
            {
                var baseKey = TypeKey.Create(baseType);
                if (!byKey.ContainsKey(baseKey))
                {
                    byKey.Add(baseKey, baseType);
                }
            }
        }

        var factoryName = ControlModelBuilder.GetMetadata(type).Alias ?? type.Name;
        factoryName = SymbolUtilities.CleanIdentifier(factoryName) ?? type.Name;

        var entry = new CatalogEntry(key, type, isLocal);
        AddName(byFactoryName, factoryName, entry);

        // A referenced control aliased for its factory must still be discoverable by its CLR name.
        AddName(byFactoryName, SymbolUtilities.CleanIdentifier(type.Name) ?? type.Name, entry);
        if (isLocal)
        {
            localEntries.Add(entry);
        }
    }

    private static void AddName(
        Dictionary<string, List<CatalogEntry>> byFactoryName,
        string name,
        CatalogEntry entry)
    {
        if (!byFactoryName.TryGetValue(name, out var entries))
        {
            entries = new List<CatalogEntry>();
            byFactoryName.Add(name, entries);
        }

        if (!entries.Contains(entry))
        {
            entries.Add(entry);
        }
    }

    /// <summary>
    /// Indexes attached-property extension names (Host_Property) so a call like Host_Dock(...)
    /// roots its host even when the host type is never mentioned. Attached setters apply to any
    /// control, and the host can be a control or a plain/static helper class. The naming must match
    /// AttachedPropertyExtensionInfo exactly, including metadata aliases.
    /// </summary>
    private static void AddAttachedHostIndex(
        INamedTypeSymbol type,
        Dictionary<TypeKey, INamedTypeSymbol> byKey,
        Dictionary<string, INamedTypeSymbol> attachedHostsByName)
    {
        if (!type.IsAccessibleFromGeneratedCode())
        {
            return;
        }

        var key = TypeKey.Create(type);
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>().Where(static field => field.IsAttachedPropertyField()))
        {
            var memberName = field.GetMemberName();
            var metadata = ControlModelBuilder.GetMetadata(field);
            if (metadata.Ignore ||
                type.GetMembers($"Set{memberName}").FirstOrDefault() is not IMethodSymbol setter ||
                setter.Parameters.Length == 0)
            {
                continue;
            }

            var extensionName = $"{type.Name.Split('`')[0]}_{metadata.Alias ?? memberName}";
            if (attachedHostsByName.ContainsKey(extensionName))
            {
                continue;
            }

            attachedHostsByName.Add(extensionName, type);

            // Controls are registered by Add(); only non-control hosts need a key here, otherwise
            // Add() would see an existing key and skip their normal control registration.
            if (!type.IsOrInheritsFrom("Avalonia.AvaloniaObject") && !byKey.ContainsKey(key))
            {
                byKey.Add(key, type);
            }
        }
    }

    private static bool ReferencesAvalonia(
        IAssemblySymbol assembly,
        Dictionary<AssemblyIdentity, IAssemblySymbol> assembliesByIdentity,
        HashSet<IAssemblySymbol>? visited = null)
    {
        if (assembly.Name.StartsWith("Avalonia", StringComparison.Ordinal))
        {
            return true;
        }

        visited ??= new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
        if (!visited.Add(assembly))
        {
            return false;
        }

        foreach (var module in assembly.Modules)
        {
            foreach (var reference in module.ReferencedAssemblies)
            {
                if (reference.Name is "Avalonia.Base" or "Avalonia.Controls" or "Avalonia.Markup" or "Avalonia.Themes.Fluent" ||
                    (assembliesByIdentity.TryGetValue(reference, out var referencedAssembly) &&
                     ReferencesAvalonia(referencedAssembly, assembliesByIdentity, visited)))
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
            foreach (var candidate in EnumerateTypeAndNested(type, publicOnly, skipGeneric))
            {
                yield return candidate;
            }
        }

        foreach (var namespaceMember in ns.GetNamespaceMembers())
        {
            foreach (var type in EnumerateClasses(namespaceMember, publicOnly, skipGeneric))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypeAndNested(
        INamedTypeSymbol type,
        bool publicOnly,
        bool skipGeneric)
    {
        if (type.TypeKind != TypeKind.Class ||
            (skipGeneric && type.IsGenericType) ||
            (publicOnly && type.DeclaredAccessibility != Accessibility.Public))
        {
            yield break;
        }

        yield return type;

        foreach (var nested in type.GetTypeMembers())
        {
            foreach (var candidate in EnumerateTypeAndNested(nested, publicOnly, skipGeneric))
            {
                yield return candidate;
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
