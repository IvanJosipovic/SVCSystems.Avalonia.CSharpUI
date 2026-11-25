using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;

namespace SVCSystems.Avalonia.CSharpUI.Analyzers;

/// <summary>
/// Replaces a mistyped factory name with a globally qualified call to the unique matching
/// generated factory. The qualification is annotated so the simplifier can shorten it, but never
/// shorten it into a name that a local type or import could capture.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(QualifyGeneratedFactoryNameCodeFixProvider)), Shared]
public sealed class QualifyGeneratedFactoryNameCodeFixProvider : CodeFixProvider
{
    private const string Title = "Use the generated control factory";

    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(MistypedGeneratedFactoryNameAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            if (diagnostic.Id != MistypedGeneratedFactoryNameAnalyzer.DiagnosticId)
            {
                continue;
            }

            // Everything the fix needs comes from the analyzer's property bag, never from parsing
            // the human-readable message.
            if (!diagnostic.Properties.TryGetValue(
                    MistypedGeneratedFactoryNameAnalyzer.SuggestionProperty,
                    out var factoryName) ||
                string.IsNullOrWhiteSpace(factoryName) ||
                !diagnostic.Properties.TryGetValue(
                    MistypedGeneratedFactoryNameAnalyzer.FactoryTypeProperty,
                    out var factoryType) ||
                string.IsNullOrWhiteSpace(factoryType))
            {
                continue;
            }

            if (root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
                is not IdentifierNameSyntax identifier)
            {
                continue;
            }

            var qualified = factoryType + "." + factoryName;
            var expression = SyntaxFactory.ParseExpression(qualified);
            if (expression.ContainsDiagnostics)
            {
                continue;
            }

            var annotated = expression.WithAdditionalAnnotations(Simplifier.Annotation);
            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    cancellationToken => ReplaceAsync(context.Document, identifier, annotated, cancellationToken),
                    equivalenceKey: Title),
                diagnostic);
        }
    }

    private static async Task<Document> ReplaceAsync(
        Document document,
        IdentifierNameSyntax identifier,
        ExpressionSyntax replacement,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        return root is null ? document : document.WithSyntaxRoot(root.ReplaceNode(identifier, replacement));
    }
}
