using System.Text;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.SetterGenerators;

internal sealed class BindFromExpressionSetterGenerator : ExtensionGeneratorBase<PropertyExtensionInfo>
{
    internal override bool OverlapsWithDeclarativeGenerators => true;

    protected override void AppendExtension(StringBuilder builder, PropertyExtensionInfo info)
    {
        var genericParams = info.IsGeneric ? "<T, TViewModel, TValue>" : "<TViewModel, TValue>";
        builder.Append($"public static {info.ReturnType} {info.ExtensionName}{genericParams}(this {info.ReturnType} control, TViewModel source, Expression<Func<TViewModel, TValue>> getter, BindingMode? mode = null, IValueConverter? converter = null{CallerInfoParameters}) {info.GenericConstraint} {SymbolUtilities.NewLine}");
        builder.Append($"   => control._setCompiledBinding({info.ControlTypeName}.{info.FieldSymbol.Name}!, source, getter, mode, converter{CallerInfoArguments});");
    }
}
