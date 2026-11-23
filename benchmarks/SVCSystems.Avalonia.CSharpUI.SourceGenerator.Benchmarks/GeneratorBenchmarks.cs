using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.InternalGenerators;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Benchmarks;

/// <summary>
/// Measures the generator the way it is used in practice: a cold build, and the incremental
/// re-runs triggered by an IDE keystroke. MemoryDiagnoser is essential here - source generators
/// that allocate heavily on every keystroke cause GC pressure in the compiler process.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class GeneratorBenchmarks
{
    private GeneratorDriver _driver = null!;
    private int _editIndex;
    private int _variantIndex;

    [GlobalSetup]
    public void Setup()
    {
        // Force one-time construction of the compilation and metadata references outside the timers.
        _ = BenchmarkWorkload.Base;
        _ = BenchmarkWorkload.IrrelevantEdit;
        _ = BenchmarkWorkload.NameEdit;
        _ = BenchmarkWorkload.ColdVariants;
        _ = BenchmarkWorkload.NameVariants;
        _driver = CreateDriver().RunGenerators(BenchmarkWorkload.Base);
        _editIndex = 0;
    }

    // ---------- Cold start (fresh generator driver on every invocation) ----------

    [Benchmark(Description = "Cold: full generator, 40 views")]
    public GeneratorDriverRunResult Cold_FullGenerator() => Run(CreateDriver(), NextColdVariant());

    [Benchmark(Description = "Cold: usage-driven markup generator only")]
    public GeneratorDriverRunResult Cold_UsageDriven() =>
        Run(CreateDriver(new UsageAdapter()), NextColdVariant());

    [Benchmark(Description = "Cold: Avalonia property extensions only")]
    public GeneratorDriverRunResult Cold_PropertyExtensions() =>
        Run(CreateDriver(new AvaloniaPropertyExtensionsGenerator()), NextColdVariant());

    [Benchmark(Description = "Cold: Avalonia event extensions only")]
    public GeneratorDriverRunResult Cold_EventExtensions() =>
        Run(CreateDriver(new AvaloniaEventExtensionsGenerator()), NextColdVariant());

    // ---------- Incremental (reuse driver, as Roslyn does in the IDE) ----------

    [Benchmark(Description = "Incremental: edit that changes no simple name")]
    public GeneratorDriverRunResult Incremental_IrrelevantEdit()
    {
        var compilation = (_editIndex++ & 1) == 0
            ? BenchmarkWorkload.IrrelevantEdit
            : BenchmarkWorkload.Base;
        _driver = _driver.RunGenerators(compilation);
        return _driver.GetRunResult();
    }

    [Benchmark(Description = "Incremental: edit that adds a control name")]
    public GeneratorDriverRunResult Incremental_NameEdit()
    {
        _driver = _driver.RunGenerators(NextNameVariant());
        return _driver.GetRunResult();
    }

    private Compilation NextColdVariant() =>
        BenchmarkWorkload.ColdVariants[_variantIndex++ % BenchmarkWorkload.ColdVariants.Length];

    private Compilation NextNameVariant() =>
        BenchmarkWorkload.NameVariants[_variantIndex++ % BenchmarkWorkload.NameVariants.Length];

    private static GeneratorDriverRunResult Run(GeneratorDriver driver, Compilation compilation)
    {
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult();
    }

    private static GeneratorDriver CreateDriver(params IIncrementalGenerator[] generators)
    {
        if (generators.Length == 0)
        {
            generators = [new Generator()];
        }

        return CSharpGeneratorDriver.Create(
            generators.Select(static generator => generator.AsSourceGenerator()),
            parseOptions: BenchmarkWorkload.ParseOptions);
    }

    /// <summary>UsageDrivenMarkupGenerator exposes Initialize directly but is not marked as a generator.</summary>
    private sealed class UsageAdapter : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context) =>
            new UsageDrivenMarkupGenerator().Initialize(context);
    }
}
