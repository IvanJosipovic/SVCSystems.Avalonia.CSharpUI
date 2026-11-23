using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator;

internal static class SymbolUtilities
{
    internal const string NewLine = "\n";

    // Symbols are canonical within a compilation, so a ConditionalWeakTable gives us a
    // compilation-scoped cache with no lifetime risk: entries disappear with their symbols.
    private static readonly ConditionalWeakTable<ISymbol, string> MetadataNames = new();
    private static readonly ConditionalWeakTable<ITypeSymbol, string> FullTypeNames = new();
    private static readonly ConcurrentDictionary<string, string> ShortNames = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string?> CleanIdentifiers = new(StringComparer.Ordinal);
    private static readonly HashSet<char> InvalidFileNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    internal static string FormatXmlDoc(ISymbol? symbol, Compilation? compilation = null) =>
        DocumentationLookup.GetDocumentation(symbol, compilation);

    private static readonly SymbolDisplayFormat FullyQualifiedGlobalFormat =
        new(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions:
                SymbolDisplayGenericsOptions.IncludeTypeParameters |
                SymbolDisplayGenericsOptions.IncludeVariance,
            miscellaneousOptions:
                SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
        );

    internal static IEnumerable<ISymbol> GetAllMembers(this INamedTypeSymbol type)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<INamedTypeSymbol>();
        stack.Push(type);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var member in current.GetMembers())
            {
                if (seen.Add(member.ToDisplayString()))
                {
                    yield return member;
                }
            }

            if (current.BaseType != null)
            {
                stack.Push(current.BaseType);
            }

            foreach (var iface in current.Interfaces)
            {
                stack.Push(iface);
            }
        }
    }

    internal static bool IsOrInheritsFrom(this ITypeSymbol? symbol, string metadataName)
    {
        // Most hierarchy levels have a different simple name from the type we are looking for.
        // Checking that first avoids formatting a namespace on almost every step of every walk.
        var shortName = ShortNames.GetOrAdd(metadataName,
            static name => name.Substring(name.LastIndexOf('.') + 1));

        while (symbol != null)
        {
            if (symbol.Name == shortName && GetMetadataName(symbol) == metadataName)
            {
                return true;
            }

            symbol = symbol.BaseType;
        }

        return false;
    }

    internal static bool IsStyledElement(this INamedTypeSymbol controlType) =>
        controlType.IsOrInheritsFrom("Avalonia.StyledElement");

    internal static string GetStyleConstraintTypeName(this ITypeSymbol controlType) =>
        controlType.IsOrInheritsFrom("Avalonia.StyledElement")
            ? controlType.GetFullTypeName()
            : "global::Avalonia.StyledElement";

    internal static bool HasPublicSetter(this IPropertySymbol? property) =>
        property?.SetMethod != null && property.SetMethod.DeclaredAccessibility == Accessibility.Public;

    internal static bool HasAccessibleParameterlessConstructor(this INamedTypeSymbol type)
    {
        foreach (var constructor in type.InstanceConstructors)
        {
            if (constructor.Parameters.Length != 0)
            {
                continue;
            }

            if (constructor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsAccessibleFromGeneratedCode(this INamedTypeSymbol type)
    {
        INamedTypeSymbol? current = type;
        while (current != null)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return false;
            }

            current = current.ContainingType;
        }

        return true;
    }

    internal static bool IsAvaloniaPropertyField(this IFieldSymbol field)
    {
        // The type-name check is free; GetAttributes()/GetMembers() are not. Do it first.
        if (!IsPropertyFieldType(field.Type.Name, attachedOnly: false))
        {
            return false;
        }

        return !field.IsObsolete() && !field.IsReadOnlyField();
    }

    internal static bool IsAttachedPropertyField(this IFieldSymbol field) =>
        IsPropertyFieldType(field.Type.Name, attachedOnly: true) &&
        !field.IsObsolete() &&
        !field.IsReadOnlyAttachedField();

    internal static bool IsAcceptableStyledField(this IFieldSymbol field) =>
        IsPropertyFieldType(field.Type.Name, styledOrAttachedOnly: true) &&
        !field.IsObsolete() &&
        !field.IsReadOnlyField();

    private static bool IsPropertyFieldType(string name, bool attachedOnly = false, bool styledOrAttachedOnly = false)
    {
        if (attachedOnly)
        {
            return name.StartsWith("AttachedProperty", StringComparison.Ordinal);
        }

        if (styledOrAttachedOnly)
        {
            return name.StartsWith("StyledProperty", StringComparison.Ordinal) ||
                   name.StartsWith("AttachedProperty", StringComparison.Ordinal);
        }

        return name.StartsWith("DirectProperty", StringComparison.Ordinal) ||
               name.StartsWith("StyledProperty", StringComparison.Ordinal) ||
               name.StartsWith("AttachedProperty", StringComparison.Ordinal) ||
               name.StartsWith("AvaloniaProperty", StringComparison.Ordinal);
    }

    internal static bool HasUnsupportedExternalValueType(this IFieldSymbol field)
    {
        var valueTypeName = field.Type.GetLastGenericArgument().GetFullTypeName();
        return valueTypeName is "global::Avalonia.Data.IBinding" or "global::Avalonia.Data.IBinding?";
    }

    internal static bool IsReadOnlyField(this IFieldSymbol field)
    {
        var controlType = field.ContainingType;
        var propertyName = field.GetMemberName();
        var symbol = controlType?.GetMembers(propertyName).FirstOrDefault();

        return symbol is not IPropertySymbol property || !property.HasPublicSetter();
    }

    internal static bool IsReadOnlyAttachedField(this IFieldSymbol field)
    {
        var controlType = field.ContainingType;
        var setterMethodName = "Set" + field.GetMemberName();
        var symbol = controlType?.GetMembers(setterMethodName).FirstOrDefault();

        return symbol is not IMethodSymbol method ||
               method.DeclaredAccessibility != Accessibility.Public ||
               !method.IsStatic;
    }

    internal static bool IsObsolete(this ISymbol symbol) =>
        symbol.GetAttributes().Any(static x => x.AttributeClass?.Name == nameof(ObsoleteAttribute));

    internal static IPropertySymbol? GetBackingPropertySymbol(this IFieldSymbol field) =>
        field.ContainingType?.GetMembers(field.GetMemberName()).OfType<IPropertySymbol>().FirstOrDefault();

    internal static string GetGeneratedXmlDoc(this IFieldSymbol field, Compilation? compilation = null) =>
        FormatXmlDoc((ISymbol?)field.GetBackingPropertySymbol() ?? field, compilation);

    internal static string GetMemberName(this IFieldSymbol field)
    {
        if (field.AssociatedSymbol != null)
        {
            return RemoveTrailingProperty(field.AssociatedSymbol.Name);
        }

        return RemoveTrailingProperty(field.Name);
    }

    internal static string RemoveTrailingProperty(string source) =>
        source.EndsWith("Property") ? source.Substring(0, source.Length - "Property".Length) : source;

    /// <summary>
    /// Builds a CREF-safe name for a type. C# documentation IDs use braces for generic arguments;
    /// emitting the raw C# type name (with angle brackets and commas) produces malformed XML.
    /// </summary>
    internal static string GetCrefName(INamedTypeSymbol type)
    {
        return type.GetFullTypeName()
            .Replace('<', '{')
            .Replace('>', '}');
    }

    internal static string GetFullTypeName(this ITypeSymbol symbol) =>
        FullTypeNames.GetValue(symbol, static type =>
        {
            var fullName = type.ToDisplayString(FullyQualifiedGlobalFormat);

            if (!fullName.EndsWith("?") && type.NullableAnnotation == NullableAnnotation.Annotated)
            {
                fullName += "?";
            }

            return fullName;
        });

    internal static ITypeSymbol GetLastGenericArgument(this ITypeSymbol type)
    {
        if (type is INamedTypeSymbol namedType && namedType.TypeArguments.Length > 0)
        {
            return namedType.TypeArguments.Last();
        }

        return type;
    }

    internal static string RemoveIllegalFileNameCharacters(string fileName)
    {
        foreach (var ch in fileName)
        {
            if (InvalidFileNameChars.Contains(ch) || char.IsControl(ch))
            {
                return SanitizeFileName(fileName);
            }
        }

        return fileName;
    }

    private static string SanitizeFileName(string fileName)
    {
        var sb = new StringBuilder(fileName.Length);
        foreach (var ch in fileName)
        {
            sb.Append(InvalidFileNameChars.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        }

        return sb.ToString();
    }

    internal static string? CleanIdentifier(string name, bool @namespace = false)
    {
        name = name.Trim();

        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        // The mapping is compilation-independent, so it can be shared for the whole process.
        // The namespace form is rare and needs different escaping, so it is not cached here.
        return @namespace
            ? CleanIdentifierCore(name, @namespace: true)
            : CleanIdentifiers.GetOrAdd(name, static value => CleanIdentifierCore(value, @namespace: false)!);
    }

    private static string? CleanIdentifierCore(string name, bool @namespace)
    {
        var sb = new StringBuilder(name.Length + 1);
        if (!SyntaxFacts.IsIdentifierStartCharacter(name[0]))
        {
            sb.Append('_');
        }

        foreach (var ch in name)
        {
            if (SyntaxFacts.IsIdentifierPartCharacter(ch) || (@namespace && ch == '.'))
            {
                sb.Append(ch);
            }
        }

        var result = sb.ToString();
        if (SyntaxFacts.GetKeywordKind(result) != SyntaxKind.None)
        {
            result = '@' + result;
        }

        if (!@namespace)
        {
            return result;
        }

        var parts = result.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            if (SyntaxFacts.GetKeywordKind(parts[i]) != SyntaxKind.None)
            {
                parts[i] = '@' + parts[i];
            }
        }

        return string.Join(".", parts);
    }

    internal static string BuildExtensionClassName(INamedTypeSymbol typeSymbol)
    {
        return $"{BuildTypeIdentifier(typeSymbol)}_MarkupExtensions";
    }

    internal static string BuildTypeIdentifier(INamedTypeSymbol typeSymbol)
    {
        var parts = new List<string>();

        if (typeSymbol.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace)
        {
            parts.AddRange(containingNamespace.ToDisplayString().Split('.'));
        }

        var typeParts = new Stack<string>();
        INamedTypeSymbol? current = typeSymbol;
        while (current != null)
        {
            typeParts.Push(current.Name.Split('`')[0]);
            current = current.ContainingType;
        }

        parts.AddRange(typeParts);

        return CleanIdentifier(string.Join("_", parts)) ?? "Unknown";
    }

    private static string GetMetadataName(ISymbol symbol) =>
        MetadataNames.GetValue(symbol, static value =>
        {
            var ns = value.ContainingNamespace?.ToDisplayString();
            return string.IsNullOrWhiteSpace(ns) ? value.Name : ns + "." + value.Name;
        });
}
