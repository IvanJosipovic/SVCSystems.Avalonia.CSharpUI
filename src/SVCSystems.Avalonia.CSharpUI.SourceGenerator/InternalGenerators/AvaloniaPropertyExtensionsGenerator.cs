using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Diagnostics;
using System.Text;
using static SVCSystems.Avalonia.CSharpUI.SourceGenerator.MarkupTypeHelpers;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.InternalGenerators;

public class AvaloniaPropertyExtensionsGenerator : IIncrementalGenerator
{

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
#if DEBUG
        if (!Debugger.IsAttached)
        {
            //Debugger.Launch();
        }
#endif
        Debug.WriteLine("Initialize AvaloniaPropertyExtensionsGenerator code generator");

        // Shares one class-declaration pass with the event generator when both are registered.
        ViewExtensionGenerator.Register(context, emitPropertyExtensions: true, emitEventExtensions: false);
    }

    internal static string? GenerateSource(
        ClassDeclarationSyntax type,
        SemanticModel semanticModel,
        INamedTypeSymbol typeSymbol,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = type.SyntaxTree.GetRoot(cancellationToken);
        var ns = root.DescendantNodes()
            .FirstOrDefault(x => x is BaseNamespaceDeclarationSyntax) as BaseNamespaceDeclarationSyntax;

        var typeNamespace = ns?.Name.ToString() ?? string.Empty;
        var sb = new StringBuilder(4096);
        var emittedUsings = new HashSet<string>(StringComparer.Ordinal);

        sb.AppendLine("#nullable enable");
        sb.AppendLine("// Auto-generated code");
        AppendUsingDirective(sb, emittedUsings, "using System;");
        AppendUsingDirective(sb, emittedUsings, "using Avalonia.Data;");
        AppendUsingDirective(sb, emittedUsings, "using Avalonia.Data.Converters;");
        AppendUsingDirective(sb, emittedUsings, "using System.Linq.Expressions;");
        AppendUsingDirective(sb, emittedUsings, "using System.Runtime.CompilerServices;");

        if (root is CompilationUnitSyntax compilationUnit)
        {
            foreach (var usingDirective in compilationUnit.Usings)
            {
                AppendUsingDirective(sb, emittedUsings, usingDirective.ToString());
            }
        }

        if (!string.IsNullOrWhiteSpace(typeNamespace))
            AppendUsingDirective(sb, emittedUsings, $"using {typeNamespace};");

        // Build the extension class name - handle nested classes properly
        var extensionClassName = NamingConventions.ViewExtensionClassName(typeSymbol);

        // Fully qualified control type name including namespace, containing types and type parameters
        var controlTypeQualified = GetControlTypeQualifiedName(typeSymbol);

        // Build method generic parameters for all containing type parameters (outermost -> innermost)
        string allTypeParamsForMethods = BuildAllTypeParameters(type);

        sb.AppendLine("namespace SVCSystems.Avalonia.CSharpUI;");
        sb.AppendLine($"public static partial class {extensionClassName}Extensions");
        sb.AppendLine("{");

        var members = type.Members;
        var processedFields = new HashSet<string>(StringComparer.Ordinal);

        // PROCESS AVALONIA PROPERTIES
        foreach (var field in members.OfType<FieldDeclarationSyntax>())
        {
            if (field.Declaration.Type is GenericNameSyntax { Identifier.ValueText: "DirectProperty" or "StyledProperty" or "AttachedProperty" }
                && HasAvaloniaPropertyPublicSetter(field, members))
            {
                sb.AppendLine($"// avalonia properties\n");
                AppendIfNotNull(sb, GetPropertySetterExtension(controlTypeQualified, allTypeParamsForMethods, field, semanticModel));
                AppendIfNotNull(sb, GetExpressionBindingSetterExtension(controlTypeQualified, allTypeParamsForMethods, field, semanticModel));
                processedFields.Add(field.Declaration.Variables[0].Identifier.ValueText);
            }
        }

        // PROCESS COMMON PROPERTIES
        foreach (var property in members.OfType<PropertyDeclarationSyntax>())
        {
            var propertyName = property.Identifier.ToString();
            if (!processedFields.Contains(propertyName + "Property")
                && IsPublic(property)
                && HasPublicSetter(property)
                && IsCommonInstanceProperty(property, members))
            {
                sb.AppendLine($"// common properties\n");

                AppendIfNotNull(sb, GetCommonPropertySetterExtension(controlTypeQualified, property, semanticModel));
                // AppendIfNotNull(sb, GetCommonPropertyBindingSetterExtension(controlTypeQualified, property, semanticModel));
                AppendIfNotNull(sb, GetCommonPropertyExpressionBindingSetterExtension(controlTypeQualified, property, semanticModel));

                processedFields.Add(propertyName);
            }
        }

        sb.AppendLine("}");

        return processedFields.Count > 0 ? sb.ToString() : null;
    }

    internal static string GetHintName(INamedTypeSymbol typeSymbol) =>
        SymbolUtilities.RemoveIllegalFileNameCharacters(
            NamingConventions.ViewExtensionClassName(typeSymbol)) + "Extensions.g.cs";

    private static string BuildAllTypeParameters(ClassDeclarationSyntax type)
    {
        var stack = new Stack<string>();
        SyntaxNode? current = type;
        while (current is ClassDeclarationSyntax cds)
        {
            if (cds.TypeParameterList is { Parameters.Count: > 0 })
            {
                // Push in reverse so that when we read Stack.ToArray() we keep original order per containing type
                foreach (var p in cds.TypeParameterList.Parameters.Reverse())
                {
                    stack.Push(p.Identifier.Text);
                }
            }
            current = current.Parent;
            if (current is BaseNamespaceDeclarationSyntax)
                break;
        }

        if (stack.Count == 0)
            return string.Empty;

        var arr = stack.ToArray(); // outermost -> innermost order
        return $"<{string.Join(", ", arr)}>";
    }

    private static string GetControlTypeQualifiedName(INamedTypeSymbol typeSymbol) =>
        typeSymbol.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat
                .WithGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance)
                .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.ExpandNullable));

    private static void AppendIfNotNull(StringBuilder sb, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.AppendLine(value);
    }

    private static void AppendUsingDirective(StringBuilder sb, HashSet<string> emittedUsings, string usingDirective)
    {
        if (emittedUsings.Add(usingDirective))
        {
            sb.AppendLine(usingDirective);
        }
    }

    private static string GetFieldXmlDoc(FieldDeclarationSyntax field, SemanticModel semanticModel)
    {
        var variable = field.Declaration.Variables.FirstOrDefault();
        if (variable is null)
        {
            return string.Empty;
        }

        return semanticModel.GetDeclaredSymbol(variable) is IFieldSymbol fieldSymbol
            ? fieldSymbol.GetGeneratedXmlDoc(semanticModel.Compilation)
            : string.Empty;
    }

    private static string GetPropertyXmlDoc(PropertyDeclarationSyntax property, SemanticModel semanticModel) =>
        semanticModel.GetDeclaredSymbol(property) is IPropertySymbol propertySymbol
            ? SymbolUtilities.FormatXmlDoc(propertySymbol, semanticModel.Compilation)
            : string.Empty;

    public static string GetPropertySetterExtension(string controlTypeName, string genericParamsAll, FieldDeclarationSyntax field, SemanticModel semanticModel)
    {
        var extensionName = field.Declaration.Variables[0].Identifier.ToString().Replace("Property", "");
        var xmlDoc = GetFieldXmlDoc(field, semanticModel);

        var genericName = field.Declaration.Type as GenericNameSyntax;
        if (genericName == null)
            return string.Empty;

        var valueTypeSource = genericName.TypeArgumentList.Arguments.Last();

        var valueName = valueTypeSource is NullableTypeSyntax nullableTypeSyntax
            ? nullableTypeSyntax.ElementType.ToString()
            : valueTypeSource.ToString();

        // Get Class constraints
        var classConstraint = "";
        if (field.Parent is ClassDeclarationSyntax classDecleration)
        {
            foreach (TypeParameterConstraintClauseSyntax constraintClause in classDecleration.ConstraintClauses)
            {
                if (constraintClause.Name.ToString() == valueName)
                {
                    classConstraint = " " + constraintClause.ToString();
                }
            }
        }

        var extensionText =
            $"{xmlDoc}public static {controlTypeName} {extensionName}{genericParamsAll}" +
            $"(this {controlTypeName} control, {valueTypeSource} value, [CallerFilePath] string? _callerFile = null, [CallerLineNumber] int _callerLine = 0){classConstraint}{NewLine}" +
            $"=> control._set(() => control.{extensionName} = value!, _callerFile, _callerLine);";

        return extensionText;
    }

    private static string GetCommonPropertySetterExtension(string controlTypeName, PropertyDeclarationSyntax property, SemanticModel semanticModel)
    {
        var extensionName = property.Identifier.ToString();

        var valueTypeSource = GetPropertyTypeName(property, semanticModel);
        var xmlDoc = GetPropertyXmlDoc(property, semanticModel);

        var extensionText =
            $"{xmlDoc}public static {controlTypeName} {extensionName}" +
            $"(this {controlTypeName} control, {valueTypeSource} value, [CallerFilePath] string? _callerFile = null, [CallerLineNumber] int _callerLine = 0)" +
            $"=>{NewLine} control._set(() => control.{extensionName} = value, _callerFile, _callerLine);";

        return extensionText;
    }

    // private static string GetCommonPropertyBindingSetterExtension(string controlTypeName, PropertyDeclarationSyntax property, SemanticModel semanticModel)
    // {
    //     var extensionName = property.Identifier.ToString();
    //     var valueTypeSource = GetPropertyTypeName(property, semanticModel);

    //     var extensionText =
    //         $"#pragma warning disable CS8601{NewLine}" +
    //         $"[Obsolete] public static {controlTypeName} {extensionName}" +
    //         $"(this {controlTypeName} control, IBinding binding)" +
    //         $"=>{NewLine} control._setCommonBindingEx(({valueTypeSource}? v) => control.{extensionName} = v ?? default({valueTypeSource}), binding);{NewLine}" +
    //         $"#pragma warning restore CS8601";

    //     return extensionText;
    // }
    public static string GetExpressionBindingSetterExtension(string controlTypeName, string genericParamsAll, FieldDeclarationSyntax field, SemanticModel semanticModel)
    {
        var extensionName = field.Declaration.Variables[0].Identifier.ToString().Replace("Property", "");
        var xmlDoc = GetFieldXmlDoc(field, semanticModel);

        var genericName = field.Declaration.Type as GenericNameSyntax;
        if (genericName == null)
            return string.Empty;

        var valueTypeSource = genericName.TypeArgumentList.Arguments.Last();

        var valueName = valueTypeSource is NullableTypeSyntax nullableTypeSyntax
            ? nullableTypeSyntax.ElementType.ToString()
            : valueTypeSource.ToString();

        // Get Class constraints
        var classConstraint = "";
        if (field.Parent is ClassDeclarationSyntax classDecleration)
        {
            foreach (TypeParameterConstraintClauseSyntax constraintClause in classDecleration.ConstraintClauses)
            {
                if (constraintClause.Name.ToString() == valueName)
                {
                    classConstraint = " " + constraintClause.ToString();
                }
            }
        }

        var genericParamsCombined = CombineTypeParameters(genericParamsAll, "TViewModel", "TSourceValue");

        var extensionText =
            $"{xmlDoc}public static {controlTypeName} {extensionName}{genericParamsCombined}(this {controlTypeName} control, TViewModel source, Expression<Func<TViewModel, TSourceValue>> getter, BindingMode? mode = null, IValueConverter? converter = null, [CallerFilePath] string? _callerFile = null, [CallerLineNumber] int _callerLine = 0){classConstraint}{NewLine}" +
            $"   => control._setCompiledBinding({controlTypeName}.{extensionName}Property, source, getter, mode, converter, _callerFile, _callerLine);";

        return extensionText;
    }

    private static string GetCommonPropertyExpressionBindingSetterExtension(string controlTypeName, PropertyDeclarationSyntax property, SemanticModel semanticModel)
    {
        var extensionName = property.Identifier.ToString();
        var valueTypeSource = GetPropertyTypeName(property, semanticModel);
        var xmlDoc = GetPropertyXmlDoc(property, semanticModel);

        // Avalonia's property generator emits the registration field for properties marked with
        // [GeneratedDirectProperty]/[GeneratedStyledProperty]. Those properties look like plain CLR
        // properties in syntax, so they reach this path; bind to their generated field instead of
        // passing a null AvaloniaProperty, which makes AvaloniaObject.Bind throw at runtime.
        var registrationField = IsAvaloniaGeneratedProperty(property, semanticModel)
            ? $"{controlTypeName}.{extensionName}Property"
            : "null!";

        var extensionText =
            $"//Generated by GetCommonPropertyExpressionBindingSetterExtension{NewLine}" +
            $"{xmlDoc}public static {controlTypeName} {extensionName}<TViewModel>(this {controlTypeName} control, TViewModel source, Expression<Func<TViewModel, {valueTypeSource}>> getter, BindingMode? mode = null, IValueConverter? converter = null, [CallerFilePath] string? _callerFile = null, [CallerLineNumber] int _callerLine = 0){NewLine}" +
            $"   => control._setCompiledBinding({registrationField}, source, getter, mode, converter, _callerFile, _callerLine);";


        return extensionText;
    }

    private static bool IsAvaloniaGeneratedProperty(PropertyDeclarationSyntax property, SemanticModel semanticModel)
    {
        if (semanticModel.GetDeclaredSymbol(property) is not IPropertySymbol propertySymbol)
            return false;

        foreach (var attribute in propertySymbol.GetAttributes())
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is null
                || attributeClass.ContainingNamespace.ToDisplayString() != "Avalonia")
            {
                continue;
            }

            if (attributeClass.Name is "GeneratedDirectPropertyAttribute" or "GeneratedStyledPropertyAttribute")
                return true;
        }

        return false;
    }

    private static string CombineTypeParameters(string genericParamsAll, params string[] requiredParameters)
    {
        var parameters = string.IsNullOrEmpty(genericParamsAll)
            ? new List<string>()
            : genericParamsAll.Trim('<', '>').Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

        foreach (var requiredParameter in requiredParameters)
        {
            if (!parameters.Contains(requiredParameter))
            {
                parameters.Add(requiredParameter);
            }
        }

        return parameters.Count == 0
            ? string.Empty
            : $"<{string.Join(", ", parameters)}>";
    }
}
