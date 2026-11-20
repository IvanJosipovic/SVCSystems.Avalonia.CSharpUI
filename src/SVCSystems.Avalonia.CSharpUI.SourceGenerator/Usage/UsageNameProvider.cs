using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Usage;

/// <summary>
/// Collects every simple name in user source. One of these names is enough to find a control:
/// factory calls (Button()), object creation (new Button()), typed declarations, casts,
/// typeof/nameof, generic arguments and base lists all contain the control's simple name.
/// The transform deliberately does no semantic work so unchanged syntax trees are cached by
/// Roslyn; resolution happens once per compilation in <see cref="UsageResolver"/>.
/// </summary>
internal static class UsageNameProvider
{
    internal static IncrementalValuesProvider<string> Create(IncrementalGeneratorInitializationContext context) =>
        context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is SimpleNameSyntax,
                transform: static (syntaxContext, _) =>
                    ((SimpleNameSyntax)syntaxContext.Node).Identifier.ValueText)
            .Where(static name => !string.IsNullOrWhiteSpace(name));
}
