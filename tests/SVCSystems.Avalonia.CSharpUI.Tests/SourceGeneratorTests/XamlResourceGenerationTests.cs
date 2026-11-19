using System.Collections.Immutable;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators;
using SVCSystems.Avalonia.CSharpUI.Tests.XamlFixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SVCSystems.Avalonia.CSharpUI.Tests.SourceGeneratorTests;

public class XamlResourceGenerationTests
{
    private const string Root = "avares://SVCSystems.Avalonia.CSharpUI.Tests.XamlFixtures/";
    private static readonly ImmutableArray<MetadataReference> References = CreateReferences();

    [Theory]
    [InlineData("Resources.axaml", "global::Avalonia.Controls.ResourceDictionary")]
    [InlineData("Styles.axaml", "global::Avalonia.Styling.Styles")]
    [InlineData("Space%20Folder/Extra.axaml", "global::Avalonia.Controls.ResourceDictionary")]
    public void Generates_compilable_typed_factories_without_runtime_loading(string path, string returnType)
    {
        var result = Run(Request(Root + path));
        Assert.Empty(result.Diagnostics);
        var source = Assert.Single(result.Sources);
        Assert.Contains($"public static {returnType} Create(", source, StringComparison.Ordinal);
        Assert.Contains("UnsafeAccessorType(", source, StringComparison.Ordinal);
        Assert.Contains("CreateRootServiceProviderV3(services)", source, StringComparison.Ordinal);
        Assert.Contains("Name = \"Build:/" + Uri.UnescapeDataString(path) + "\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AvaloniaXamlLoader", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResourceInclude", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", source, StringComparison.Ordinal);
        Assert.Empty(result.Output.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error));
        using var stream = new MemoryStream();
        var emit = result.Output.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
    }

    [Theory]
    [InlineData(".NETCoreApp,Version=v8.0")]
    [InlineData(".NETCoreApp,Version=v9.0")]
    [InlineData(".NETStandard,Version=v2.1")]
    [InlineData(".NETFramework,Version=v4.8")]
    public void Unsupported_frameworks_report_an_error_without_generating_code(string framework)
    {
        var result = Run(Request(Root + "Resources.axaml"), framework);
        AssertDiagnostic(result, "SVCUIGEN002");
    }

    [Theory]
    [InlineData(".NETCoreApp,Version=v10.0")]
    [InlineData(".NETCoreApp,Version=v11.0")]
    public void Net10_and_later_are_supported(string framework)
    {
        var result = Run(Request(Root + "Resources.axaml"), framework);
        Assert.Empty(result.Diagnostics);
        Assert.Single(result.Sources);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Resources.axaml")]
    [InlineData("avares:/SVCSystems.Avalonia.CSharpUI.Tests.XamlFixtures/Resources.axaml")]
    [InlineData("avares://SVCSystems.Avalonia.CSharpUI.Tests.XamlFixtures\\Resources.axaml")]
    [InlineData("https://example.com/Resources.axaml")]
    [InlineData("avares://SVCSystems.Avalonia.CSharpUI.Tests.XamlFixtures/")]
    [InlineData(Root + "Resources.axaml?query=true")]
    [InlineData(Root + "Resources.axaml#fragment")]
    [InlineData("avares://user@SVCSystems.Avalonia.CSharpUI.Tests.XamlFixtures/Resources.axaml")]
    [InlineData("avares://SVCSystems.Avalonia.CSharpUI.Tests.XamlFixtures:123/Resources.axaml")]
    public void Invalid_uris_report_an_error(string source) =>
        AssertDiagnostic(Run(Request(source)), "SVCUIGEN004");

    [Theory]
    [InlineData("")]
    [InlineData("class")]
    [InlineData("@class")]
    [InlineData("Has.Dot")]
    [InlineData("has-dash")]
    [InlineData("Create")]
    [InlineData("Build")]
    [InlineData("123Name")]
    public void Invalid_names_report_an_error(string name) =>
        AssertDiagnostic(Run(Request(Root + "Resources.axaml", name)), "SVCUIGEN004");

    [Theory]
    [InlineData(Root + "Missing.axaml")]
    [InlineData(Root + "resources.axaml")]
    [InlineData(Root + "Control.axaml")]
    [InlineData("avares://MissingAssembly/Resources.axaml")]
    [InlineData("avares://XamlResourceConsumer/Resources.axaml")]
    public void Missing_or_incompatible_factories_report_an_error(string source) =>
        AssertDiagnostic(Run(Request(source)), "SVCUIGEN005");

    [Theory]
    [InlineData("Resources.axaml")]
    [InlineData("Styles.axaml")]
    public void Duplicate_names_report_a_diagnostic_instead_of_crashing_the_generator(string secondPath)
    {
        var first = Request(Root + "Resources.axaml");
        var result = Run(first + Request(Root + secondPath));
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("SVCUIGEN004", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("GeneratedResources", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.True(diagnostic.Location.SourceSpan.Start >= first.Length);
        Assert.Single(result.Sources);
        Assert.Empty(result.Output.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Duplicate_names_in_separate_files_report_at_the_second_request()
    {
        var result = Run(Request(Root + "Resources.axaml"),
            additionalSource: Request(Root + "Styles.axaml"));
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("SVCUIGEN004", diagnostic.Id);
        Assert.Equal("Additional.cs", diagnostic.Location.SourceTree?.FilePath);
        Assert.Single(result.Sources);
    }

    [Fact]
    public void Repeated_duplicates_report_each_conflict_and_do_not_block_other_factories()
    {
        var result = Run(Request(Root + "Resources.axaml") +
            Request(Root + "Styles.axaml") +
            Request(Root + "Resources.axaml") +
            Request(Root + "Styles.axaml", "OtherResources"));
        Assert.Equal(2, result.Diagnostics.Length);
        Assert.All(result.Diagnostics, d => Assert.Equal("SVCUIGEN004", d.Id));
        Assert.Equal(2, result.Sources.Length);
    }

    [Fact]
    public void Existing_types_are_not_overwritten()
    {
        var result = Run(Request(Root + "Resources.axaml") +
            "namespace SVCSystems.Avalonia.CSharpUI { internal class GeneratedResources {} }");
        AssertDiagnostic(result, "SVCUIGEN004");
    }

    [Fact]
    public void No_opt_in_generates_no_code_or_diagnostics_on_older_targets()
    {
        var result = Run("", ".NETCoreApp,Version=v8.0");
        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Sources);
    }

    [Fact]
    public void Multiple_requests_generate_distinct_factories()
    {
        var result = Run(Request(Root + "Resources.axaml", "First") +
            Request(Root + "Styles.axaml", "Second"));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Sources.Length);
    }

    [Fact]
    public void The_same_resource_can_have_two_distinct_factory_names()
    {
        var result = Run(Request(Root + "Resources.axaml", "First") +
            Request(Root + "Resources.axaml", "Second"));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Sources.Length);
        Assert.Empty(result.Output.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Attribute_preserves_the_request()
    {
        var attribute = new GenerateXamlResourceAttribute(Root + "Resources.axaml", "Resources");
        Assert.Equal(Root + "Resources.axaml", attribute.Source);
        Assert.Equal("Resources", attribute.FactoryName);
    }

    [Theory]
    [InlineData("null, \"GeneratedResources\"")]
    [InlineData("\"avares://Missing/Resources.axaml\", null")]
    public void Null_attribute_arguments_report_an_error(string arguments)
    {
        AssertDiagnostic(Run($"[assembly: SVCSystems.Avalonia.CSharpUI.GenerateXamlResource({arguments})]"),
            "SVCUIGEN004");
    }

    private static void AssertDiagnostic(Result result, string id)
    {
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(id, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.True(diagnostic.Location.IsInSource);
        Assert.Empty(result.Sources);
    }

    private static string Request(string uri, string name = "GeneratedResources") =>
        $"[assembly: SVCSystems.Avalonia.CSharpUI.GenerateXamlResource({SymbolDisplay.FormatLiteral(uri, true)}, {SymbolDisplay.FormatLiteral(name, true)})]\n";

    private static Result Run(string source, string framework = ".NETCoreApp,Version=v10.0",
        string? additionalSource = null)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(
                $"[assembly: System.Runtime.Versioning.TargetFramework(\"{framework}\")]\n" + source,
                parseOptions, path: "Request.cs")
        };
        if (additionalSource is not null)
            trees.Add(CSharpSyntaxTree.ParseText(additionalSource, parseOptions, path: "Additional.cs"));
        var compilation = CSharpCompilation.Create("XamlResourceConsumer",
            trees,
            References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new XamlResourceGenerator().AsSourceGenerator()], parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output,
            out var diagnostics, TestContext.Current.CancellationToken);
        return new Result(output, diagnostics,
            [.. driver.GetRunResult().Results.SelectMany(r => r.GeneratedSources)
                .Select(s => s.SourceText.ToString())]);
    }

    private static ImmutableArray<MetadataReference> CreateReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("The runtime did not provide trusted platform assemblies.");
        return [.. trusted.Split(Path.PathSeparator)
            .Append(typeof(GenerateXamlResourceAttribute).Assembly.Location)
            .Append(typeof(FixtureService).Assembly.Location)
            .Where(p => !StringComparer.OrdinalIgnoreCase.Equals(p, typeof(XamlResourceGenerationTests).Assembly.Location))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => MetadataReference.CreateFromFile(p))];
    }

    private sealed record Result(Compilation Output, ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<string> Sources);
}
