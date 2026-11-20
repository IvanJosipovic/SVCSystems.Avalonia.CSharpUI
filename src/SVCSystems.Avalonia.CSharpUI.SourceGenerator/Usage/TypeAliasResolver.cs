using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Usage;

internal readonly struct AliasResolutionResult(
    ImmutableArray<KeyValuePair<string, string>> aliases,
    ImmutableArray<(string Name, string Reason)> skipped)
{
    internal ImmutableArray<KeyValuePair<string, string>> Aliases { get; } = aliases;

    internal ImmutableArray<(string Name, string Reason)> Skipped { get; } = skipped;
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
    internal static AliasResolutionResult Resolve(Compilation compilation, HashSet<string> factoryNames)
    {
        if (factoryNames.Count == 0)
        {
            return new AliasResolutionResult(
                ImmutableArray<KeyValuePair<string, string>>.Empty,
                ImmutableArray<(string, string)>.Empty);
        }

        var typesByName = new Dictionary<string, HashSet<INamedTypeSymbol>>(StringComparer.Ordinal);
        var skippedByName = new Dictionary<string, string>(StringComparer.Ordinal);
        var userAliases = new HashSet<string>(StringComparer.Ordinal);

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var root = syntaxTree.GetRoot();
            foreach (var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                if (usingDirective.Alias is not null)
                {
                    userAliases.Add(usingDirective.Alias.Name.Identifier.ValueText);
                }
            }

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                if (name is GenericNameSyntax || !factoryNames.Contains(name.Identifier.ValueText))
                {
                    continue;
                }

                if (semanticModel.GetSymbolInfo(name).Symbol is not INamedTypeSymbol type)
                {
                    continue;
                }

                if (!CanUseInGlobalAlias(type, compilation))
                {
                    skippedByName[name.Identifier.ValueText] = "cannot be used in a global alias";
                    continue;
                }

                if (!typesByName.TryGetValue(name.Identifier.ValueText, out var types))
                {
                    types = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                    typesByName.Add(name.Identifier.ValueText, types);
                }

                types.Add(type);
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
                aliases.Add(new KeyValuePair<string, string>(
                    pair.Key,
                    pair.Value.Single().ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
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
