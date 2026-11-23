using BenchmarkDotNet.Running;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.InternalGenerators;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Benchmarks;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--profile", StringComparison.OrdinalIgnoreCase))
        {
            Profile();
            return 0;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }

    /// <summary>
    /// Step-level profiler. Registers each generator separately (with Roslyn's tracked-step
    /// support) so the report shows exactly which generator and which pipeline stage costs what,
    /// and what survives compilation edits through the incremental cache.
    /// </summary>
    private static void Profile()
    {
        Console.WriteLine("Workload: 40 views, 24 controls each, " +
                          $"{BenchmarkWorkload.Base.SyntaxTrees.Count()} syntax trees, " +
                          $"{BenchmarkWorkload.References.Length} references");

        (string Label, Compilation Compilation)[] runs =
        [
            ("COLD", BenchmarkWorkload.Base),
            ("IDENTICAL COMPILATION (ideal cache)", BenchmarkWorkload.Base),
            ("IRRELEVANT EDIT (comment only)", BenchmarkWorkload.IrrelevantEdit),
            ("NAME-ADDING EDIT", BenchmarkWorkload.NameEdit)
        ];

        var driver = CreateTrackingDriver();
        foreach (var (label, compilation) in runs)
        {
            Console.WriteLine($"\n=== {label} ===");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var newDriver = driver.RunGenerators(compilation);
            var runMs = stopwatch.Elapsed.TotalMilliseconds;
            var runResult = newDriver.GetRunResult();
            Console.WriteLine($"  RunGenerators: {runMs:F3} ms; GetRunResult: " +
                              $"{stopwatch.Elapsed.TotalMilliseconds - runMs:F3} ms");
            driver = newDriver;
            PrintResults(runResult, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static GeneratorDriver CreateTrackingDriver() =>
        CSharpGeneratorDriver.Create(
        [
            new UsageAdapter().AsSourceGenerator(),
            new AvaloniaPropertyExtensionsGenerator().AsSourceGenerator(),
            new AvaloniaEventExtensionsGenerator().AsSourceGenerator(),
            new XamlResourceGenerator().AsSourceGenerator()
        ],
            parseOptions: BenchmarkWorkload.ParseOptions,
            driverOptions: new GeneratorDriverOptions(trackIncrementalGeneratorSteps: true));

    private static void PrintResults(GeneratorDriverRunResult runResult, double elapsedMs)
    {
        Console.WriteLine($"  TOTAL generator time: {elapsedMs:F3} ms");

        foreach (var result in runResult.Results)
        {
            var name = result.Generator?.GetType().Name ?? "<unknown>";
            Console.WriteLine($"  {name}: {result.GeneratedSources.Length} sources, " +
                              $"{result.Diagnostics.Length} diagnostics, " +
                              $"{result.TrackedSteps.Count} step names");

            if (result.Exception is not null)
            {
                Console.WriteLine($"    EXCEPTION: {result.Exception}");
            }

            var steps = result.TrackedSteps
                .SelectMany(static pair => pair.Value.Select(step => (Name: pair.Key, Step: step)))
                .GroupBy(static step => step.Name, StringComparer.Ordinal)
                .Select(static group => (
                    Name: group.Key,
                    Count: group.Count(),
                    Time: group.Sum(static step => step.Step.ElapsedTime.TotalMilliseconds),
                    Outputs: group.Sum(static step => step.Step.Outputs.Length)))
                .OrderByDescending(static step => step.Time)
                .Take(12);

            foreach (var step in steps)
            {
                Console.WriteLine($"    {step.Time,9:F3} ms  {step.Count,4} runs  " +
                                  $"{step.Outputs,5} outputs  {Truncate(step.Name, 70)}");
            }
        }
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "...";

    private sealed class UsageAdapter : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context) =>
            new UsageDrivenMarkupGenerator().Initialize(context);
    }
}
