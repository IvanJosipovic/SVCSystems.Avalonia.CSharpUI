using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.InternalGenerators;

/// <summary>
/// One syntax/semantic pass per view class for both the property and event extension generators.
/// Historically each generator registered its own provider, so every class was symbol-bound twice
/// and every edited tree was walked twice.
/// </summary>
internal static class ViewExtensionGenerator
{
    internal enum Kind
    {
        PropertyExtensions,
        EventExtensions
    }

    private readonly struct Target : IEquatable<Target>
    {
        private readonly int _hash;

        internal Target(Kind kind, string hintName, string text)
        {
            Kind = kind;
            HintName = hintName;
            Text = text;
            unchecked
            {
                _hash = (((int)kind * 397) ^ StringComparer.Ordinal.GetHashCode(hintName)) ^
                        StringComparer.Ordinal.GetHashCode(text);
            }
        }

        internal Kind Kind { get; }

        internal string HintName { get; }

        internal string Text { get; }

        public bool Equals(Target other) =>
            Kind == other.Kind &&
            StringComparer.Ordinal.Equals(HintName, other.HintName) &&
            StringComparer.Ordinal.Equals(Text, other.Text);

        public override bool Equals(object? obj) => obj is Target other && Equals(other);

        public override int GetHashCode() => _hash;
    }

    private sealed class TargetComparer : IEqualityComparer<Target>
    {
        internal static readonly TargetComparer Instance = new();

        public bool Equals(Target x, Target y) => x.Equals(y);

        public int GetHashCode(Target obj) => obj.GetHashCode();
    }

    internal static void Register(
        IncrementalGeneratorInitializationContext context,
        bool emitPropertyExtensions,
        bool emitEventExtensions)
    {
        var targets = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: (ctx, cancellationToken) =>
                    Analyze(ctx, emitPropertyExtensions, emitEventExtensions, cancellationToken))
            .Where(static targets => !targets.IsDefaultOrEmpty)
            .SelectMany(static (targets, _) => targets)
            .WithComparer(TargetComparer.Instance)
            .WithTrackingName("ViewExtensions.Classes");

        context.RegisterSourceOutput(targets, (sourceContext, target) =>
        {
            if ((target.Kind == Kind.PropertyExtensions && !emitPropertyExtensions) ||
                (target.Kind == Kind.EventExtensions && !emitEventExtensions))
            {
                return;
            }

            sourceContext.AddSource(target.HintName, SourceText.From(target.Text, Encoding.UTF8));
        });
    }

    private static ImmutableArray<Target> Analyze(
        GeneratorSyntaxContext context,
        bool emitPropertyExtensions,
        bool emitEventExtensions,
        CancellationToken cancellationToken)
    {
        var classDeclaration = (ClassDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken);
        if (symbol is not INamedTypeSymbol typeSymbol ||
            !MarkupTypeHelpers.IsAvaloniaMarkupView(typeSymbol))
        {
            return ImmutableArray<Target>.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var propertyText = emitPropertyExtensions
            ? AvaloniaPropertyExtensionsGenerator.GenerateSource(
                classDeclaration, context.SemanticModel, typeSymbol, cancellationToken)
            : null;
        var eventSource = emitEventExtensions
            ? AvaloniaEventExtensionsGenerator.GenerateSource(classDeclaration, cancellationToken)
            : null;

        if (propertyText is null && eventSource is null)
        {
            return ImmutableArray<Target>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<Target>(2);
        if (propertyText is not null)
        {
            builder.Add(new Target(
                Kind.PropertyExtensions,
                AvaloniaPropertyExtensionsGenerator.GetHintName(typeSymbol),
                propertyText));
        }

        if (eventSource is not null)
        {
            builder.Add(new Target(Kind.EventExtensions, eventSource.Value.HintName, eventSource.Value.Text));
        }

        return builder.ToImmutable();
    }
}
