using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SVCSystems.Avalonia.CSharpUI.Analyzers;

/// <summary>
/// Suggests a generated control factory when an unbound, PascalCase identifier in call position is
/// a very close match for exactly one factory. Wrong suggestions are worse than silence, so the
/// gates are deliberately strict and the fix is withheld on any ambiguity.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MistypedGeneratedFactoryNameAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "SVCUI002";
    public const string Category = "SVCSystems.Avalonia.CSharpUI";

    internal const string SuggestionProperty = "SuggestedFactoryName";
    internal const string FactoryTypeProperty = "FactoryType";

    // Stricter than a CLI-style suggester: this rule fires in the compiler, under
    // TreatWarningsAsErrors, without a corpus to rank alternatives.
    private const double SimilarityThreshold = 0.88;
    private const int MinimumNameLength = 4;
    private const int MaximumLengthDelta = 2;

    private const string FactoryTypeName = "GeneratedControlFactories";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Mistyped generated control factory name",
        "'{0}' is not a known name; did you mean the generated control factory '{1}'?",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The identifier could not be bound and closely matches exactly one generated "
                   + "control factory. No suggestion is offered when two factories are equally close.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static startContext =>
        {
            var factories = CollectFactories(startContext.Compilation);
            if (factories.Length == 0)
            {
                return;
            }

            startContext.RegisterSyntaxNodeAction(
                context => AnalyzeInvocation(context, factories),
                SyntaxKind.InvocationExpression);
        });
    }

    private static ImmutableArray<(string Name, string FactoryType)> CollectFactories(Compilation compilation)
    {
        var result = ImmutableArray.CreateBuilder<(string Name, string FactoryType)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in AllTypes(compilation.GlobalNamespace))
        {
            if (!string.Equals(type.Name, FactoryTypeName, StringComparison.Ordinal) ||
                type.ContainingNamespace?.ToDisplayString() is not { } ns ||
                ns.IndexOf("SVCSystems.Avalonia.CSharpUI", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            var factoryType = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            foreach (var member in type.GetMembers())
            {
                // Generated factories carry optional caller-info parameters so a bare call still
                // compiles; only require that no argument is mandatory.
                if (member is not IMethodSymbol { IsStatic: true, IsImplicitlyDeclared: false } method ||
                    !method.Parameters.All(static parameter => parameter.IsOptional) ||
                    !method.Name.All(char.IsLetterOrDigit) ||
                    !seen.Add(method.Name))
                {
                    continue;
                }

                result.Add((method.Name, factoryType));
            }
        }

        return result.ToImmutable();
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in NestedTypes(type))
            {
                yield return nested;
            }
        }

        foreach (var child in ns.GetNamespaceMembers())
        {
            foreach (var type in AllTypes(child))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> NestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in NestedTypes(nested))
            {
                yield return deeper;
            }
        }
    }

    private static void AnalyzeInvocation(
        SyntaxNodeAnalysisContext context,
        ImmutableArray<(string Name, string FactoryType)> factories)
    {
        // Bare factories are IdentifierNameSyntax; member accesses are intentionally excluded so
        // this rule cannot interfere with a real static call or a member-access qualifier.
        if (context.Node is not InvocationExpressionSyntax invocation ||
            invocation.Expression is not IdentifierNameSyntax identifier ||
            identifier.IsMissing)
        {
            return;
        }

        var name = identifier.Identifier.ValueText;
        if (name.Length < MinimumNameLength || !char.IsUpper(name[0]))
        {
            return;
        }

        var symbolInfo = context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken);
        if (symbolInfo.Symbol is not null || !symbolInfo.CandidateSymbols.IsEmpty)
        {
            return;
        }

        (string Name, string FactoryType)? best = null;
        var bestScore = 0.0;
        var ties = 0;

        foreach (var factory in factories)
        {
            if (Math.Abs(factory.Name.Length - name.Length) > MaximumLengthDelta)
            {
                continue;
            }

            var score = StringSimilarity.JaroWinkler(name, factory.Name);
            if (score < SimilarityThreshold)
            {
                continue;
            }

            if (score > bestScore + 1e-9)
            {
                best = factory;
                bestScore = score;
                ties = 0;
            }
            else if (Math.Abs(score - bestScore) <= 1e-9)
            {
                ties++;
            }
        }

        if (best is null || ties > 0)
        {
            return;
        }

        var match = best.Value;
        var properties = ImmutableDictionary<string, string?>.Empty
            .Add(SuggestionProperty, match.Name)
            .Add(FactoryTypeProperty, match.FactoryType);

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            identifier.GetLocation(),
            properties,
            name,
            match.Name));
    }
}
