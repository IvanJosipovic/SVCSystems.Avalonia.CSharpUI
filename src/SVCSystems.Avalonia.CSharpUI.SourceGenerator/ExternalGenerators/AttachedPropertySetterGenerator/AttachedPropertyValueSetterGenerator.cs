using System.Text;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.AttachedPropertySetterGenerator;

internal sealed class AttachedPropertyValueSetterGenerator : ExtensionGeneratorBase<AttachedPropertyExtensionInfo>
{
    protected override void AppendExtension(StringBuilder builder, AttachedPropertyExtensionInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.AttachedPropertyHostTypeName))
        {
            return;
        }

        AppendDocumentation(builder, info.XmlDoc);
        builder.Append($"public static T {info.ExtensionName}<T>(this T control, {info.ValueTypeSource} value{CallerInfoParameters}) where T : {info.AttachedPropertyHostTypeName} {SymbolUtilities.NewLine}");
        builder.Append($"   => control._set(() => {info.ControlTypeName}.Set{info.MemberName}(control, value!), _callerFile, _callerLine);");
    }
}
