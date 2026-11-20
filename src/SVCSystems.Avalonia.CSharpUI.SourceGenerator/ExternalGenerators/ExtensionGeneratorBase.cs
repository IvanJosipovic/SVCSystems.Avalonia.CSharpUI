using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators;

internal abstract class ExtensionGeneratorBase<TMemberExtensionInfo> : ExtensionGeneratorBase
{
    protected abstract string? GetExtension(TMemberExtensionInfo info);

    public override string? GetExtension(IMemberExtensionInfo info) => GetExtension((TMemberExtensionInfo)info);
}

internal abstract class ExtensionGeneratorBase
{
    protected static string CallerInfoParameters => ", [CallerFilePath] string? _callerFile = null, [CallerLineNumber] int _callerLine = 0";

    protected static string CallerInfoArguments => ", _callerFile, _callerLine";

    protected static string PrefixDocumentation(string xmlDoc, string extensionCode) =>
        string.IsNullOrEmpty(xmlDoc) ? extensionCode : xmlDoc + extensionCode;

    public abstract string? GetExtension(IMemberExtensionInfo info);

    /// <summary>
    /// Whether this generator emits an overload that AvaloniaPropertyExtensionsGenerator or
    /// AvaloniaEventExtensionsGenerator already emits publicly for declarative views. Such
    /// overloads are suppressed for views so consumers never see two applicable public setters.
    /// </summary>
    internal virtual bool OverlapsWithDeclarativeGenerators => false;
}
