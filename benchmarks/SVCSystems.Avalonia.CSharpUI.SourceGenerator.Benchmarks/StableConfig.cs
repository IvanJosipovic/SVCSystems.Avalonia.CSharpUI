using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Benchmarks;

/// <summary>More iterations than ShortRun so noisy generator-level timings converge.</summary>
public sealed class StableConfig : ManualConfig
{
    public StableConfig()
    {
        AddJob(Job.ShortRun
            .WithWarmupCount(5)
            .WithIterationCount(10)
            .WithLaunchCount(1));
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}
