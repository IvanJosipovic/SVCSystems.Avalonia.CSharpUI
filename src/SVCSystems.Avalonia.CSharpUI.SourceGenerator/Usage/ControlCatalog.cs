using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.Analysis;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Usage;

/// <summary>
/// A cheap index of candidate control types in the current compilation. Building the catalog
/// only walks type symbols and reads one attribute; no <see cref="ControlModel"/> is built here.
/// </summary>
internal sealed class ControlCatalog
{
    /// <summary>
    /// The metadata-derived half of the catalog. Roslyn reuses metadata assembly symbols across
    /// compilations that share references, so this index is built once per assembly symbol and
    /// merged into each compilation's catalog instead of re-walking every public type.
    /// </summary>
    private sealed class CachedAssembly
    {
        internal Dictionary<string, List<CatalogEntry>> ByFactoryName = new(StringComparer.Ordinal);
        internal Dictionary<TypeKey, INamedTypeSymbol> ByKey = new();
        internal Dictionary<string, INamedTypeSymbol> AttachedHostsByName = new(StringComparer.Ordinal);
    }

    private static readonly ConditionalWeakTable<IAssemblySymbol, CachedAssembly> MetadataCatalogCache = new();

    /// <summary>Merged metadata index for one reference set, reused across edits.</summary>
    private sealed class MetadataCatalog
    {
        internal Dictionary<string, List<CatalogEntry>> ByFactoryName = new(StringComparer.Ordinal);
        internal Dictionary<TypeKey, INamedTypeSymbol> ByKey = new();
        internal Dictionary<string, INamedTypeSymbol> AttachedHostsByName = new(StringComparer.Ordinal);
        internal INamedTypeSymbol[] CommonTypes = [];
    }

    private static readonly ConcurrentDictionary<ulong, MetadataCatalog> MetadataByReferences = new();

    private readonly Dictionary<string, List<CatalogEntry>> _localByFactoryName;
    private readonly Dictionary<TypeKey, INamedTypeSymbol> _localByKey;
    private readonly Dictionary<string, INamedTypeSymbol> _localAttachedHostsByName;
    private readonly List<CatalogEntry> _localEntries;
    private readonly INamedTypeSymbol[] _commonTypes;
    private readonly MetadataCatalog? _metadata;

    private ControlCatalog(
        Dictionary<string, List<CatalogEntry>> byFactoryName,
        Dictionary<TypeKey, INamedTypeSymbol> byKey,
        Dictionary<string, INamedTypeSymbol> attachedHostsByName,
        List<CatalogEntry> localEntries,
        INamedTypeSymbol[] commonTypes,
        MetadataCatalog? metadata)
    {
        _localByFactoryName = byFactoryName;
        _localByKey = byKey;
        _localAttachedHostsByName = attachedHostsByName;
        _localEntries = localEntries;
        _commonTypes = commonTypes;
        _metadata = metadata;
    }

    internal IReadOnlyList<CatalogEntry> LocalEntries => _localEntries;

    /// <summary>
    /// Curated common controls that exist in this compilation (see <see cref="CommonControls"/>).
    /// </summary>
    internal IReadOnlyList<INamedTypeSymbol> CommonTypes() => _commonTypes;

    internal static ControlCatalog Build(
        Compilation compilation,
        CancellationToken cancellationToken = default,
        ulong referenceHash = 0)
    {
        var localByFactoryName = new Dictionary<string, List<CatalogEntry>>(StringComparer.Ordinal);
        var localByKey = new Dictionary<TypeKey, INamedTypeSymbol>();
        var localAttachedHosts = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
        var localEntries = new List<CatalogEntry>();
        var localAssembly = compilation.Assembly;

        foreach (var type in EnumerateClasses(localAssembly.GlobalNamespace, publicOnly: false, skipGeneric: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddAttachedHostIndex(type, type.GetMembers(), localByKey, localAttachedHosts);
            Add(type, isLocal: true, localByFactoryName, localByKey, localEntries);
        }

        var commonTypeNames = CommonControls.GetTypeNames(compilation);

        MetadataCatalog? metadata;
        if (referenceHash != 0 && MetadataByReferences.TryGetValue(referenceHash, out var cachedMetadata))
        {
            metadata = cachedMetadata;
        }
        else
        {
            var merged = new MetadataCatalog();
            Dictionary<AssemblyIdentity, IAssemblySymbol>? assembliesByIdentity = null;
            Dictionary<IAssemblySymbol, bool> referencesAvaloniaCache = new(SymbolEqualityComparer.Default);

            foreach (var reference in compilation.References)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly ||
                    SymbolEqualityComparer.Default.Equals(assembly, localAssembly))
                {
                    continue;
                }

                if (MetadataCatalogCache.TryGetValue(assembly, out var cached))
                {
                    Merge(cached);
                    continue;
                }

                assembliesByIdentity ??= BuildIdentityMap(compilation);
                if (!ReferencesAvalonia(assembly, assembliesByIdentity, referencesAvaloniaCache))
                {
                    continue;
                }

                var assemblyEntries = new CachedAssembly();
                foreach (var type in EnumerateClasses(assembly.GlobalNamespace, publicOnly: true, skipGeneric: true))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddAttachedHostIndex(type, type.GetMembers(), assemblyEntries.ByKey, assemblyEntries.AttachedHostsByName);
                    Add(type, isLocal: false, assemblyEntries.ByFactoryName, assemblyEntries.ByKey, localEntries: null);
                }

                MetadataCatalogCache.GetValue(assembly, _ => assemblyEntries);
                Merge(assemblyEntries);
            }

            merged.CommonTypes = [.. merged.ByKey
                .Where(pair => commonTypeNames.Contains(pair.Key.TypeName))
                .Select(pair => pair.Value)];

            if (referenceHash != 0)
            {
                if (MetadataByReferences.Count > 8)
                {
                    MetadataByReferences.Clear();
                }

                MetadataByReferences.GetOrAdd(referenceHash, merged);
            }

            metadata = merged;

            void Merge(CachedAssembly source)
            {
                foreach (var pair in source.ByFactoryName)
                {
                    if (!merged.ByFactoryName.TryGetValue(pair.Key, out var entries))
                    {
                        entries = new List<CatalogEntry>();
                        merged.ByFactoryName.Add(pair.Key, entries);
                    }

                    foreach (var entry in pair.Value)
                    {
                        if (!entries.Contains(entry))
                        {
                            entries.Add(entry);
                        }
                    }
                }

                foreach (var pair in source.ByKey)
                {
                    if (!merged.ByKey.ContainsKey(pair.Key))
                    {
                        merged.ByKey.Add(pair.Key, pair.Value);
                    }
                }

                foreach (var pair in source.AttachedHostsByName)
                {
                    if (!merged.AttachedHostsByName.ContainsKey(pair.Key))
                    {
                        merged.AttachedHostsByName.Add(pair.Key, pair.Value);
                    }
                }
            }
        }

        var common = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var pair in localByKey)
        {
            if (commonTypeNames.Contains(pair.Key.TypeName))
            {
                common.Add(pair.Value);
            }
        }

        if (metadata is not null)
        {
            foreach (var type in metadata.CommonTypes)
            {
                common.Add(type);
            }
        }

        return new ControlCatalog(
            localByFactoryName, localByKey, localAttachedHosts, localEntries, [.. common], metadata);
    }

    internal IReadOnlyList<CatalogEntry> Lookup(string factoryName)
    {
        var hasLocal = _localByFactoryName.TryGetValue(factoryName, out var local);
        List<CatalogEntry>? fromMetadata = null;
        var hasMetadata = _metadata is not null &&
                          _metadata.ByFactoryName.TryGetValue(factoryName, out fromMetadata);
        if (hasLocal && hasMetadata)
        {
            var combined = new List<CatalogEntry>(local!.Count + fromMetadata!.Count);
            combined.AddRange(local);
            combined.AddRange(fromMetadata);
            return combined;
        }

        if (hasLocal)
        {
            return local!;
        }

        return hasMetadata ? fromMetadata! : Array.Empty<CatalogEntry>();
    }

    internal bool TryGetByKey(TypeKey key, out INamedTypeSymbol symbol) =>
        _localByKey.TryGetValue(key, out symbol!) ||
        (_metadata is not null && _metadata.ByKey.TryGetValue(key, out symbol!));

    /// <summary>
    /// Resolves a generated attached-property extension name (Host_Property) back to the type that
    /// hosts the Avalonia attached property. Attached setters apply to any control, so using one in
    /// source must bring its host into the generation set.
    /// </summary>
    internal bool TryGetAttachedPropertyHost(string extensionName, out INamedTypeSymbol host) =>
        _localAttachedHostsByName.TryGetValue(extensionName, out host!) ||
        (_metadata is not null && _metadata.AttachedHostsByName.TryGetValue(extensionName, out host!));
    private static void Add(
        INamedTypeSymbol type,
        bool isLocal,
        Dictionary<string, List<CatalogEntry>> byFactoryName,
        Dictionary<TypeKey, INamedTypeSymbol> byKey,
        List<CatalogEntry>? localEntries)
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
        if (isLocal && localEntries is not null)
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
        System.Collections.Immutable.ImmutableArray<ISymbol> members,
        Dictionary<TypeKey, INamedTypeSymbol> byKey,
        Dictionary<string, INamedTypeSymbol> attachedHostsByName)
    {
        if (!type.IsAccessibleFromGeneratedCode())
        {
            return;
        }

        // Build the method-name index once instead of scanning GetMembers("SetX") per field.
        HashSet<string>? methodNames = null;
        foreach (var member in members)
        {
            if (member is IMethodSymbol method && method.Parameters.Length > 0)
            {
                methodNames ??= new HashSet<string>(StringComparer.Ordinal);
                methodNames.Add(method.Name);
            }
        }

        var key = TypeKey.Create(type);
        foreach (var field in members.OfType<IFieldSymbol>())
        {
            if (!field.IsAttachedPropertyField())
            {
                continue;
            }

            var memberName = field.GetMemberName();
            var metadata = ControlModelBuilder.GetMetadata(field);
            var setterName = "Set" + memberName;
            if (metadata.Ignore ||
                methodNames is null || !methodNames.Contains(setterName))
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

    private static Dictionary<AssemblyIdentity, IAssemblySymbol> BuildIdentityMap(Compilation compilation)
    {
        var result = new Dictionary<AssemblyIdentity, IAssemblySymbol>();
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
            {
                result[assembly.Identity] = assembly;
            }
        }

        return result;
    }

    private static bool ReferencesAvalonia(
        IAssemblySymbol assembly,
        Dictionary<AssemblyIdentity, IAssemblySymbol> assembliesByIdentity,
        Dictionary<IAssemblySymbol, bool> memo)
    {
        if (assembly.Name.StartsWith("Avalonia", StringComparison.Ordinal))
        {
            return true;
        }

        if (memo.TryGetValue(assembly, out var cached))
        {
            return cached;
        }

        // Mark false before recursing so reference cycles terminate; the real result replaces it.
        memo[assembly] = false;
        var result = false;
        foreach (var module in assembly.Modules)
        {
            foreach (var reference in module.ReferencedAssemblies)
            {
                if (reference.Name is "Avalonia.Base" or "Avalonia.Controls" or "Avalonia.Markup" or "Avalonia.Themes.Fluent")
                {
                    result = true;
                    break;
                }

                if (assembliesByIdentity.TryGetValue(reference, out var referencedAssembly) &&
                    ReferencesAvalonia(referencedAssembly, assembliesByIdentity, memo))
                {
                    result = true;
                    break;
                }
            }

            if (result)
            {
                break;
            }
        }

        memo[assembly] = result;
        return result;
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

    internal readonly struct CatalogEntry : IEquatable<CatalogEntry>
    {
        internal CatalogEntry(TypeKey key, INamedTypeSymbol symbol, bool isLocal)
        {
            Key = key;
            Symbol = symbol;
            IsLocal = isLocal;
        }

        internal TypeKey Key { get; }

        internal INamedTypeSymbol Symbol { get; }

        internal bool IsLocal { get; }

        public bool Equals(CatalogEntry other) =>
            Key.Equals(other.Key) &&
            SymbolEqualityComparer.Default.Equals(Symbol, other.Symbol) &&
            IsLocal == other.IsLocal;

        public override bool Equals(object? obj) => obj is CatalogEntry other && Equals(other);

        public override int GetHashCode() => Key.GetHashCode();
    }
}
