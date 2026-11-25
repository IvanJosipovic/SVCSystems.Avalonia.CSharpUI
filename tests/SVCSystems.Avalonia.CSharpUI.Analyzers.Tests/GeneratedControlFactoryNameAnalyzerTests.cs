using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SVCSystems.Avalonia.CSharpUI.Analyzers.Tests;

public sealed class GeneratedControlFactoryNameAnalyzerTests
{
    private const string FactorySource = """
        namespace SVCSystems.Avalonia.CSharpUI
        {
            internal static class GeneratedControlFactories
            {
                public static object Path() => new object();
            }
        }
        """;

    private const string TypeSource = """
        namespace Types
        {
            public static class Path
            {
                public static string Combine(string left, string right) => left + right;
            }
        }
        """;

    // Forces the simplifier to keep global:: after the fix: a bare Types.Path would otherwise
    // bind to this same-named namespace nested inside App.
    private const string ShadowingNamespaceSource = """
        namespace App.Types
        {
            public class Marker { }
        }
        """;

    [Fact]
    public void RuleIsAWarningInTheExpectedCategory()
    {
        var descriptor = Assert.Single(new GeneratedControlFactoryNameAnalyzer().SupportedDiagnostics);

        Assert.Equal("SVCUI001", descriptor.Id);
        Assert.Equal(DiagnosticSeverity.Warning, descriptor.DefaultSeverity);
        Assert.Equal(GeneratedControlFactoryNameAnalyzer.Category, descriptor.Category);
    }

    [Fact]
    public async Task ReportsAtIdentifierWhenFactoryShadowsTypeInMemberAccessAsync()
    {
        var test = CreateAnalyzerTest();
        test.TestState.Sources.Add("""
            namespace App
            {
                using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;
                using Types;

                public class Usage
                {
                    public string M() => Path.Combine("a", "b");
                }
            }
            """);
        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(GeneratedControlFactoryNameAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithSpan("/0/Test2.cs", 8, 30, 8, 34)
                .WithMessage(
                    "'Path' binds to a generated control factory method instead of the type 'Types.Path'; "
                    + "qualify the type with 'global::'"));

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DoesNotReportWhenIdentifierBindsToRealTypeAsync()
    {
        var test = CreateAnalyzerTest();
        test.TestState.Sources.Add("""
            namespace App
            {
                using Types;

                public class Usage
                {
                    public string M() => Path.Combine("a", "b");
                }
            }
            """);

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DoesNotReportWhenFactoryUsedAsMethodCallAsync()
    {
        var test = CreateAnalyzerTest();
        test.TestState.Sources.Add("""
            namespace App
            {
                using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;
                using Types;

                public class Usage
                {
                    public object M() => Path();
                }
            }
            """);

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DoesNotReportWhenNoCompetingTypeIsInScopeAsync()
    {
        var test = CreateAnalyzerTest();
        test.TestState.Sources.Add("""
            namespace App
            {
                using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;

                public class Usage
                {
                    public string M() => Path.Combine("a", "b");
                }
            }
            """);

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CodeFixQualifiesTheTypeWithGlobalAsync()
    {
        var test = new CSharpCodeFixTest<
            GeneratedControlFactoryNameAnalyzer,
            QualifyControlFactoryTypeCodeFixProvider,
            DefaultVerifier>
        {
            // The pre-fix source intentionally produces CS0119: the factory method group is not
            // valid as the left side of a member access. That is exactly what the fix repairs.
            CompilerDiagnostics = CompilerDiagnostics.None,
        };
        AddSharedSources(test.TestState.Sources);
        test.TestState.Sources.Add(ShadowingNamespaceSource);
        test.TestState.Sources.Add("""
            namespace App
            {
                using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;
                using global::Types;

                public class Usage
                {
                    public string M() => {|SVCUI001:Path|}.Combine("a", "b");
                }
            }
            """);

        AddSharedSources(test.FixedState.Sources);
        test.FixedState.Sources.Add(ShadowingNamespaceSource);
        test.FixedState.Sources.Add("""
            namespace App
            {
                using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;
                using global::Types;

                public class Usage
                {
                    public string M() => global::Types.Path.Combine("a", "b");
                }
            }
            """);

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    private static CSharpAnalyzerTest<GeneratedControlFactoryNameAnalyzer, DefaultVerifier> CreateAnalyzerTest()
    {
        var test = new CSharpAnalyzerTest<GeneratedControlFactoryNameAnalyzer, DefaultVerifier>
        {
            CompilerDiagnostics = CompilerDiagnostics.None,
        };
        AddSharedSources(test.TestState.Sources);
        return test;
    }

    private static void AddSharedSources(SourceFileList sources)
    {
        sources.Add(FactorySource);
        sources.Add(TypeSource);
    }
}
