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
        new ExternalAssemblyExtensionsGenerator().Initialize(context);
        new LocalControlExtensionsGenerator().Initialize(context);
        new AvaloniaPropertyExtensionsGenerator().Initialize(context);
        new AvaloniaEventExtensionsGenerator().Initialize(context);
        new ViewFactoryRegistrationGenerator().Initialize(context);
        new ControlFactoryGenerator().Initialize(context);
        new XamlResourceGenerator().Initialize(context);

        context.RegisterPostInitializationOutput(static output => output.AddSource(
            "SVCSystemsCSharpUIGlobalUsings.g.cs",
            SourceText.From(
                "global using SVCSystems.Avalonia.CSharpUI;\n" +
                "global using SVCSystems.Avalonia.CSharpUI.CommonExtensions;\n",
                Encoding.UTF8)));
    }
}
