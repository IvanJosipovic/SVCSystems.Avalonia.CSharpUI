using System.Text;
using Microsoft.CodeAnalysis;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.SetterGenerators;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.StyleSetterGenerators;

internal sealed class ValueOverloadsStyleSetterGenerator : ExtensionGeneratorBase<PropertyExtensionInfo>
{
    protected override void AppendExtension(StringBuilder builder, PropertyExtensionInfo info)
    {
        var valueNamespace = info.ValueType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        if (valueNamespace.StartsWith("System", StringComparison.Ordinal) ||
            !info.ValueType.IsValueType)
        {
            return;
        }

        foreach (var (argDefs, argVals) in ConstructorOverloads.Get(info.ValueType))
        {
            builder.Append(SymbolUtilities.NewLine);
            AppendDocumentation(builder, info.XmlDoc);
            builder.Append($"public static Style<{info.ReturnType}> {info.ExtensionName}{info.GenericArg}(this Style<{info.ReturnType}> style, {argDefs}{CallerInfoParameters}) {info.StyleGenericConstraint} {SymbolUtilities.NewLine}");
            builder.Append($"   => style._addSetter({info.ControlTypeName}.{info.MemberName}Property, new {info.ValueTypeSource}({argVals}), _callerFile, _callerLine);");
        }
    }
}
