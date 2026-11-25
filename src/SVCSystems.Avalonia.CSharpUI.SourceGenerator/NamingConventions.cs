using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator;

/// <summary>
/// The single home for identifiers used in generated names. The individual methods differ on
/// purpose — control extension classes carry their full namespace, view extension classes keep
/// generic arity, event classes use a dotted nested path — but each rule now has exactly one
/// implementation so collision handling and hint-name changes cannot drift between emitters.
/// </summary>
internal static class NamingConventions
{
    /// <summary>Namespace + nested type path with generic arity removed, used for external control extension classes.</summary>
    internal static string ControlTypeIdentifier(INamedTypeSymbol typeSymbol) =>
        SymbolUtilities.BuildTypeIdentifier(typeSymbol);

    /// <summary>Nested type path keeping generic arity, used for declarative-view extension classes.</summary>
    internal static string ViewExtensionClassName(INamedTypeSymbol? typeSymbol)
    {
        if (typeSymbol is null)
        {
            return "Unknown";
        }

        var parts = new List<string>();
        var current = typeSymbol;
        while (current is not null)
        {
            var name = current.Name;
            if (current.TypeParameters.Length > 0)
            {
                name += $"`{current.TypeParameters.Length}";
            }

            parts.Insert(0, name);
            current = current.ContainingType;
        }

        return SymbolUtilities.CleanIdentifier(string.Join("_", parts)) ?? "Unknown";
    }

    /// <summary>Namespace + nested type path flattened to a legal C# identifier, used for event extension classes.</summary>
    internal static string NestedPathIdentifier(string typeNamespace, string nestedQualifiedTypeName)
    {
        var fullPath = string.IsNullOrWhiteSpace(typeNamespace)
            ? nestedQualifiedTypeName
            : typeNamespace + "." + nestedQualifiedTypeName;
        return fullPath.Replace('.', '_').Replace('+', '_');
    }

    /// <summary>The <c>Host_Property</c> convention for attached-property setters.</summary>
    internal static string AttachedPropertyName(string hostTypeName, string memberName) =>
        $"{hostTypeName.Split('`')[0]}_{memberName}";

    /// <summary>
    /// Prefixes C# keywords and contextual keywords with <c>@</c>. Roslyn reports a keyword-named
    /// parameter as <c>class</c>, which would produce uncompilable generated code when used as a
    /// parameter identifier.
    /// </summary>
    internal static string EscapeIdentifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ||
        SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None
            ? "@" + name
            : name;
}
