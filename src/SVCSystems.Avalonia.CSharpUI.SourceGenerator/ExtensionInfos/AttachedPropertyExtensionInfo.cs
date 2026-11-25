#nullable enable

using Microsoft.CodeAnalysis;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;

internal sealed class AttachedPropertyExtensionInfo : PropertyExtensionInfo
{
    public string? AttachedPropertyHostTypeName { get; }

    public AttachedPropertyExtensionInfo(IFieldSymbol field, string? extensionName = null, Compilation? compilation = null)
        : base(field, extensionName, compilation)
    {
        ExtensionName = NamingConventions.AttachedPropertyName(field.ContainingType.Name, ExtensionName);

        if (field.ContainingType.GetMembers($"Set{MemberName}").FirstOrDefault() is IMethodSymbol setter &&
            setter.Parameters.Length > 0)
        {
            AttachedPropertyHostTypeName = setter.Parameters[0].Type.GetFullTypeName();
            ReturnType = "T";
            GenericConstraint = $"where T : {AttachedPropertyHostTypeName}";
            GenericArg = "<T>";
        }
    }
}
