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
/// names resolve to their real types. Names that cannot be aliased are reported to the caller.
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

        var typesByName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
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

                var typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (!typesByName.TryGetValue(name.Identifier.ValueText, out var names))
                {
                    names = new HashSet<string>(StringComparer.Ordinal);
                    typesByName.Add(name.Identifier.ValueText, names);
                }

                names.Add(typeName);
            }
        }

        var aliases = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
        var skipped = ImmutableArray.CreateBuilder<(string Name, string Reason)>();
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
            else
            {
                aliases.Add(new KeyValuePair<string, string>(pair.Key, pair.Value.Single()));
            }
        }

        return new AliasResolutionResult(aliases.ToImmutable(), skipped.ToImmutable());
    }
}
