using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.Analysis;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.InternalGenerators;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.Usage;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Benchmarks;

/// <summary>
/// Measures the individual pipeline operations, so a regression can be attributed to a single
/// stage instead of only showing up as "generator got slower".
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class HotPathBenchmarks
{
    private static readonly ImmutableArray<string> UsageNames =
        ["Button", "TextBlock", "TextBox", "StackPanel", "Grid", "Border", "CheckBox", "ComboBox", "Path"];

    private Compilation _compilation = null!;
    private ControlCatalog _catalog = null!;
    private UsageResult _usage = null!;
    private INamedTypeSymbol _button = null!;
    private ControlModel _buttonModel = null!;
    private int _variantIndex;

    [GlobalSetup]
    public void Setup()
    {
        _compilation = BenchmarkWorkload.Base;
        _catalog = ControlCatalog.Build(_compilation);
        _usage = UsageResolver.Resolve(_catalog, UsageNames);
        _button = _compilation.GetTypeByMetadataName("Avalonia.Controls.Button") as INamedTypeSymbol
                  ?? throw new InvalidOperationException("Avalonia.Controls.Button was not found.");
        _buttonModel = ControlModelBuilder.Build(_button, _compilation.Assembly);
        _ = BenchmarkWorkload.ColdVariants;
    }

    // ---------- Analysis stages ----------

    [Benchmark(Description = "Stage: build control catalog (cached, real path)")]
    public object ControlCatalog_BuildCached()
    {
        var compilation = NextVariant();
        return ControlCatalog.Build(compilation, default, GenerationCache.GetReferenceHash(compilation));
    }

    [Benchmark(Description = "Stage: build control catalog (uncached)")]
    public object ControlCatalog_BuildUncached() => ControlCatalog.Build(NextVariant());

    [Benchmark(Description = "Stage: resolve usage set from catalog")]
    public object UsageResolver_Resolve() => UsageResolver.Resolve(_catalog, UsageNames);

    [Benchmark(Description = "Stage: read publishing metadata (cached, real path)")]
    public object ExtensionPublishing_ReadCached() =>
        ExtensionPublishing.Read(_compilation, default, GenerationCache.GetReferenceHash(_compilation));

    [Benchmark(Description = "Stage: read publishing metadata (uncached)")]
    public object ExtensionPublishing_ReadUncached() => ExtensionPublishing.Read(_compilation);

    [Benchmark(Description = "Stage: build one control model (process cache, real path)")]
    public object ControlModelBuilder_BuildCached() =>
        ControlModelBuilder.Build(_button, _compilation.Assembly);

    [Benchmark(Description = "Stage: build one control model (uncached, per-call cache)")]
    public object ControlModelBuilder_BuildUncached()
    {
        var cache = new Dictionary<INamedTypeSymbol, ControlModel>(SymbolEqualityComparer.Default);
        return ControlModelBuilder.Build(_button, _compilation.Assembly, cache);
    }

    [Benchmark(Description = "Stage: compute generation fingerprint")]
    public object GenerationFingerprint_Create() =>
        GenerationFingerprint.Create(UsageNames, _compilation, default);

    // ---------- Emit stages ----------

    [Benchmark(Description = "Stage: generate one control's extensions")]
    public string? GenerateExtensions() =>
        new ExternalGeneratorHost().GenerateExtensions(_buttonModel, _compilation, publish: false, suppressOverlappingSetters: true);

    // ---------- Individual helpers (hot micro-operations) ----------

    [Benchmark(Description = "Helper: IsOrInheritsFrom (Button -> AvaloniaObject)")]
    public bool IsOrInheritsFrom() => _button.IsOrInheritsFrom("Avalonia.AvaloniaObject");

    [Benchmark(Description = "Helper: GetFullTypeName (cached)")]
    public string GetFullTypeName() => _button.GetFullTypeName();

    [Benchmark(Description = "Helper: CleanIdentifier (cached)")]
    public string? CleanIdentifier() => SymbolUtilities.CleanIdentifier("Grid");

    [Benchmark(Description = "Helper: RemoveIllegalFileNameCharacters")]
    public string RemoveIllegalFileNameCharacters() =>
        SymbolUtilities.RemoveIllegalFileNameCharacters("Avalonia.Controls.Grid`1[[System.Int32]]");

    [Benchmark(Description = "Helper: FormatXmlDoc")]
    public string FormatXmlDoc() => SymbolUtilities.FormatXmlDoc(_button, _compilation);

    [Benchmark(Description = "Helper: usage key + text")]
    public string TypeKeyToString() => _usage.Keys[0].ToString();

    private Compilation NextVariant() =>
        BenchmarkWorkload.ColdVariants[_variantIndex++ % BenchmarkWorkload.ColdVariants.Length];
}
