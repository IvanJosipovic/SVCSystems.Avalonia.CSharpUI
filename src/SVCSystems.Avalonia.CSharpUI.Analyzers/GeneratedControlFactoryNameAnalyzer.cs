using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SVCSystems.Avalonia.CSharpUI.Analyzers;

// This assembly intentionally hosts both the analyzer and its code fix. RS1038 assumes an
// analyzer-only assembly; the analyzer below never touches any Workspaces type.
#pragma warning disable RS1038
/// <summary>
/// Finds member accesses whose left-hand identifier binds to a generated control factory method
/// instead of the type the author meant to use, for example <c>Path.Combine(...)</c> binding to
/// <c>GeneratedControlFactories.Path()</c> instead of <c>System.IO.Path</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GeneratedControlFactoryNameAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "SVCUI001";
    public const string Category = "SVCSystems.Avalonia.CSharpUI";

    private const string FactoryTypeName = "GeneratedControlFactories";
    private const string FactoryNamespaceName = "SVCSystems.Avalonia.CSharpUI";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Generated control factory hides a type name",
        "'{0}' binds to a generated control factory method instead of the type '{1}'; qualify the type with 'global::'",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A generated factory method shadows a type with the same name in a member access. "
                   + "Use a globally qualified type name to make the intent unambiguous.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        // Cheap syntactic gates first. Semantic resolution is deliberately the last step.
        if (context.Node is not MemberAccessExpressionSyntax memberAccess ||
            memberAccess.Expression is not IdentifierNameSyntax identifier ||
            identifier.IsMissing ||
            identifier.Identifier.ValueText.Length == 0)
        {
            return;
        }

        var symbolInfo = context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken);
        var factory = symbolInfo.Symbol as IMethodSymbol;
        if (factory is null)
        {
            // A method group used as the left side of a member access produces CS0119 and leaves
            // Symbol empty; the method is still available as a candidate.
            factory = symbolInfo.CandidateSymbols.OfType<IMethodSymbol>()
                .FirstOrDefault(static method => IsGeneratedControlFactory(method));
        }

        if (factory is null || !IsGeneratedControlFactory(factory))
        {
            return;
        }

        var competingType = ResolveCompetingType(context.SemanticModel, identifier);
        if (competingType is null)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            identifier.GetLocation(),
            identifier.Identifier.ValueText,
            competingType.ToDisplayString()));
    }

    private static bool IsGeneratedControlFactory(IMethodSymbol method)
    {
        var containingType = method.ContainingType;
        if (containingType is null ||
            !string.Equals(containingType.Name, FactoryTypeName, StringComparison.Ordinal))
        {
            return false;
        }

        var containingNamespace = containingType.ContainingNamespace?.ToDisplayString();
        return containingNamespace is not null &&
               containingNamespace.IndexOf(FactoryNamespaceName, StringComparison.Ordinal) >= 0;
    }

    /// <summary>
    /// Finds the named type in scope at the identifier that the factory method is hiding.
    /// A type whose full name is the factory name itself is not a useful qualification target.
    /// </summary>
    internal static INamedTypeSymbol? ResolveCompetingType(
        SemanticModel semanticModel,
        IdentifierNameSyntax identifier)
    {
        var factoryName = identifier.Identifier.ValueText;
        foreach (var candidate in semanticModel.LookupSymbols(
                     identifier.SpanStart,
                     name: factoryName,
                     includeReducedExtensionMethods: false))
        {
            if (candidate is INamedTypeSymbol namedType &&
                !string.Equals(namedType.ToDisplayString(), factoryName, StringComparison.Ordinal))
            {
                return namedType;
            }
        }

        return null;
    }
}
#pragma warning restore RS1038
