using Microsoft.CodeAnalysis;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.InternalGenerators;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators;

/// <summary>
/// Compatibility entry point for hosts that run this generator directly. The implementation now
/// lives in <see cref="UsageDrivenMarkupGenerator"/> and is driven by control usage.
/// </summary>
public sealed class ExternalAssemblyExtensionsGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        new UsageDrivenMarkupGenerator().Initialize(context);
}
