using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.InternalGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator;

/// <summary>
/// Compatibility entry point for tests and direct generator hosts.
/// </summary>
[Generator]
public sealed class Generator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        new UsageDrivenMarkupGenerator().Initialize(context);
        // One class-declaration pass produces both property and event extensions.
        ViewExtensionGenerator.Register(context, emitPropertyExtensions: true, emitEventExtensions: true);
        new XamlResourceGenerator().Initialize(context);

        context.RegisterPostInitializationOutput(static output => output.AddSource(
            "SVCSystemsCSharpUIGlobalUsings.g.cs",
            SourceText.From(
                "global using SVCSystems.Avalonia.CSharpUI;\n" +
                "global using SVCSystems.Avalonia.CSharpUI.CommonExtensions;\n",
                Encoding.UTF8)));
        // The global static factory import and its type aliases are emitted by
        // UsageDrivenMarkupGenerator after the used control set is known.
    }
}
