using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators;

/// <summary>
/// Generates reflection-free factories for compiled XAML resources in referenced assemblies.
/// Registered by <see cref="Generator"/>; instantiated directly by generator tests.
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

    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        context.RegisterSourceOutput(context.CompilationProvider, static (spc, compilation) =>
            Generate(spc, compilation));

    private static void Generate(SourceProductionContext context, Compilation compilation)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in compilation.Assembly.GetAttributes()
                     .Where(static a => a.AttributeClass?.ToDisplayString() == AttributeName))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var location = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation()
                ?? Location.None;
            if (!SupportsAccessors(compilation))
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedFramework, location));
                continue;
            }

            var source = attribute.ConstructorArguments.ElementAtOrDefault(0).Value as string;
            var name = attribute.ConstructorArguments.ElementAtOrDefault(1).Value as string;
            if (string.IsNullOrEmpty(name) || !SyntaxFacts.IsValidIdentifier(name) ||
                SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || name![0] == '@' ||
                name == "Create" || name == "Build")
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidRequest, location,
                    "factoryName must be a non-keyword C# class identifier other than 'Create' or 'Build'"));
                continue;
            }

            if (!names.Add(name!) || compilation.GetTypeByMetadataName($"{GeneratedNamespace}.{name}") is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidRequest, location,
                    $"factory name '{name}' is already in use"));
                continue;
            }

            var uri = ParseSource(source);
            if (uri is null)
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidRequest, location,
                    "source must be an absolute avares URI without a query, fragment, credentials, or port"));
                continue;
            }

            var assembly = compilation.References.Select(compilation.GetAssemblyOrModuleSymbol)
                .OfType<IAssemblySymbol>()
                .FirstOrDefault(a => string.Equals(a.Name, uri.Host, StringComparison.OrdinalIgnoreCase));
            var methodName = "Build:" + Uri.UnescapeDataString(uri.AbsolutePath);
            var resources = assembly?.GetTypeByMetadataName(ResourcesType);
            var method = resources?.GetMembers(methodName).OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.IsStatic && m.DeclaredAccessibility == Accessibility.Public &&
                    m.Parameters.Length == 1 &&
                    m.Parameters[0].Type.ToDisplayString() == "System.IServiceProvider" &&
                    IsResource(m.ReturnType, compilation));

            if (method is null || resources?.DeclaredAccessibility != Accessibility.Public ||
                !compilation.IsSymbolAccessibleWithin(method.ReturnType, compilation.Assembly))
            {
                context.ReportDiagnostic(Diagnostic.Create(MissingFactory, location, name, source));
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
            context.AddSource($"{name}.XamlResource.g.cs", SourceText.From(code, Encoding.UTF8));
        }
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

    private static bool IsResource(ITypeSymbol type, Compilation compilation)
    {
        var dictionary = compilation.GetTypeByMetadataName("Avalonia.Controls.IResourceDictionary");
        var style = compilation.GetTypeByMetadataName("Avalonia.Styling.IStyle");
        return type.AllInterfaces.Any(i =>
            SymbolEqualityComparer.Default.Equals(i, dictionary) || SymbolEqualityComparer.Default.Equals(i, style));
    }
}
