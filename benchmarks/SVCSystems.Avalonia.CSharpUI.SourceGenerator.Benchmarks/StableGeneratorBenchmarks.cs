using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.InternalGenerators;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Benchmarks;

/// <summary>Low-noise measurements of the paths whose timings previously looked volatile.</summary>
[Config(typeof(StableConfig))]
public class StableGeneratorBenchmarks
{
    private GeneratorDriver _driver = null!;
    private int _variantIndex;

    [GlobalSetup]
    public void Setup()
    {
        _ = BenchmarkWorkload.ColdVariants;
        _ = BenchmarkWorkload.NameVariants;
        _driver = CreateDriver().RunGenerators(BenchmarkWorkload.Base);
    }

    [Benchmark(Description = "Stable: cold full generator")]
    public GeneratorDriverRunResult Cold_FullGenerator()
    {
        var driver = CreateDriver();
        driver = driver.RunGenerators(BenchmarkWorkload.ColdVariants[_variantIndex++ % 8]);
        return driver.GetRunResult();
    }

    [Benchmark(Description = "Stable: incremental identical compilation")]
    public GeneratorDriverRunResult Incremental_Identical()
    {
        _driver = _driver.RunGenerators(BenchmarkWorkload.Base);
        return _driver.GetRunResult();
    }

    [Benchmark(Description = "Stable: incremental comment-only edit")]
    public GeneratorDriverRunResult Incremental_CommentEdit()
    {
        _driver = _driver.RunGenerators(BenchmarkWorkload.IrrelevantEdit);
        return _driver.GetRunResult();
    }

    [Benchmark(Description = "Stable: incremental code edit (new control name)")]
    public GeneratorDriverRunResult Incremental_NameEdit()
    {
        _driver = _driver.RunGenerators(BenchmarkWorkload.NameVariants[_variantIndex++ % 8]);
        return _driver.GetRunResult();
    }

    private static GeneratorDriver CreateDriver() =>
        CSharpGeneratorDriver.Create(
            [new Generator().AsSourceGenerator()],
            parseOptions: BenchmarkWorkload.ParseOptions);
}
