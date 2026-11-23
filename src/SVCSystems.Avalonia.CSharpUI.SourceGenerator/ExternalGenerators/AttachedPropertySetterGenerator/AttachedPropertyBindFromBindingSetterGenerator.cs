using System.Text;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.AttachedPropertySetterGenerator;

/// <summary>
/// Generates a named overload for each attached property that accepts a ready-made <c>BindingBase</c>, e.g.
/// <c>new TextBlock().Grid_Row(new Binding("Row"))</c>. See issue #121.
/// </summary>
internal sealed class AttachedPropertyBindFromBindingSetterGenerator : ExtensionGeneratorBase<AttachedPropertyExtensionInfo>
{
    protected override void AppendExtension(StringBuilder builder, AttachedPropertyExtensionInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.AttachedPropertyHostTypeName) ||
            info.ValueTypeSource is "global::Avalonia.Data.BindingBase" or "global::Avalonia.Data.BindingBase?")
        {
            return;
        }

        AppendDocumentation(builder, info.XmlDoc);
        builder.Append($"public static T {info.ExtensionName}<T>(this T control, BindingBase binding{CallerInfoParameters}) where T : {info.AttachedPropertyHostTypeName} {SymbolUtilities.NewLine}");
        builder.Append($"   => control.BindValue({info.ControlTypeName}.{info.FieldSymbol.Name}!, binding{CallerInfoArguments});");
    }
}
