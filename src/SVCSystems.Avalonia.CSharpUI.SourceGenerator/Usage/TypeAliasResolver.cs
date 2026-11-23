using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.InternalGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Usage;

internal readonly struct AliasResolutionResult(
    ImmutableArray<KeyValuePair<string, string>> aliases,
    ImmutableArray<(string Name, string Reason)> skipped)
{
    internal ImmutableArray<KeyValuePair<string, string>> Aliases { get; } = aliases;

    internal ImmutableArray<(string Name, string Reason)> Skipped { get; } = skipped;
}

internal readonly struct NameOccurrence
{
    internal NameOccurrence(string text, string context, string qualifier, SimpleNameSyntax node)
    {
        Text = text;
        Context = context;
        Qualifier = qualifier;
        Node = node;
    }

    internal SimpleNameSyntax Node { get; }

    internal string Text { get; }

    internal string Context { get; }

    internal string Qualifier { get; }

    internal string Key => Text + "\0" + Context + "\0" + Qualifier;
}

internal sealed class TreeOccurrences
{
    internal ImmutableArray<NameOccurrence> Names = ImmutableArray<NameOccurrence>.Empty;
    internal ImmutableArray<string> UserAliases = ImmutableArray<string>.Empty;
}

/// <summary>
/// The syntactic part of alias resolution depends only on a tree's token hash and the factory
/// name set. Roslyn reuses unchanged trees, so this cache removes the full syntax walk on every
/// generation; only trees the user actually edited are re-walked.
/// </summary>
internal static class TypeAliasOccurrenceCache
{
    // Syntax nodes belong to one tree instance. Roslyn keeps unchanged trees alive across
    // compilations, but a comment-only edit creates a new tree with the same token hash; keying
    // by the tree instance (weakly) is therefore required even though the token hash matches.
    private sealed class Entry
    {
        internal int FactoryHash;
        internal TreeOccurrences Occurrences = null!;
    }

    private static readonly ConditionalWeakTable<SyntaxTree, Entry> Entries = new();

    internal static bool TryGet(
        SyntaxTree tree,
        int factoryHash,
        out TreeOccurrences occurrences)
    {
        if (Entries.TryGetValue(tree, out var entry) && entry.FactoryHash == factoryHash)
        {
            occurrences = entry.Occurrences;
            return true;
        }

        occurrences = null!;
        return false;
    }

    internal static void Add(SyntaxTree tree, int factoryHash, TreeOccurrences occurrences) =>
        Entries.GetValue(tree, _ => new Entry { FactoryHash = factoryHash, Occurrences = occurrences });
}

internal sealed class TreeResolution
{
    internal ImmutableArray<(string Name, string DisplayName)> Bindings = ImmutableArray<(string, string)>.Empty;
    internal ImmutableArray<string> SkippedNames = ImmutableArray<string>.Empty;
    internal ImmutableArray<string> UserAliases = ImmutableArray<string>.Empty;
}

/// <summary>
/// The final syntactic+semantic alias facts for one tree. Keyed weakly by tree instance plus the
/// reference set and factory names, so unchanged trees contribute their aliases in O(1).
/// </summary>
internal static class TypeAliasResolutionCache
{
    private sealed class Entry
    {
        internal int FactoryHash;
        internal ulong SemanticHash;
        internal TreeResolution Resolution = null!;
    }

    private static readonly ConditionalWeakTable<SyntaxTree, Entry> Entries = new();

    internal static bool TryGet(
        SyntaxTree tree,
        int factoryHash,
        ulong semanticHash,
        out TreeResolution resolution)
    {
        if (Entries.TryGetValue(tree, out var entry) &&
            entry.FactoryHash == factoryHash &&
            entry.SemanticHash == semanticHash)
        {
            resolution = entry.Resolution;
            return true;
        }

        resolution = null!;
        return false;
    }

    internal static void Add(
        SyntaxTree tree,
        int factoryHash,
        ulong semanticHash,
        TreeResolution resolution) =>
        Entries.GetValue(tree, _ => new Entry
        {
            FactoryHash = factoryHash,
            SemanticHash = semanticHash,
            Resolution = resolution
        });
}

internal readonly struct TypeAliasBinding : IEquatable<TypeAliasBinding>
{
    internal TypeAliasBinding(string? displayName, bool canUseInGlobalAlias)
    {
        DisplayName = displayName;
        CanUseInGlobalAlias = canUseInGlobalAlias;
    }

    internal string? DisplayName { get; }

    internal bool CanUseInGlobalAlias { get; }

    public bool Equals(TypeAliasBinding other) =>
        DisplayName == other.DisplayName && CanUseInGlobalAlias == other.CanUseInGlobalAlias;

    public override bool Equals(object? obj) => obj is TypeAliasBinding other && Equals(other);

    public override int GetHashCode() => (DisplayName?.GetHashCode() ?? 0) ^ (CanUseInGlobalAlias ? 1 : 0);
}

/// <summary>
/// Semantic binding results for the factory names used in one tree. Roslyn reuses unchanged
/// syntax trees across compilations, so keying on the tree's token hash plus the reference set
/// lets us skip all semantic binding for files the user did not edit.
/// </summary>
internal static class TypeAliasBindingCache
{
    private const int MaxEntries = 512;
    private static readonly ConcurrentDictionary<ulong, ImmutableDictionary<(string, string), TypeAliasBinding>> Bindings = new();

    internal static bool TryGet(ulong key, out ImmutableDictionary<(string, string), TypeAliasBinding> bindings) =>
        Bindings.TryGetValue(key, out bindings!);

    internal static void Add(ulong key, Dictionary<(string, string), TypeAliasBinding> bindings)
    {
        Bindings[key] = bindings.ToImmutableDictionary();
        if (Bindings.Count > MaxEntries)
        {
            Bindings.Clear();
        }
    }
}

/// <summary>
/// A global static import of GeneratedControlFactories makes factory names shadow type names in
/// every context. To keep both working, emit a global alias for each factory name that the
/// application also uses as a type (e.g. Path -&gt; System.IO.Path, Grid -&gt; Avalonia.Controls.Grid).
/// This runs on the pre-generation compilation, where the static import is not yet present, so
/// names resolve to their real types. Names that cannot be safely aliased are reported as skipped.
/// </summary>
internal static class TypeAliasResolver
{
    private static readonly ConcurrentDictionary<(ulong, ulong, ulong), AliasResolutionResult> ResultCache = new();

    internal static AliasResolutionResult Resolve(
        Compilation compilation,
        HashSet<string> factoryNames,
        CancellationToken cancellationToken = default)
    {
        if (factoryNames.Count == 0)
        {
            return new AliasResolutionResult(
                ImmutableArray<KeyValuePair<string, string>>.Empty,
                ImmutableArray<(string, string)>.Empty);
        }

        var typesByName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var skippedByName = new Dictionary<string, string>(StringComparer.Ordinal);
        var userAliases = new HashSet<string>(StringComparer.Ordinal);

        // Bindings are keyed by references + tree tokens only. A newly used factory name simply
        // misses the per-name cache and gets bound; existing bindings stay reusable.
        var referenceHash = GenerationCache.GetReferenceHash(compilation);

        // A stable hash of the factory set; occurrences are cached per (tree, factory set).
        ulong factoryHash = 14695981039346656037UL;
        foreach (var factoryName in factoryNames)
        {
            ulong nameHash = 14695981039346656037UL;
            foreach (var ch in factoryName)
            {
                nameHash = (nameHash ^ ch) * 1099511628211UL;
            }

            factoryHash ^= nameHash;
        }

        var factoryHashCode = unchecked((int)(factoryHash ^ (factoryHash >> 32)));

        // A compilation changes on every keystroke, but the set of trees usually does not.
        // Hashing the cached per-tree token hashes gives a cheap identity for the whole tree set.
        const ulong prime = 1099511628211UL;
        ulong treeSetHash = 14695981039346656037UL;
        foreach (var tree in compilation.SyntaxTrees)
        {
            treeSetHash = (treeSetHash ^ GenerationCache.GetTreeKey(tree, referenceHash)) * prime;
        }

        var resultKey = (referenceHash, factoryHash, treeSetHash);
        if (ResultCache.TryGetValue(resultKey, out var cachedResult))
        {
            return cachedResult;
        }

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Include every source tree in the semantic key: a global using added in another file
            // can change how this tree's names bind even when this tree itself is unchanged.
            var treeKey = GenerationCache.GetTreeKey(syntaxTree, treeSetHash);
            TypeAliasBindingCache.TryGet(treeKey, out var cachedBindings);
            Dictionary<(string, string), TypeAliasBinding>? newBindings = null;

            if (TypeAliasResolutionCache.TryGet(syntaxTree, factoryHashCode, treeSetHash, out var cachedResolution))
            {
                foreach (var alias in cachedResolution.UserAliases)
                {
                    userAliases.Add(alias);
                }

                foreach (var skippedName in cachedResolution.SkippedNames)
                {
                    skippedByName[skippedName] = "cannot be used in a global alias";
                }

                foreach (var binding in cachedResolution.Bindings)
                {
                    if (!typesByName.TryGetValue(binding.Name, out var existing))
                    {
                        existing = new HashSet<string>(StringComparer.Ordinal);
                        typesByName.Add(binding.Name, existing);
                    }

                    existing.Add(binding.DisplayName);
                }

                continue;
            }

            if (!TypeAliasOccurrenceCache.TryGet(syntaxTree, factoryHashCode, out var occurrences))
            {
                occurrences = CollectOccurrences(syntaxTree, factoryNames, cancellationToken);
                TypeAliasOccurrenceCache.Add(syntaxTree, factoryHashCode, occurrences);
            }

            foreach (var alias in occurrences.UserAliases)
            {
                userAliases.Add(alias);
            }

            var resolvedNames = ImmutableArray.CreateBuilder<(string, string)>();
            var skippedNames = ImmutableArray.CreateBuilder<string>();
            SemanticModel? semanticModel = null;
            foreach (var occurrence in occurrences.Names)
            {
                var text = occurrence.Text;
                var key = (text, occurrence.Context + "|" + occurrence.Qualifier);
                TypeAliasBinding binding;
                if (cachedBindings is not null && cachedBindings.TryGetValue(key, out binding))
                {
                    // Reuse the binding resolved for this unchanged tree in an earlier compilation.
                }
                else if (newBindings is not null && newBindings.TryGetValue(key, out binding))
                {
                    // The same (name, context) repeated inside this tree.
                }
                else
                {
                    semanticModel ??= compilation.GetSemanticModel(syntaxTree);
                    var type = semanticModel.GetSymbolInfo(occurrence.Node, cancellationToken).Symbol as INamedTypeSymbol;
                    binding = type is not null
                        ? new TypeAliasBinding(
                            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                            CanUseInGlobalAlias(type, compilation))
                        : default;

                    if (binding.DisplayName is not null)
                    {
                        newBindings ??= [];
                        newBindings[key] = binding;
                    }
                }

                if (binding.DisplayName is null)
                {
                    continue;
                }

                if (!binding.CanUseInGlobalAlias)
                {
                    skippedByName[text] = "cannot be used in a global alias";
                    skippedNames.Add(text);
                    continue;
                }

                if (!typesByName.TryGetValue(text, out var types))
                {
                    types = new HashSet<string>(StringComparer.Ordinal);
                    typesByName.Add(text, types);
                }

                // Record this tree's binding unconditionally. The whole-compilation set is only for
                // detecting ambiguity; another tree may later remove the same binding, and this
                // tree's cached resolution must still contain it.
                types.Add(binding.DisplayName);
                resolvedNames.Add((text, binding.DisplayName));
            }

            if (newBindings is not null)
            {
                if (cachedBindings is not null)
                {
                    foreach (var pair in cachedBindings)
                    {
                        if (!newBindings.ContainsKey(pair.Key))
                        {
                            newBindings.Add(pair.Key, pair.Value);
                        }
                    }
                }

                TypeAliasBindingCache.Add(treeKey, newBindings);
            }

            TypeAliasResolutionCache.Add(syntaxTree, factoryHashCode, treeSetHash, new TreeResolution
            {
                Bindings = resolvedNames.ToImmutable(),
                SkippedNames = skippedNames.ToImmutable(),
                UserAliases = occurrences.UserAliases
            });
        }

        static TreeOccurrences CollectOccurrences(
            SyntaxTree syntaxTree,
            HashSet<string> factoryNames,
            CancellationToken cancellationToken)
        {
            var root = syntaxTree.GetRoot(cancellationToken);
            var result = new TreeOccurrences();
            var names = ImmutableArray.CreateBuilder<NameOccurrence>();
            var aliases = ImmutableArray.CreateBuilder<string>();
            var contextByScope = new Dictionary<SyntaxNode, string>();
            var fileContext = new System.Text.StringBuilder(128);

            foreach (var node in root.DescendantNodes())
            {
                if (node is UsingDirectiveSyntax usingDirective)
                {
                    fileContext.Append(usingDirective.ToString()).Append(';');
                    if (usingDirective.Alias is { } alias)
                    {
                        aliases.Add(alias.Name.Identifier.ValueText);
                    }

                    continue;
                }

                if (node is BaseNamespaceDeclarationSyntax namespaceDeclaration)
                {
                    fileContext.Append(namespaceDeclaration.Name.ToString()).Append(';');
                }

                if (node is not SimpleNameSyntax name ||
                    name is GenericNameSyntax ||
                    !factoryNames.Contains(name.Identifier.ValueText))
                {
                    continue;
                }

                var text = name.Identifier.ValueText;
                var scope = name.FirstAncestorOrSelf<SyntaxNode>(static candidate =>
                    candidate is MemberDeclarationSyntax or UsingDirectiveSyntax) ?? name;

                if (!contextByScope.TryGetValue(scope, out var context))
                {
                    var contextBuilder = new System.Text.StringBuilder(fileContext.Length + 80)
                        .Append(fileContext)
                        .Append(scope.Kind()).Append('@').Append(scope.SpanStart).Append(';');
                    foreach (var ancestor in scope.AncestorsAndSelf())
                    {
                        if (ancestor is BaseTypeDeclarationSyntax declaration &&
                            factoryNames.Contains(declaration.Identifier.ValueText))
                        {
                            contextBuilder.Append('T').Append(declaration.Identifier.ValueText).Append(';');
                        }
                    }

                    context = contextBuilder.ToString();
                    contextByScope.Add(scope, context);
                }

                var qualifier = name.Parent switch
                {
                    QualifiedNameSyntax qualified when qualified.Right == name => qualified.Left.ToString(),
                    MemberAccessExpressionSyntax memberAccess when memberAccess.Name == name => memberAccess.Expression.ToString(),
                    // A::Button and B::Button bind to different namespaces; their aliases must stay
                    // distinct keys so an ambiguity is reported instead of reusing one binding.
                    AliasQualifiedNameSyntax aliasQualified when aliasQualified.Name == name =>
                        aliasQualified.Alias.Identifier.ValueText + "::",
                    _ => string.Empty
                };

                names.Add(new NameOccurrence(text, context, qualifier, name));
            }

            result.Names = names.ToImmutable();
            result.UserAliases = aliases.ToImmutable();
            return result;
        }
        var globalNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in compilation.GlobalNamespace.GetTypeMembers())
        {
            globalNames.Add(type.Name);
        }

        foreach (var ns in compilation.GlobalNamespace.GetNamespaceMembers())
        {
            globalNames.Add(ns.Name);
        }

        var aliases = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
        var skipped = ImmutableArray.CreateBuilder<(string Name, string Reason)>();
        foreach (var pair in skippedByName.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            skipped.Add((pair.Key, pair.Value));
        }

        foreach (var pair in typesByName.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (pair.Value.Count > 1)
            {
                skipped.Add((pair.Key, "resolves to more than one type"));
            }
            else if (userAliases.Contains(pair.Key))
            {
                // A generated global alias would collide (CS1537) with a user-declared using alias.
                skipped.Add((pair.Key, "is already used as a using alias"));
            }
            else if (globalNames.Contains(pair.Key))
            {
                skipped.Add((pair.Key, "conflicts with a global namespace member"));
            }
            else
            {
                aliases.Add(new KeyValuePair<string, string>(pair.Key, pair.Value.Single()));
            }
        }

        var result = new AliasResolutionResult(
            aliases.ToImmutable(), skipped.ToImmutable().Distinct().ToImmutableArray());
        if (ResultCache.Count > 8)
        {
            ResultCache.Clear();
        }

        return ResultCache.GetOrAdd(resultKey, result);
    }

    private static bool CanUseInGlobalAlias(INamedTypeSymbol type, Compilation compilation)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return false;
            }

            var sameAssembly = SymbolEqualityComparer.Default.Equals(current.ContainingAssembly, compilation.Assembly);
            if (!sameAssembly && current.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }

            // A type nested in a generic container has out-of-scope type parameters in a
            // compilation-unit alias, and an open generic cannot be named as an alias target.
            if (current.IsGenericType)
            {
                return false;
            }
        }

        return type.TypeArguments.All(static argument => argument.TypeKind != TypeKind.TypeParameter);
    }
}
