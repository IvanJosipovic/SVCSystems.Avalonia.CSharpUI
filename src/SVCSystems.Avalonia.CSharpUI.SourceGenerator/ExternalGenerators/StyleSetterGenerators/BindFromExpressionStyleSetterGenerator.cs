using System.Text;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.StyleSetterGenerators;

internal sealed class BindFromExpressionStyleSetterGenerator : ExtensionGeneratorBase<PropertyExtensionInfo>
{
    protected override void AppendExtension(StringBuilder builder, PropertyExtensionInfo info)
    {
        if (info.ValueTypeSource is "global::Avalonia.Data.IBinding" or "global::Avalonia.Data.IBinding?")
        {
            return;
        }

        var genericParams = info.IsGeneric ? "<T, TViewModel, TValue>" : "<TViewModel, TValue>";
        builder.Append($"public static Style<{info.ReturnType}> {info.ExtensionName}{genericParams}(this Style<{info.ReturnType}> style, TViewModel source, Expression<Func<TViewModel, TValue>> getter, BindingMode? bindingMode = null, IValueConverter? converter = null{CallerInfoParameters}) {info.StyleGenericConstraint} {SymbolUtilities.NewLine}");
        builder.Append($"   => style._addSetterCompiledBinding({info.ControlTypeName}.{info.MemberName}Property!, source, getter, bindingMode, converter{CallerInfoArguments});");
        builder.Append(SymbolUtilities.NewLine);
        builder.Append($"public static Style<{info.ReturnType}> {info.ExtensionName}{genericParams}(this Style<{info.ReturnType}> style, Expression<Func<TViewModel, TValue>> getter, BindingMode? bindingMode = null, IValueConverter? converter = null{CallerInfoParameters}) {info.StyleGenericConstraint} {SymbolUtilities.NewLine}");
        builder.Append($"   => style._addSetterCompiledBinding({info.ControlTypeName}.{info.MemberName}Property!, getter, bindingMode, converter{CallerInfoArguments});");
    }
}
