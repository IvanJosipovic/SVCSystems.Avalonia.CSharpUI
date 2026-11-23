using System.Collections.Immutable;
using System.Text;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.Analysis;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.AttachedPropertySetterGenerator;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.EventGenerators;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.SetterGenerators;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.StyleSetterGenerators;
using Microsoft.CodeAnalysis;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators;

internal sealed class ExternalGeneratorHost
{
    // All generators are stateless; one shared array avoids reallocating the whole emission
    // pipeline for each compilation's generation pass.
    private static readonly ExtensionGroupGenerator[] GroupGenerators =
    [
        new("Properties",
            static (model, compilation) => GetPropertyInfos(model, compilation),

            new ValueSetterGenerator(),
            new BindFromExpressionSetterGenerator(),
            new BindFromBindingSetterGenerator(),
            new ValueOverloadsSetterGenerator()
        ),

        new("Attached Properties",
            static (model, compilation) => GetAttachedPropertyInfos(model, compilation),

            new AttachedPropertyValueSetterGenerator(),
            new AttachedPropertyBindFromExpressionSetterGenerator(),
            new AttachedPropertyBindFromBindingSetterGenerator()
        ),

        new("Events",
            static (model, compilation) => GetEventInfos(model, compilation),

            new ActionToEventGenerator()
        ),

        new("Styles",
            static (model, compilation) => GetStyleInfos(model, compilation),

            new ValueStyleSetterGenerator(),
            new BindFromExpressionStyleSetterGenerator(),
            new BindFromBindingStyleSetterGenerator(),
            new ValueOverloadsStyleSetterGenerator()
        )
    ];

    internal static IEnumerable<PropertyExtensionInfo> GetPropertyInfos(ControlModel model, Compilation? compilation = null)
    {
        var propertiesByField = model.DeclaredProperties
            .Where(static property => !property.IsIgnored &&
                property.AvaloniaPropertyField is not null &&
                property.CanBind)
            .ToDictionary(
                static property => property.AvaloniaPropertyField!,
                SymbolEqualityComparer.Default);

        return model.Type.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(propertiesByField.ContainsKey)
            .Select(field => new PropertyExtensionInfo(field, propertiesByField[field].ExtensionName, compilation));
    }

    internal static IEnumerable<AttachedPropertyExtensionInfo> GetAttachedPropertyInfos(ControlModel model, Compilation? compilation = null) =>
        model.DeclaredAttachedProperties
            .Where(static property => !property.IsIgnored)
            .Select(property => new AttachedPropertyExtensionInfo(property.Field, property.ExtensionName, compilation))
            .Where(static x => !string.IsNullOrWhiteSpace(x.AttachedPropertyHostTypeName));

    internal static IEnumerable<EventExtensionInfo> GetEventInfos(ControlModel model, Compilation? compilation = null) =>
        model.DeclaredEvents
            .Where(static eventModel => !eventModel.IsIgnored && eventModel.InheritanceKind != PropertyInheritanceKind.Override)
            .Select(eventModel => new EventExtensionInfo(eventModel.Symbol, eventModel.ExtensionName, compilation));

    internal static IEnumerable<PropertyExtensionInfo> GetStyleInfos(ControlModel model, Compilation? compilation = null)
    {
        if (!model.Type.IsStyledElement())
        {
            return [];
        }

        var result = new List<PropertyExtensionInfo>();
        foreach (var field in model.StylePropertyFields)
        {
            if (field.HasUnsupportedExternalValueType())
            {
                continue;
            }

            var metadata = ControlModelBuilder.GetMetadata(field);
            if (metadata.Ignore)
            {
                continue;
            }

            result.Add(new PropertyExtensionInfo(field, metadata.Alias, compilation));
        }

        return result;
    }

    public string? GenerateExtensions(
        INamedTypeSymbol controlType,
        Compilation? compilation = null,
        bool publish = false,
        bool suppressOverlappingSetters = false)
        => GenerateExtensions(ControlModelBuilder.Build(controlType), compilation, publish, suppressOverlappingSetters);

    internal string? GenerateExtensions(
        ControlModel controlModel,
        Compilation? compilation = null,
        bool publish = false,
        bool suppressOverlappingSetters = false)
    {
        if (controlModel.IgnoreGeneratedExtensions)
        {
            return null;
        }

        List<(string GroupName, string Extensions)>? generatedGroups = null;

        foreach (var groupGenerator in GroupGenerators)
        {
            var extensions = groupGenerator.Generate(controlModel, compilation, suppressOverlappingSetters, out var generatedCount);
            if (generatedCount == 0 || string.IsNullOrWhiteSpace(extensions))
            {
                continue;
            }

            generatedGroups ??= [];
            generatedGroups.Add((groupGenerator.GroupName, extensions!));
        }

        if (generatedGroups is null)
        {
            return null;
        }

        var sb = new StringBuilder(4096);
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#pragma warning disable CS1591");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using Avalonia;");
        sb.AppendLine("using Avalonia.Data;");
        sb.AppendLine("using Avalonia.Data.Converters;");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Linq.Expressions;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine();
        sb.AppendLine("namespace SVCSystems.Avalonia.CSharpUI;");
        var typeName = controlModel.Type.GetFullTypeName();
        var crefName = SymbolUtilities.GetCrefName(controlModel.Type);
        sb.AppendLine("/// <summary>");
        sb.AppendLine($"/// Fluent setters, binding helpers, event helpers and style setters for <see cref=\"{crefName}\"/>.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("[global::System.CodeDom.Compiler.GeneratedCode(\"SVCSystems.Avalonia.CSharpUI.SourceGenerator\", \"1.0.0.0\")]");
        sb.AppendLine("[global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]");
        var visibility = publish && IsPubliclyAccessible(controlModel.Type) ? "public" : "internal";
        sb.AppendLine($"{visibility} static partial class {SymbolUtilities.BuildExtensionClassName(controlModel.Type)}");
        sb.AppendLine("{");

        foreach (var group in generatedGroups)
        {
            sb.AppendLine($"//================= {group.GroupName} ======================//");
            sb.Append(group.Extensions);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// A published extension class must not expose less accessible types in its public signatures.
    /// A control is publishable only when its whole type is visible outside the assembly: the type
    /// itself, its containing types and every type argument (including array/pointer elements).
    /// </summary>
    internal static bool CanPublish(INamedTypeSymbol type) => IsPubliclyAccessible(type);

    private static bool IsPubliclyAccessible(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => IsPubliclyAccessible(array.ElementType),
        IPointerTypeSymbol pointer => IsPubliclyAccessible(pointer.PointedAtType),
        // An open generic's parameter is part of the public definition, not an inaccessible type.
        ITypeParameterSymbol => true,
        INamedTypeSymbol named =>
            named.DeclaredAccessibility == Accessibility.Public &&
            (named.ContainingType is null || IsPubliclyAccessible(named.ContainingType)) &&
            named.TypeArguments.All(IsPubliclyAccessible),
        _ => false
    };
}
