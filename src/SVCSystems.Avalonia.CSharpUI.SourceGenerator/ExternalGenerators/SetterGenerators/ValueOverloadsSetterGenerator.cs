using System.Text;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;
using Microsoft.CodeAnalysis;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.SetterGenerators;

internal sealed class ValueOverloadsSetterGenerator : ExtensionGeneratorBase<PropertyExtensionInfo>
{
    protected override string GetExtension(PropertyExtensionInfo info)
    {
        StringBuilder? extensionText = null;

        var valueNamespace = info.ValueType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        if (!valueNamespace.StartsWith("System", StringComparison.Ordinal) &&
            info.ValueType.IsValueType)
        {
            foreach (var constructor in info.ValueType.GetMembers().OfType<IMethodSymbol>().Where(static m => m.MethodKind == MethodKind.Constructor))
            {
                var ps = constructor.Parameters;
                if (ps.Length == 0)
                {
                    continue;
                }

                var argDefs = string.Join(", ", ps.Select(static x => $"{x.Type.GetFullTypeName()} {x.Name}"));
                var argVals = string.Join(", ", ps.Select(static x => x.Name));

                extensionText ??= new StringBuilder(256);
                extensionText.Append(SymbolUtilities.NewLine);
                extensionText.Append(PrefixDocumentation(info.XmlDoc,
                    $"public static {info.ReturnType} {info.ExtensionName}{info.GenericArg}(this {info.ReturnType} control, {argDefs}{CallerInfoParameters}) {info.GenericConstraint} {SymbolUtilities.NewLine}"));
                extensionText.Append($"   => control._set(() => control.{info.MemberName} = new {info.ValueTypeSource}({argVals}), _callerFile, _callerLine);");
            }
        }

        return extensionText?.ToString() ?? string.Empty;
    }
}
