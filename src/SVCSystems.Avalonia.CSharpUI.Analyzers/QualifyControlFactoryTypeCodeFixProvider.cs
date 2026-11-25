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
/// Rewrites a name hidden by a generated control factory to its globally qualified form.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(QualifyControlFactoryTypeCodeFixProvider)), Shared]
public sealed class QualifyControlFactoryTypeCodeFixProvider : CodeFixProvider
{
    private const string Title = "Qualify the type with global::";

    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(GeneratedControlFactoryNameAnalyzer.DiagnosticId);

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
            if (diagnostic.Id != GeneratedControlFactoryNameAnalyzer.DiagnosticId)
            {
                continue;
            }

            if (root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
                is not IdentifierNameSyntax identifier)
            {
                continue;
            }

            var semanticModel = await context.Document
                .GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (semanticModel is null)
            {
                continue;
            }

            // Withhold the fix when the competing type cannot be resolved from semantics; never
            // guess a qualification from the diagnostic text alone.
            var competingType = GeneratedControlFactoryNameAnalyzer.ResolveCompetingType(
                semanticModel, identifier);
            if (competingType is null)
            {
                continue;
            }

            var qualifiedName = competingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (SyntaxFactory.ParseName(qualifiedName).ContainsDiagnostics)
            {
                continue;
            }

            // Parse as an expression as well so the rewritten tree has the same shape as source
            // text that spells out global::Type.Member.
            var nameExpression = SyntaxFactory.ParseExpression(qualifiedName);
            if (nameExpression.ContainsDiagnostics)
            {
                continue;
            }

            // The annotation keeps the globally qualified name from being simplified back into a
            // bare identifier that a same-named local type or generated factory could capture.
            var annotatedName = nameExpression.WithAdditionalAnnotations(Simplifier.Annotation);
            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    cancellationToken => ReplaceAsync(context.Document, identifier, annotatedName, cancellationToken),
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
