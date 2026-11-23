using System.Text;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators;

internal abstract class ExtensionGeneratorBase<TMemberExtensionInfo> : ExtensionGeneratorBase
{
    internal override void AppendExtension(StringBuilder builder, IMemberExtensionInfo info) =>
        AppendExtension(builder, (TMemberExtensionInfo)info);

    protected abstract void AppendExtension(StringBuilder builder, TMemberExtensionInfo info);
}

internal abstract class ExtensionGeneratorBase
{
    protected static string CallerInfoParameters => ", [CallerFilePath] string? _callerFile = null, [CallerLineNumber] int _callerLine = 0";

    protected static string CallerInfoArguments => ", _callerFile, _callerLine";

    protected static void AppendDocumentation(StringBuilder builder, string xmlDoc)
    {
        if (!string.IsNullOrEmpty(xmlDoc))
        {
            builder.Append(xmlDoc);
        }
    }

    /// <summary>Writes the extension directly into the group's builder, avoiding an intermediate string.</summary>
    internal abstract void AppendExtension(StringBuilder builder, IMemberExtensionInfo info);

    /// <summary>
    /// Whether this generator emits an overload that AvaloniaPropertyExtensionsGenerator or
    /// AvaloniaEventExtensionsGenerator already emits publicly for declarative views. Such
    /// overloads are suppressed for views so consumers never see two applicable public setters.
    /// </summary>
    internal virtual bool OverlapsWithDeclarativeGenerators => false;
}
