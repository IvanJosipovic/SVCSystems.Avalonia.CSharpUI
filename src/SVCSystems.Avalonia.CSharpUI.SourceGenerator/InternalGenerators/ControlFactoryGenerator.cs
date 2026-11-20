using Microsoft.CodeAnalysis;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.InternalGenerators;

/// <summary>
/// Compatibility entry point for hosts that run this generator directly. Factories are now
/// emitted by <see cref="UsageDrivenMarkupGenerator"/> for the used control set only.
/// </summary>
public sealed class ControlFactoryGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        new UsageDrivenMarkupGenerator().Initialize(context);
}
