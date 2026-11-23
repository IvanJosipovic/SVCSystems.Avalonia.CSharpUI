using System.Text;
using Microsoft.CodeAnalysis;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.SetterGenerators;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.SetterGenerators;

internal sealed class ValueOverloadsSetterGenerator : ExtensionGeneratorBase<PropertyExtensionInfo>
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
            builder.Append($"public static {info.ReturnType} {info.ExtensionName}{info.GenericArg}(this {info.ReturnType} control, {argDefs}{CallerInfoParameters}) {info.GenericConstraint} {SymbolUtilities.NewLine}");
            builder.Append($"   => control._set(() => control.{info.MemberName} = new {info.ValueTypeSource}({argVals}), _callerFile, _callerLine);");
        }
    }
}
