using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SVCSystems.Avalonia.CSharpUI.Analyzers;

/// <summary>
/// Flags event subscriptions inside a declarative view's <c>Build</c> method. CSharpUI can rebuild a
/// view on reload (and a view can be built more than once), so a handler attached with <c>+=</c>
/// there accumulates instead of being replaced. This is a message-only diagnostic: there is no
/// universally correct place to move the subscription to.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EventSubscriptionInBuildMethodAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "SVCUI003";
    public const string Category = "SVCSystems.Avalonia.CSharpUI";

    private const string BuildMethodName = "Build";
    private const string ViewBaseTypeName = "ViewBase";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Event subscribed inside a declarative Build method",
        "Event '{0}' is subscribed inside Build(); the handler may be attached again on every rebuild",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Move the subscription to a stable lifecycle hook, or use a generated event "
                   + "setter that replaces the handler instead of adding another one.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeAddAssignment, SyntaxKind.AddAssignmentExpression);
    }

    private static void AnalyzeAddAssignment(SyntaxNodeAnalysisContext context)
    {
        // Cheap syntactic gate first: only `Event += ...` or `x.Event += ...` shaped assignments
        // are candidates. Both an identifier and a member access can bind to an event symbol.
        if (context.Node is not AssignmentExpressionSyntax assignment ||
            (assignment.Left is not MemberAccessExpressionSyntax &&
             assignment.Left is not IdentifierNameSyntax))
        {
            return;
        }

        var method = FindEnclosingMethod(assignment);
        if (method is null ||
            !string.Equals(method.Identifier.ValueText, BuildMethodName, StringComparison.Ordinal))
        {
            return;
        }

        var semanticModel = context.SemanticModel;
        if (semanticModel.GetSymbolInfo(assignment.Left, context.CancellationToken).Symbol is not IEventSymbol @event)
        {
            return;
        }

        if (method.Parent is not TypeDeclarationSyntax typeDeclaration ||
            semanticModel.GetDeclaredSymbol(typeDeclaration, context.CancellationToken)
                is not INamedTypeSymbol containingType ||
            !DerivesFromViewBase(containingType))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            assignment.GetLocation(),
            @event.Name));
    }

    private static MethodDeclarationSyntax? FindEnclosingMethod(SyntaxNode node)
    {
        for (SyntaxNode? current = node; current is not null; current = current.Parent)
        {
            if (current is MethodDeclarationSyntax method)
            {
                return method;
            }
        }

        return null;
    }

    private static bool DerivesFromViewBase(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.Name, ViewBaseTypeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
