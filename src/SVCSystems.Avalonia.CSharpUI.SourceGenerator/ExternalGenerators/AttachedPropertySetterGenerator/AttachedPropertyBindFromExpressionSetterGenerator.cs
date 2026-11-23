using System.Text;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.AttachedPropertySetterGenerator;

internal sealed class AttachedPropertyBindFromExpressionSetterGenerator : ExtensionGeneratorBase<AttachedPropertyExtensionInfo>
{
    protected override void AppendExtension(StringBuilder builder, AttachedPropertyExtensionInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.AttachedPropertyHostTypeName))
        {
            return;
        }

        // overload with source parameter (for regular binding scenarios)
        AppendDocumentation(builder, info.XmlDoc);
        builder.Append($"public static T {info.ExtensionName}<T, TViewModel, TValue>(this T control, TViewModel source, Expression<Func<TViewModel, TValue>> getter, BindingMode? mode = null, IValueConverter? converter = null{CallerInfoParameters}) where T : {info.AttachedPropertyHostTypeName} {SymbolUtilities.NewLine}");
        builder.Append($"   => control._setCompiledBinding({info.ControlTypeName}.{info.FieldSymbol.Name}!, source, getter, mode, converter{CallerInfoArguments});");
        builder.Append(SymbolUtilities.NewLine);

        // overload without source parameter (for DataContext-bound scenarios)
        AppendDocumentation(builder, info.XmlDoc);
        builder.Append($"public static T {info.ExtensionName}<T, TViewModel, TValue>(this T control, Expression<Func<TViewModel, TValue>> getter, BindingMode? mode = null, IValueConverter? converter = null{CallerInfoParameters}) where T : {info.AttachedPropertyHostTypeName} {SymbolUtilities.NewLine}");
        builder.Append($"   => control._setCompiledBinding({info.ControlTypeName}.{info.FieldSymbol.Name}!, getter, mode, converter{CallerInfoArguments});");
    }
}
