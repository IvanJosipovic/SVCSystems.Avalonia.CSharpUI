using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SVCSystems.Avalonia.CSharpUI.Analyzers.Tests;

public sealed class MistypedGeneratedFactoryNameAnalyzerTests
{
    private const string FactoriesSource = """
        namespace SVCSystems.Avalonia.CSharpUI
        {
            internal static class GeneratedControlFactories
            {
                public static object Button() => new object();
                public static object TextBlock() => new object();
                public static object ListBox() => new object();
            }
        }
        """;

    [Fact]
    public void RuleIsAWarningInTheExpectedCategory()
    {
        var descriptor = Assert.Single(new MistypedGeneratedFactoryNameAnalyzer().SupportedDiagnostics);

        Assert.Equal("SVCUI002", descriptor.Id);
        Assert.Equal(DiagnosticSeverity.Warning, descriptor.DefaultSeverity);
        Assert.Equal(MistypedGeneratedFactoryNameAnalyzer.Category, descriptor.Category);
    }

    [Fact]
    public async Task ReportsSingleNearMatchAsync()
    {
        var test = CreateAnalyzerTest();
        test.TestState.Sources.Add("""
            namespace App
            {
                public class Usage
                {
                    public object M() => {|#0:Buton|}();
                }
            }
            """);
        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(MistypedGeneratedFactoryNameAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithMessage("'Buton' is not a known name; did you mean the generated control factory 'Button'?"));

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DoesNotReportWhenNameBindsAsync()
    {
        var test = CreateAnalyzerTest();
        test.TestState.Sources.Add("""
            namespace App
            {
                using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;

                public class Usage
                {
                    public object M() => Button();
                }
            }
            """);

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DoesNotReportForLowercaseOrShortNamesAsync()
    {
        var test = CreateAnalyzerTest();
        test.TestState.Sources.Add("""
            namespace App
            {
                public class Usage
                {
                    public object M() => button();
                    public object N() => But();
                }
            }
            """);

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    // Jaro-Winkler's shared-prefix inflation makes `List` look close to `ListBox`; the length gate
    // must defeat it. This is the near-miss that catches an over-eager similarity threshold.
    [Fact]
    public async Task DoesNotReportWhenLengthDeltaExceedsTwoAsync()
    {
        var test = CreateAnalyzerTest();
        test.TestState.Sources.Add("""
            namespace App
            {
                public class Usage
                {
                    public object M() => List();
                }
            }
            """);

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    // Member accesses are excluded from this rule: they may be a real static call whose receiver
    // happens to contain a mistyped name, and rewriting only the right-hand side would be wrong.
    [Fact]
    public async Task DoesNotReportForMemberAccessAsync()
    {
        var test = CreateAnalyzerTest();
        test.TestState.Sources.Add("""
            namespace App
            {
                public class Usage
                {
                    public object M() => Buton.Make();
                }
            }
            """);

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFixQualifiesTheFactoryAsync()
    {
        var test = new CSharpCodeFixTest<
            MistypedGeneratedFactoryNameAnalyzer,
            QualifyGeneratedFactoryNameCodeFixProvider,
            DefaultVerifier>
        {
            CompilerDiagnostics = CompilerDiagnostics.None,
        };
        test.TestState.Sources.Add(FactoriesSource);
        test.TestState.Sources.Add("""
            namespace App
            {
                public class Usage
                {
                    public object M() => {|SVCUI002:Buton|}();
                }
            }
            """);

        test.FixedState.Sources.Add(FactoriesSource);
        test.FixedState.Sources.Add("""
            namespace App
            {
                public class Usage
                {
                    public object M() => SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories.Button();
                }
            }
            """);

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    private static CSharpAnalyzerTest<MistypedGeneratedFactoryNameAnalyzer, DefaultVerifier> CreateAnalyzerTest()
    {
        var test = new CSharpAnalyzerTest<MistypedGeneratedFactoryNameAnalyzer, DefaultVerifier>
        {
            // The diagnostic co-occurs with CS0103.
            CompilerDiagnostics = CompilerDiagnostics.None,
        };
        test.TestState.Sources.Add(FactoriesSource);
        return test;
    }
}
