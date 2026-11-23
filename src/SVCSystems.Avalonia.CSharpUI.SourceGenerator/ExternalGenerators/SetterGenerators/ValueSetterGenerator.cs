using System.Text;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.SetterGenerators;

internal sealed class ValueSetterGenerator : ExtensionGeneratorBase<PropertyExtensionInfo>
{
    internal override bool OverlapsWithDeclarativeGenerators => true;

    protected override void AppendExtension(StringBuilder builder, PropertyExtensionInfo info)
    {
        AppendDocumentation(builder, info.XmlDoc);
        builder.Append($"public static {info.ReturnType} {info.ExtensionName}{info.GenericArg}(this {info.ReturnType} control, {info.ValueTypeSource} value{CallerInfoParameters}) {info.GenericConstraint} {SymbolUtilities.NewLine}");
        builder.Append($"=> control._set(() => control.{info.MemberName} = value!, _callerFile, _callerLine);");
    }
}
