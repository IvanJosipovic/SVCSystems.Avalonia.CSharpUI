using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator;

internal static class MarkupTypeHelpers
{
    internal const string NewLine = "\n";

    private sealed class BoolBox
    {
        internal bool Value;
    }

    private static readonly ConditionalWeakTable<INamedTypeSymbol, BoolBox> MarkupViewCache = new();

    internal static bool IsAvaloniaMarkupView(INamedTypeSymbol typeSymbol) =>
        MarkupViewCache.GetValue(typeSymbol, static type =>
            new BoolBox { Value = type.AllInterfaces.Any(x => x.Name == "IDeclarativeViewBase") }).Value;

    internal static ImmutableArray<ClassDeclarationSyntax> FindAvaloniaMarkupViews(Compilation compilation)
    {
        IEnumerable<SyntaxNode> allNodes = compilation.SyntaxTrees.SelectMany(s => s.GetRoot().DescendantNodes());
        IEnumerable<ClassDeclarationSyntax> allClasses = allNodes
            .Where(d => d.IsKind(SyntaxKind.ClassDeclaration))
            .OfType<ClassDeclarationSyntax>();

        return [..allClasses.Where(type => IsGenerateExtensionsView(compilation, type))];
    }

    private static bool IsGenerateExtensionsView(Compilation compilation, ClassDeclarationSyntax component)
    {
        var sModel = compilation.GetSemanticModel(component.SyntaxTree);
        var classSymbol = ModelExtensions.GetDeclaredSymbol(sModel, component);

        return classSymbol is INamedTypeSymbol cs && cs.AllInterfaces.Any(x => x.Name == "IDeclarativeViewBase");
    }

    /// <summary>
    /// Determines if property also has Avalonia property backend or not
    /// </summary>
    /// <param name="property">target property</param>
    /// <param name="members"></param>
    /// <returns></returns>
    internal static bool IsCommonInstanceProperty(PropertyDeclarationSyntax property, SyntaxList<MemberDeclarationSyntax> members)
    {
        if(property.Modifiers.Any(x=>x.Text == "static"))
            return false;

        var avaloniaPropertyName = property.Identifier + "Property";
        return members.OfType<FieldDeclarationSyntax>().All(field => field.Declaration.Variables[0].Identifier.ToString() != avaloniaPropertyName);
    }

    internal static bool HasAvaloniaPropertyPublicSetter(FieldDeclarationSyntax field, SyntaxList<MemberDeclarationSyntax> members)
    {
        var backingPropertyName = field.Declaration.Variables[0].Identifier.ToString().Replace("Property", "");

        var property = members
            .OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(x => x.Identifier.ValueText == backingPropertyName);

        return HasPublicSetter(property);
    }

    internal static bool HasPublicSetter(PropertyDeclarationSyntax? property)
    {
        if (property != null)
        {
            var setter = property.AccessorList?.Accessors.FirstOrDefault(x => x.IsKind(SyntaxKind.SetAccessorDeclaration));
            if (setter?.Modifiers.Any() == false)
                return true;
        }

        return false;
    }

    internal static bool IsPublic(PropertyDeclarationSyntax? property)
    {
        return property != null && property.Modifiers.Any(x => x.ValueText == "public");
    }

    internal static string GetPropertyTypeName(PropertyDeclarationSyntax property, SemanticModel semanticModel)
    {
        var typeInfo = semanticModel.GetTypeInfo(property.Type);
        // Generated code lives in namespace SVCSystems.Avalonia.CSharpUI, where a relative
        // "Avalonia.Controls.X" would resolve to SVCSystems.Avalonia.CSharpUI.Controls.
        return typeInfo.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            ?? property.Type.ToString();
    }

    internal static string GetNullableLambdaParameterTypeName(PropertyDeclarationSyntax property, SemanticModel semanticModel)
    {
        var typeInfo = semanticModel.GetTypeInfo(property.Type);
        var typeSymbol = typeInfo.Type;

        if (typeSymbol == null)
            return property.Type + "?";

        var nullableTypeSymbol = typeSymbol.WithNullableAnnotation(NullableAnnotation.Annotated);

        return nullableTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }
}