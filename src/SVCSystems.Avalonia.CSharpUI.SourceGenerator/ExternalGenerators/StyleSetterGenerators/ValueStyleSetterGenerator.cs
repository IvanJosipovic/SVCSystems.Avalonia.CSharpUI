using System.Text;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.StyleSetterGenerators;

internal sealed class ValueStyleSetterGenerator : ExtensionGeneratorBase<PropertyExtensionInfo>
{
    protected override void AppendExtension(StringBuilder builder, PropertyExtensionInfo info)
    {
        AppendDocumentation(builder, info.XmlDoc);
        builder.Append($"public static Style<{info.ReturnType}> {info.ExtensionName}{info.GenericArg}(this Style<{info.ReturnType}> style, {info.ValueTypeSource} value{CallerInfoParameters}) {info.StyleGenericConstraint} {SymbolUtilities.NewLine}");
        builder.Append($"=> style._addSetter({info.ControlTypeName}.{info.MemberName}Property!, value!, _callerFile, _callerLine);");
    }
}
