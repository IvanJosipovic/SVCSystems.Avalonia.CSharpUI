using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.InternalGenerators;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators;

/// <summary>
/// Generates reflection-free factories for compiled XAML resources in referenced assemblies.
/// Requests come from an attribute-driven incremental provider; the compilation is only used
/// inside the generation step, and the step emits strings so no syntax tree or semantic model is
/// retained by the incremental cache.
/// </summary>
public sealed class XamlResourceGenerator : IIncrementalGenerator
{
    private const string AttributeName = "SVCSystems.Avalonia.CSharpUI.GenerateXamlResourceAttribute";
    private const string GeneratedNamespace = "SVCSystems.Avalonia.CSharpUI";
    private const string ResourcesType = "CompiledAvaloniaXaml.!AvaloniaResources";
    private const string AccessorType = "System.Runtime.CompilerServices.UnsafeAccessorTypeAttribute";

    private static readonly DiagnosticDescriptor UnsupportedFramework = new(
        "SVCUIGEN002", "Compiled XAML factories require .NET 10",
        "GenerateXamlResource requires .NET 10 or later with UnsafeAccessorType support",
        "SVCSystems.Avalonia.CSharpUI", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor InvalidRequest = new(
        "SVCUIGEN004", "Invalid compiled XAML factory request",
        "Cannot generate XAML resource factory: {0}",
        "SVCSystems.Avalonia.CSharpUI", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor MissingFactory = new(
        "SVCUIGEN005", "Compiled XAML resource factory is unavailable",
        "Cannot generate '{0}': '{1}' must identify a public compiled ResourceDictionary or IStyle factory in a referenced assembly",
        "SVCSystems.Avalonia.CSharpUI", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // ForAttributeWithMetadataName does not surface assembly-level attributes in this
        // Roslyn version, so select candidate attribute syntax and bind it to the matching
        // AttributeData from the assembly symbol.
        var requests = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) =>
                    node is AttributeSyntax attribute && IsRequestAttributeName(attribute.Name),
                transform: static (ctx, cancellationToken) => CreateRequest(ctx, cancellationToken))
            .Where(static request => request is not null)
            .Select(static (request, _) => request!.Value)
            .WithComparer(XamlRequestComparer.Instance)
            .WithTrackingName("XamlResource.Requests");

        var generation = requests.Collect()
            .Combine(context.CompilationProvider)
            .Select(static (pair, cancellationToken) => Generate(pair.Left, pair.Right, cancellationToken))
            .WithComparer(XamlGenerationResultComparer.Instance)
            .WithTrackingName("XamlResource.Generate");

        context.RegisterSourceOutput(generation,
            static (sourceContext, result) => Emit(sourceContext, result));
    }

    private static bool IsRequestAttributeName(NameSyntax name)
    {
        var identifier = name switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name.Identifier.ValueText,
            IdentifierNameSyntax identifierName => identifierName.Identifier.ValueText,
            _ => string.Empty
        };
        return identifier is "GenerateXamlResource" or "GenerateXamlResourceAttribute";
    }

    private static XamlRequest? CreateRequest(
        GeneratorSyntaxContext context,
        CancellationToken cancellationToken)
    {
        var syntax = (AttributeSyntax)context.Node;
        foreach (var attribute in context.SemanticModel.Compilation.Assembly.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.AttributeClass?.ToDisplayString() != AttributeName ||
                attribute.ApplicationSyntaxReference is not { } reference ||
                reference.SyntaxTree != syntax.SyntaxTree ||
                reference.Span != syntax.Span)
            {
                continue;
            }

            return new XamlRequest(
                attribute.ConstructorArguments.ElementAtOrDefault(0).Value as string,
                attribute.ConstructorArguments.ElementAtOrDefault(1).Value as string,
                syntax.GetLocation());
        }

        return null;
    }

    private static XamlGenerationResult Generate(
        ImmutableArray<XamlRequest> requests,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        if (requests.IsDefaultOrEmpty)
        {
            return XamlGenerationResult.Empty;
        }

        var key = ComputeKey(requests, compilation, cancellationToken);
        return XamlGenerationCache.GetOrAdd(key, () => GenerateCore(requests, compilation, cancellationToken));
    }

    private static XamlGenerationResult GenerateCore(
        ImmutableArray<XamlRequest> requests,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var supportsAccessors = SupportsAccessors(compilation);
        var assembliesByName = new Dictionary<string, IAssemblySymbol>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in compilation.References)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly &&
                !assembliesByName.ContainsKey(assembly.Name))
            {
                assembliesByName.Add(assembly.Name, assembly);
            }
        }

        var resourceDictionary = compilation.GetTypeByMetadataName("Avalonia.Controls.IResourceDictionary");
        var style = compilation.GetTypeByMetadataName("Avalonia.Styling.IStyle");

        var sources = ImmutableArray.CreateBuilder<(string Hint, string Text)>();
        var diagnostics = ImmutableArray.CreateBuilder<XamlDiagnostic>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = request.Location;
            if (!supportsAccessors)
            {
                diagnostics.Add(new XamlDiagnostic("SVCUIGEN002", DiagnosticSeverity.Error, [], location));
                continue;
            }

            var source = request.Source;
            var name = request.Name;
            if (string.IsNullOrEmpty(name) || !SyntaxFacts.IsValidIdentifier(name) ||
                SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || name![0] == '@' ||
                name == "Create" || name == "Build")
            {
                diagnostics.Add(new XamlDiagnostic("SVCUIGEN004", DiagnosticSeverity.Error,
                    ["factoryName must be a non-keyword C# class identifier other than 'Create' or 'Build'"],
                    location));
                continue;
            }

            if (!names.Add(name!) || compilation.GetTypeByMetadataName($"{GeneratedNamespace}.{name}") is not null)
            {
                diagnostics.Add(new XamlDiagnostic("SVCUIGEN004", DiagnosticSeverity.Error,
                    [$"factory name '{name}' is already in use"], location));
                continue;
            }

            var uri = ParseSource(source);
            if (uri is null)
            {
                diagnostics.Add(new XamlDiagnostic("SVCUIGEN004", DiagnosticSeverity.Error,
                    ["source must be an absolute avares URI without a query, fragment, credentials, or port"],
                    location));
                continue;
            }

            assembliesByName.TryGetValue(uri.Host, out var assembly);
            var methodName = "Build:" + Uri.UnescapeDataString(uri.AbsolutePath);
            var resources = assembly?.GetTypeByMetadataName(ResourcesType);
            var method = resources?.GetMembers(methodName).OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.IsStatic && m.DeclaredAccessibility == Accessibility.Public &&
                    m.Parameters.Length == 1 &&
                    m.Parameters[0].Type.ToDisplayString() == "System.IServiceProvider" &&
                    IsResource(m.ReturnType, resourceDictionary, style));

            if (method is null || resources?.DeclaredAccessibility != Accessibility.Public ||
                !compilation.IsSymbolAccessibleWithin(method.ReturnType, compilation.Assembly))
            {
                diagnostics.Add(new XamlDiagnostic("SVCUIGEN005", DiagnosticSeverity.Error,
                    [name, source ?? string.Empty], location));
                continue;
            }

            var returnType = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var target = SymbolDisplay.FormatLiteral($"{ResourcesType}, {assembly!.Identity.Name}", true);
            var member = SymbolDisplay.FormatLiteral(methodName, true);
            var code = $$"""
                // <auto-generated/>
                #nullable enable
                namespace {{GeneratedNamespace}};

                internal static class {{name}}
                {
                    public static {{returnType}} Create(global::System.IServiceProvider? services = null) =>
                        Build(null, global::Avalonia.Markup.Xaml.XamlIl.Runtime.XamlIlRuntimeHelpers.CreateRootServiceProviderV3(services));

                    [global::System.Runtime.CompilerServices.UnsafeAccessor(
                        global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticMethod, Name = {{member}})]
                    private static extern {{returnType}} Build(
                        [global::System.Runtime.CompilerServices.UnsafeAccessorType({{target}})] object? declaringType,
                        global::System.IServiceProvider services);
                }
                """;
            sources.Add(($"{name}.XamlResource.g.cs", code));
        }

        return new XamlGenerationResult(sources.ToImmutable(), diagnostics.ToImmutable());
    }

    private static void Emit(SourceProductionContext context, XamlGenerationResult result)
    {
        foreach (var source in result.Sources)
        {
            context.AddSource(source.Hint, SourceText.From(source.Text, Encoding.UTF8));
        }

        foreach (var diagnostic in result.Diagnostics)
        {
            var descriptor = diagnostic.Id switch
            {
                "SVCUIGEN002" => UnsupportedFramework,
                "SVCUIGEN005" => MissingFactory,
                _ => InvalidRequest
            };
            context.ReportDiagnostic(Diagnostic.Create(
                descriptor,
                diagnostic.Location,
                diagnostic.Arguments.AsSpan().ToArray()));
        }
    }

    private static ulong ComputeKey(
        ImmutableArray<XamlRequest> requests,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = GenerationCache.GetReferenceHash(compilation, cancellationToken);
        foreach (var request in requests)
        {
            hash = (hash ^ (ulong)(request.Source?.GetHashCode() ?? 0)) * prime;
            hash = (hash ^ (ulong)(request.Name?.GetHashCode() ?? 0)) * prime;
            hash = (hash ^ (ulong)request.Location.GetHashCode()) * prime;
        }

        return hash == 0 ? offset : hash;
    }

    private static Uri? ParseSource(string? source)
    {
        const string prefix = "avares://";
        if (source is null || !source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            source.IndexOfAny(['?', '#', '\\']) >= 0)
            return null;

        var slash = source.IndexOf('/', prefix.Length);
        if (slash <= prefix.Length || slash == source.Length - 1)
            return null;

        var authority = source.Substring(prefix.Length, slash - prefix.Length);
        if (authority.IndexOfAny([':', '@']) >= 0)
            return null;

        return Uri.TryCreate(source, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri
            : null;
    }

    private static bool SupportsAccessors(Compilation compilation)
    {
        var framework = compilation.Assembly.GetAttributes().FirstOrDefault(static a =>
            a.AttributeClass?.ToDisplayString() == "System.Runtime.Versioning.TargetFrameworkAttribute");
        if (framework?.ConstructorArguments.FirstOrDefault().Value is string name)
        {
            const string prefix = ".NETCoreApp,Version=v";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                !Version.TryParse(name.Substring(prefix.Length), out var version) || version.Major < 10)
                return false;
        }

        return compilation.GetTypeByMetadataName(AccessorType) is not null &&
            compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly.Identity.Version.Major >= 10;
    }

    private static bool IsResource(
        ITypeSymbol type,
        INamedTypeSymbol? dictionary,
        INamedTypeSymbol? style) =>
        type.AllInterfaces.Any(i =>
            SymbolEqualityComparer.Default.Equals(i, dictionary) ||
            SymbolEqualityComparer.Default.Equals(i, style));

    private readonly struct XamlRequest : IEquatable<XamlRequest>
    {
        internal XamlRequest(string? source, string? name, Location location)
        {
            Source = source;
            Name = name;
            Location = location;
        }

        internal string? Source { get; }

        internal string? Name { get; }

        internal Location Location { get; }

        public bool Equals(XamlRequest other) =>
            Source == other.Source &&
            Name == other.Name &&
            SameLocation(Location, other.Location);

        public override bool Equals(object? obj) => obj is XamlRequest other && Equals(other);

        public override int GetHashCode() =>
            ((Source?.GetHashCode() ?? 0) * 397 ^ (Name?.GetHashCode() ?? 0)) * 397 ^ Location.GetHashCode();
    }

    private sealed class XamlRequestComparer : IEqualityComparer<XamlRequest>
    {
        internal static readonly XamlRequestComparer Instance = new();

        public bool Equals(XamlRequest x, XamlRequest y) => x.Equals(y);

        public int GetHashCode(XamlRequest obj) => obj.GetHashCode();
    }

    private readonly struct XamlDiagnostic
    {
        internal XamlDiagnostic(string id, DiagnosticSeverity severity, ImmutableArray<string> arguments, Location location)
        {
            Id = id;
            Severity = severity;
            Arguments = arguments;
            Location = location;
        }

        internal string Id { get; }

        internal DiagnosticSeverity Severity { get; }

        internal ImmutableArray<string> Arguments { get; }

        internal Location Location { get; }
    }

    private sealed class XamlGenerationResult : IEquatable<XamlGenerationResult>
    {
        internal static readonly XamlGenerationResult Empty = new(
            ImmutableArray<(string, string)>.Empty,
            ImmutableArray<XamlDiagnostic>.Empty);

        private readonly int _hash;

        internal XamlGenerationResult(
            ImmutableArray<(string Hint, string Text)> sources,
            ImmutableArray<XamlDiagnostic> diagnostics)
        {
            Sources = sources;
            Diagnostics = diagnostics;
            unchecked
            {
                var hash = 17;
                foreach (var source in sources)
                {
                    hash = (hash * 31) + source.Hint.GetHashCode() + source.Text.GetHashCode();
                }

                foreach (var diagnostic in diagnostics)
                {
                    hash = (hash * 31) + diagnostic.Id.GetHashCode();
                    hash = (hash * 31) + diagnostic.Location.GetHashCode();
                }

                _hash = hash;
            }
        }

        internal ImmutableArray<(string Hint, string Text)> Sources { get; }

        internal ImmutableArray<XamlDiagnostic> Diagnostics { get; }

        public bool Equals(XamlGenerationResult? other)
        {
            if (other is null || _hash != other._hash ||
                Sources.Length != other.Sources.Length ||
                Diagnostics.Length != other.Diagnostics.Length)
            {
                return false;
            }

            for (var i = 0; i < Sources.Length; i++)
            {
                if (Sources[i] != other.Sources[i])
                {
                    return false;
                }
            }

            for (var i = 0; i < Diagnostics.Length; i++)
            {
                var left = Diagnostics[i];
                var right = other.Diagnostics[i];
                if (left.Id != right.Id ||
                    left.Severity != right.Severity ||
                    !left.Arguments.SequenceEqual(right.Arguments) ||
                    !SameLocation(left.Location, right.Location))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is XamlGenerationResult other && Equals(other);

        public override int GetHashCode() => _hash;
    }

    private sealed class XamlGenerationResultComparer : IEqualityComparer<XamlGenerationResult>
    {
        internal static readonly XamlGenerationResultComparer Instance = new();

        public bool Equals(XamlGenerationResult? x, XamlGenerationResult? y) =>
            ReferenceEquals(x, y) || (x is not null && x.Equals(y));

        public int GetHashCode(XamlGenerationResult obj) => obj.GetHashCode();
    }

    private static class XamlGenerationCache
    {
        private const int MaxEntries = 8;
        private static readonly ConcurrentDictionary<ulong, XamlGenerationResult> Results = new();

        internal static XamlGenerationResult GetOrAdd(ulong key, Func<XamlGenerationResult> create)
        {
            var result = Results.GetOrAdd(key, _ => create());
            if (Results.Count > MaxEntries)
            {
                Results.Clear();
            }

            return result;
        }
    }

    private static bool SameLocation(Location left, Location right) =>
        left.SourceTree?.FilePath == right.SourceTree?.FilePath &&
        left.SourceSpan.Equals(right.SourceSpan) &&
        left.GetLineSpan().Equals(right.GetLineSpan());
}
