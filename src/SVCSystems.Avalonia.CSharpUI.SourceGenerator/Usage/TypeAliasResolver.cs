using System.Collections.Concurrent;
using System.Collections.Immutable;
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

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = syntaxTree.GetRoot(cancellationToken);
            SemanticModel? semanticModel = null;
            var treeKey = GenerationCache.GetTreeKey(syntaxTree, referenceHash);
            TypeAliasBindingCache.TryGet(treeKey, out var cachedBindings);
            Dictionary<(string, string), TypeAliasBinding>? newBindings = null;
            var contextByScope = new Dictionary<SyntaxNode, string>();
            var fileContext = new System.Text.StringBuilder(128);

            foreach (var node in root.DescendantNodes())
            {
                if (node is UsingDirectiveSyntax usingDirective)
                {
                    fileContext.Append(usingDirective.ToString()).Append(';');
                    if (usingDirective.Alias is { } alias)
                    {
                        userAliases.Add(alias.Name.Identifier.ValueText);
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
                        // Scope kind + offset distinguishes a using-alias name from usages in a
                        // method, while identical files still share the same context string.
                        .Append(scope.Kind()).Append('@').Append(scope.SpanStart).Append(';');
                    foreach (var ancestor in scope.AncestorsAndSelf())
                    {
                        // Only a type whose name matches a factory name can shadow it. Ignoring
                        // ordinary containing view classes lets identical files share bindings.
                        if (ancestor is BaseTypeDeclarationSyntax declaration &&
                            factoryNames.Contains(declaration.Identifier.ValueText))
                        {
                            contextBuilder.Append('T').Append(declaration.Identifier.ValueText).Append(';');
                        }
                    }

                    context = contextBuilder.ToString();
                    contextByScope.Add(scope, context);
                }

                // A.Shared and B.Shared are different bindings even in the same method, so the
                // qualifier preceding the simple name is part of the cache key.
                var qualifier = name.Parent switch
                {
                    QualifiedNameSyntax qualified when qualified.Right == name => qualified.Left.ToString(),
                    MemberAccessExpressionSyntax memberAccess when memberAccess.Name == name => memberAccess.Expression.ToString(),
                    _ => string.Empty
                };
                var key = (text, context + "|" + qualifier);
                TypeAliasBinding binding;
                if (cachedBindings is not null && cachedBindings.TryGetValue(key, out binding))
                {
                    // Reuse the binding from a previous compilation for this unchanged tree.
                }
                else if (newBindings is not null && newBindings.TryGetValue(key, out binding))
                {
                    // The same (name, context) repeated inside this tree.
                }
                else
                {
                    semanticModel ??= compilation.GetSemanticModel(syntaxTree);
                    var type = semanticModel.GetSymbolInfo(name, cancellationToken).Symbol as INamedTypeSymbol;
                    binding = type is not null
                        ? new TypeAliasBinding(
                            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                            CanUseInGlobalAlias(type, compilation))
                        : default;

                    if (binding.DisplayName is not null)
                    {
                        // Failed binds are not cached: the same text may resolve differently in
                        // another scope (a local can shadow the type).
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
                    continue;
                }

                if (!typesByName.TryGetValue(text, out var types))
                {
                    types = new HashSet<string>(StringComparer.Ordinal);
                    typesByName.Add(text, types);
                }

                types.Add(binding.DisplayName);
            }

            if (newBindings is not null)
            {
                // Merge with any cached bindings from other compilations with the same tree key.
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

        return new AliasResolutionResult(aliases.ToImmutable(), skipped.ToImmutable().Distinct().ToImmutableArray());
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
