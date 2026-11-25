using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SVCSystems.Avalonia.CSharpUI.Analyzers.Tests;

public sealed class EventSubscriptionInBuildMethodAnalyzerTests
{
    private const string ViewBaseSource = """
        namespace SVCSystems.Avalonia.CSharpUI
        {
            public class ViewBase
            {
                protected virtual object Build() => new object();
            }
        }
        """;

    [Fact]
    public void RuleIsAWarningInTheExpectedCategory()
    {
        var descriptor = Assert.Single(new EventSubscriptionInBuildMethodAnalyzer().SupportedDiagnostics);

        Assert.Equal("SVCUI003", descriptor.Id);
        Assert.Equal(DiagnosticSeverity.Warning, descriptor.DefaultSeverity);
        Assert.Equal(EventSubscriptionInBuildMethodAnalyzer.Category, descriptor.Category);
    }

    [Fact]
    public async Task ReportsEventSubscriptionInsideBuildAsync()
    {
        var test = CreateAnalyzerTest();
        test.TestState.Sources.Add("""
            namespace App
            {
                public sealed class MyView : SVCSystems.Avalonia.CSharpUI.ViewBase
                {
                    public event System.Action Changed;

                    protected override object Build()
                    {
                        {|#0:Changed += Handle|};
                        return new object();
                    }

                    private void Handle() { }
                }
            }
            """);

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(EventSubscriptionInBuildMethodAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithMessage(
                    "Event 'Changed' is subscribed inside Build(); the handler may be attached again on every rebuild"));

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DoesNotReportEventSubscriptionOutsideBuildAsync()
    {
        var test = CreateAnalyzerTest();
        test.TestState.Sources.Add("""
            namespace App
            {
                public sealed class MyView : SVCSystems.Avalonia.CSharpUI.ViewBase
                {
                    public event System.Action Changed;

                    public void Subscribe() => Changed += Handle;

                    private void Handle() { }
                }
            }
            """);

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    // Near miss: `+=` on a non-event member in Build must not trip the rule.
    [Fact]
    public async Task DoesNotReportAddAssignmentOnNonEventAsync()
    {
        var test = CreateAnalyzerTest();
        test.TestState.Sources.Add("""
            namespace App
            {
                public sealed class MyView : SVCSystems.Avalonia.CSharpUI.ViewBase
                {
                    private int _count;

                    protected override object Build()
                    {
                        _count += 1;
                        return new object();
                    }
                }
            }
            """);

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DoesNotReportInNonViewClassAsync()
    {
        var test = CreateAnalyzerTest();
        test.TestState.Sources.Add("""
            namespace App
            {
                public sealed class NotAView
                {
                    public event System.Action Changed;

                    protected object Build()
                    {
                        Changed += Handle;
                        return new object();
                    }

                    private void Handle() { }
                }
            }
            """);

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    private static CSharpAnalyzerTest<EventSubscriptionInBuildMethodAnalyzer, DefaultVerifier> CreateAnalyzerTest()
    {
        var test = new CSharpAnalyzerTest<EventSubscriptionInBuildMethodAnalyzer, DefaultVerifier>
        {
            CompilerDiagnostics = CompilerDiagnostics.None,
        };
        test.TestState.Sources.Add(ViewBaseSource);
        return test;
    }
}
